"""One-time SSH installation of precompiled server components into an existing Compose project."""
import copy
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import signal
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.request
import uuid

SERVICES = ('api', 'worker', 'notifier')
ADMIN = '/var/lib/portkill/admin'


def local(path):
    path = Path(path).absolute()
    if any(parent.is_symlink() for parent in (path, *path.parents)):
        raise ValueError('部署路径不能经过符号链接：' + str(path))
    return path


def write_json(path, value, mode=0o600):
    path = local(path)
    temporary = path.with_name(path.name + '.' + uuid.uuid4().hex + '.tmp')
    try:
        temporary.write_text(json.dumps(value, ensure_ascii=False, separators=(',', ':')), encoding='utf-8')
        temporary.chmod(mode)
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def compose_text(config):
    # docker compose config escapes dollars even in JSON; our internal model holds actual values.
    return json.dumps(config, ensure_ascii=False).replace('$', '$$')


def check_space(requirements, reserve=1024**3):
    disks = {}
    for path, size in requirements:
        key = os.stat(path).st_dev
        previous, free = disks.get(key, (0, shutil.disk_usage(path).free))
        disks[key] = previous + size, min(free, shutil.disk_usage(path).free)
    if any(free < size + reserve for size, free in disks.values()):
        raise ValueError('磁盘空间不足，已在镜像构建和数据库备份前停止。')


def tree_bytes(path):
    return sum(file.stat().st_size for file in Path(path).rglob('*') if file.is_file())


def socket_group():
    # /var/run is normally the OS-managed symlink to /run on Ubuntu.
    return str(Path('/var/run/docker.sock').stat().st_gid)


def verify(root, stage):
    payload = (stage / 'install-manifest.json').read_bytes()
    manifest = json.loads(payload)
    if manifest.get('schema') != 1 or manifest.get('kind') != 'portkill-in-place-install' or manifest.get('runtime') != 'linux-x64':
        raise ValueError('部署包格式不兼容。')
    public = json.loads((root / 'verification.json').read_text(encoding='utf-8-sig'))
    key = next(value for name, value in public.items() if name.lower() == 'publickeypem')
    with tempfile.TemporaryDirectory(prefix='portkill-install-verify-') as folder:
        pem = Path(folder) / 'public.pem'
        pem.write_text(key, encoding='ascii')
        subprocess.run(['openssl', 'dgst', '-sha256', '-verify', str(pem), '-signature', str(stage / 'install-manifest.sig'),
                        str(stage / 'install-manifest.json')], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    for item in manifest['files']:
        name = item['path']
        if not re.fullmatch(r'[A-Za-z0-9._/-]+', name) or any(part in ('', '.', '..') for part in name.split('/')):
            raise ValueError('部署包文件路径不安全。')
        file = local(root / name)
        if not file.is_file() or file.stat().st_size != item['bytes'] or hashlib.sha256(file.read_bytes()).hexdigest() != item['sha256']:
            raise ValueError('部署文件摘要不符：' + name)
    for name in ('applicationImage', 'runnerImage'):
        repository = 'portkill-update-server' if name == 'applicationImage' else 'portkill-server-update-runner'
        if not re.fullmatch(repository + r':install-[a-f0-9]{16}', manifest[name]):
            raise ValueError('镜像标签无效。')
    return manifest


class Installer:
    def __init__(self, root, project, *, revoke_sessions=False, enable_updates=True):
        self.root, self.project = local(root), project
        if not re.fullmatch(r'[a-z0-9][a-z0-9_-]{0,62}', project) or str(root) == '/':
            raise ValueError('部署目录或 Compose 项目名无效。')
        self.stage = local(self.root / '.portkill-server-install')
        self.admin = local(self.root / 'data/admin')
        self.web = local(self.root / 'server-bundle/web')
        self.shared = local(self.admin / 'server-updates')
        self.pending = self.shared / 'pending'
        self.backup = None
        self.log = None
        self.control = self.publication = None
        self.switched = False
        self.owns_pending = False
        self.revoke_sessions = revoke_sessions
        self.enable_updates = enable_updates

    def extra_state(self):
        return {}

    def verify_package(self):
        return verify(self.root, self.stage)

    def verification_description(self):
        return '签名、文件摘要、运行项目及剩余空间检查通过。'

    def finalize(self):
        pass

    def rollback_extra(self):
        pass

    def succeeded(self, state):
        pass

    def output(self, text):
        text = re.sub(r'(?i)(password|token|secret|authorization|key)\s*[:=]\s*\S+', r'\1=[redacted]', text)
        print(text, flush=True)
        if self.log:
            self.log.write(text + '\n')
            self.log.flush()

    def docker(self, *args, stream=False):
        if not stream:
            process = subprocess.run(['docker', *map(str, args)], capture_output=True, text=True, encoding='utf-8', cwd=self.root)
            if process.returncode:
                # inspect/config can contain credentials. Never forward their output.
                raise RuntimeError('Docker 操作失败：' + str(args[0]) + '；请检查本机 Docker 状态。')
            return process.stdout.strip()
        process = subprocess.Popen(['docker', *map(str, args)], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding='utf-8', cwd=self.root)
        try:
            for line in process.stdout:
                self.output(line.rstrip())
            if process.wait():
                raise RuntimeError('Docker 构建或维护操作失败。')
            return ''
        finally:
            if process.poll() is None:
                process.terminate()
            process.stdout.close()

    def compose(self, file=None):
        return ['compose', '--project-directory', str(self.root), '--env-file', str(self.root / '.env') if file is None else '/dev/null',
                '-f', str(file or self.root / 'docker-compose.yml'), '-p', self.project]

    def lock(self):
        if self.control is not None:
            return
        import fcntl
        for path in (self.shared, self.shared / 'packages', self.shared / 'tasks'):
            local(path).mkdir(exist_ok=True)
            os.chown(path, 10001, 10001)
            path.chmod(0o2770)
        control = local(self.shared / 'control.lock')
        self.control = control.open('a+')
        os.chown(control, 10001, 10001)
        control.chmod(0o660)
        fcntl.flock(self.control, fcntl.LOCK_EX | fcntl.LOCK_NB)

    def drain(self):
        import fcntl
        lock = local(self.admin / 'publisher-execution.lock')
        self.publication = lock.open('a+')
        os.chown(lock, 10001, 10001)
        lock.chmod(0o660)
        deadline = time.monotonic() + 600
        self.output('等待正在执行的桌面发布完成（最多 10 分钟）……')
        while True:
            try:
                fcntl.flock(self.publication, fcntl.LOCK_EX | fcntl.LOCK_NB)
                return
            except BlockingIOError:
                if time.monotonic() > deadline:
                    raise RuntimeError('桌面发布仍未完成，本次安装已停止，旧服务未切换。')
                time.sleep(1)

    def containers(self):
        result = {}
        for service in (*SERVICES, 'caddy'):
            container = self.docker('ps', '-aq', '--filter', 'label=com.docker.compose.project=' + self.project,
                                    '--filter', 'label=com.docker.compose.service=' + service)
            if not container or '\n' in container:
                raise ValueError('目标服务缺失或不唯一：' + service)
            info = json.loads(self.docker('inspect', container))[0]
            labels = info['Config']['Labels']
            if not info['State']['Running'] or labels.get('com.docker.compose.project.working_dir') != str(self.root):
                raise ValueError('服务目录或运行状态不匹配：' + service)
            result[service] = info
        expected = {('api', ADMIN): self.root / 'data/admin', ('worker', ADMIN): self.root / 'data/admin',
                    ('caddy', '/srv/web'): self.web}
        for (service, destination), source in expected.items():
            if not any(mount.get('Type') == 'bind' and mount['Destination'] == destination and Path(mount['Source']) == source for mount in result[service]['Mounts']):
                raise ValueError('持久目录挂载不符：' + service + ' ' + destination)
        return result

    def config(self, containers):
        # Existing containers must match the effective configuration, not just the file path.
        value = self.docker(*self.compose(), 'config', '--format', 'json')
        hashes = dict(line.split() for line in self.docker(*self.compose(), 'config', '--hash', '*').splitlines())
        for service, info in containers.items():
            if hashes.get(service) != info['Config']['Labels'].get('com.docker.compose.config-hash'):
                raise ValueError('Compose 与正在运行的容器不一致，请先核对配置：' + service)
        return json.loads(value.replace('$$', '$'))

    def save_compose(self, file, config):
        local(file).write_text(compose_text(config), encoding='utf-8')
        file.chmod(0o600)

    def copy_web(self, source):
        for file in sorted(source.rglob('*'), key=lambda item: item.name == 'index.html'):
            if not file.is_file():
                continue
            target = local(self.web / file.relative_to(source))
            target.parent.mkdir(parents=True, exist_ok=True)
            temporary = target.with_name(target.name + '.install-tmp')
            shutil.copyfile(file, temporary)
            temporary.chmod(0o644)
            os.replace(temporary, target)
        for file in source.rglob('*'):
            if file.is_file() and hashlib.sha256(file.read_bytes()).digest() != hashlib.sha256((self.web / file.relative_to(source)).read_bytes()).digest():
                raise RuntimeError('Web 文件复制校验失败。')

    def health(self, images, timeout=120):
        deadline = time.monotonic() + timeout
        client = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        while True:
            try:
                containers = self.containers()
                for service in SERVICES:
                    info = containers[service]
                    if info['Image'] != images[service]:
                        raise ValueError('镜像不匹配：' + service)
                    if service == 'worker':
                        continue
                    ip = next(network['IPAddress'] for network in info['NetworkSettings']['Networks'].values() if network.get('IPAddress'))
                    host = next((value[13:].split(';')[0] for value in containers['api']['Config']['Env'] if value.startswith('AllowedHosts=')), 'localhost')
                    url = 'http://' + ip + (':5081/health/ready' if service == 'api' else ':8091/health')
                    with client.open(urllib.request.Request(url, headers={'Host': host}), timeout=5) as response:
                        if service == 'api' and json.load(response).get('database') != 'healthy':
                            raise ValueError('API 数据库未就绪。')
                return
            except Exception:
                if time.monotonic() >= deadline:
                    raise RuntimeError('API、Worker 或 Notifier 健康检查超时。')
                time.sleep(2)

    def apply(self, config):
        self.docker(*self.compose(config), 'up', '-d', '--force-recreate', '--no-deps', '--no-build', '--pull', 'never', *SERVICES, stream=True)

    def rollback(self):
        self.output('正在恢复更新前的配置、应用镜像及 Web；保留当前数据库……')
        saved = json.loads((self.backup / 'state.json').read_text())
        for service in SERVICES:
            self.docker('image', 'tag', saved['images'][service], saved['references'][service])
        shutil.copyfile(self.backup / 'docker-compose.yml', self.root / 'docker-compose.yml')
        with tempfile.TemporaryDirectory(prefix='web-restore-', dir=self.backup) as temporary:
            with tarfile.open(self.backup / 'web.tar.gz', 'r:gz') as archive:
                archive.extractall(temporary, filter='data')
            self.copy_web(Path(temporary))
        self.apply(self.backup / 'compose-rollback.json')
        self.health(saved['images'])
        self.rollback_extra()
        saved['state'] = 'rolledBack'
        write_json(self.backup / 'state.json', saved)
        self.clear_pending(saved['id'])
        self.output('旧服务已恢复，数据库未覆盖。备份：' + str(self.backup))

    def clear_pending(self, identity):
        if self.pending.is_file() and self.pending.read_text() == identity:
            self.pending.unlink()

    def recover(self):
        identity = self.pending.read_text()
        if not re.fullmatch(r'[a-f0-9]{32}', identity):
            raise ValueError('更新屏障不属于有效安装任务。')
        self.backup = local(self.admin / 'server-install-backups' / identity)
        saved = json.loads((self.backup / 'state.json').read_text())
        if saved['project'] != self.project or saved['root'] != str(self.root):
            raise ValueError('恢复记录与当前部署不一致。')
        self.drain()
        if saved['state'] == 'switching':
            self.rollback()
        elif saved['state'] in ('preparing', 'prepared'):
            self.clear_pending(identity)
            self.output('安装尚未切换，已解除准备屏障，可重新执行安装。')
        elif saved['state'] in ('succeeded', 'rolledBack'):
            self.health(saved['targetImages'] if saved['state'] == 'succeeded' else saved['images'])
            if saved['state'] == 'succeeded':
                self.succeeded(saved)
            self.clear_pending(identity)
            self.output('已确认此前安装的最终服务状态，更新屏障已解除。')
        else:
            raise ValueError('该记录无需恢复，请核对 pending 和安装日志。')

    def install(self):
        for file in ('docker-compose.yml', '.env', 'verification.json'):
            if not local(self.root / file).is_file():
                raise ValueError('缺少原部署文件：' + file)
        for directory in (self.admin, self.web):
            if not directory.is_dir():
                raise ValueError('缺少已有持久目录：' + str(directory))
        manifest = self.verify_package()
        self.lock()
        if self.pending.exists():
            raise ValueError('已有更新屏障，禁止重复安装；查看日志后使用 --recover 处理本安装任务。')
        containers = self.containers()
        original = self.config(containers)
        docker_root = local(self.docker('info', '--format', '{{.DockerRootDir}}'))
        check_space([(self.root, tree_bytes(self.stage) * 2 + tree_bytes(self.web) * 3),
                     (self.admin, tree_bytes(self.admin / 'auth-keys') + sum(file.stat().st_size for file in (self.admin / 'admin.sqlite', self.admin / 'admin.sqlite-wal') if file.is_file()) * 2),
                     (docker_root, tree_bytes(self.stage / 'server-bundle') * 6)])
        logs = local(self.root / '.portkill-server-install-logs')
        logs.mkdir(exist_ok=True, mode=0o700)
        if self.log is None:
            self.log = (logs / ('install-' + time.strftime('%Y%m%d-%H%M%S') + '.log')).open('x', encoding='utf-8')
        self.output(self.verification_description() + '日志：' + self.log.name)
        self.output('构建新镜像，现有网站继续运行；首次需下载 Docker 基础镜像……')
        builds = [('Application.Dockerfile', manifest['applicationImage'])]
        if self.enable_updates:
            builds.append(('Runner.Dockerfile', manifest['runnerImage']))
        for dockerfile, image in builds:
            self.docker('build', '--progress', 'plain', '--platform', 'linux/amd64', '-f', self.stage / dockerfile, '-t', image, self.stage, stream=True)
        # A root build/backup does not prove the service account can load .NET.
        # Export uses an in-memory database and touches no live data.
        self.output('以应用账户预检新镜像运行库和程序集；通过后才进入维护阶段……')
        runtime_probe = '''for component in api worker notifier tool; do
    directory=/opt/portkill/$component
    test -r "$directory" && test -x "$directory"
    inaccessible=$(find "$directory" ! -readable -o -type d ! -executable)
    test -z "$inaccessible"
done
for program in api/PortKiller.UpdateAdmin.Api worker/PortKiller.UpdatePublisher.Worker notifier/PortKiller.UpdateNotifier tool/PortKiller.UpdateTool; do
    test -x "/opt/portkill/$program"
done
/opt/portkill/api/PortKiller.UpdateAdmin.Api export-server-schema /tmp/runtime-schema
test -s /tmp/runtime-schema'''
        self.docker('run', '--rm', '--network', 'none', '--user', '10001:10001', '--read-only',
                    '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges:true',
                    '--tmpfs', '/tmp:rw,nosuid,nodev,noexec,size=16m,mode=1777',
                    '--entrypoint', '/bin/sh', manifest['applicationImage'], '-ec', runtime_probe, stream=True)
        # Fail before maintenance if the Docker socket path/group or shared permissions are unusable.
        group = socket_group() if self.enable_updates else None
        for directory in (self.shared, self.shared / 'packages', self.shared / 'tasks'):
            probe = directory / ('.access-' + uuid.uuid4().hex)
            self.docker('run', '--rm', '--network', 'none', '--user', '10001:10001', '--mount', 'type=bind,src=' + str(directory) + ',dst=/probe',
                        '--entrypoint', '/bin/sh', manifest['applicationImage'], '-ec', 'touch /probe/' + probe.name + ' && rm /probe/' + probe.name)
        # Recheck after the potentially long image build. No on-disk configuration changes have occurred yet.
        if self.config(self.containers()) != original:
            raise ValueError('构建期间部署配置发生变化，停止切换。')
        identity = uuid.uuid4().hex
        self.backup = local(self.admin / 'server-install-backups' / identity)
        self.backup.mkdir(parents=True, mode=0o700)
        saved = dict(id=identity, root=str(self.root), project=self.project, state='preparing', images={name: containers[name]['Image'] for name in SERVICES},
                     references={name: containers[name]['Config']['Image'] for name in SERVICES})
        saved.update(self.extra_state())
        write_json(self.backup / 'state.json', saved)
        with self.pending.open('x') as output:
            output.write(identity)
        self.pending.chmod(0o660)
        self.owns_pending = True
        try:
            self.drain()
            self.output('创建 SQLite 在线备份、认证密钥及原配置/Web 备份……')
            shutil.copyfile(self.root / 'docker-compose.yml', self.backup / 'docker-compose.yml')
            shutil.copyfile(self.root / '.env', self.backup / '.env')
            rollback = copy.deepcopy(original)
            for service in SERVICES:
                tag = 'portkill-install-previous:' + identity + '-' + service
                self.docker('image', 'tag', containers[service]['Image'], tag)
            self.save_compose(self.backup / 'compose-rollback.json', rollback)
            with tarfile.open(self.backup / 'web.tar.gz', 'w:gz') as archive:
                archive.add(self.web, arcname='.')
            inside = ADMIN + '/server-install-backups/' + identity + '/database-backup'
            self.docker('run', '--rm', '--network', 'none', '--user', '0:0', '--volumes-from', containers['api']['Id'],
                        '-e', 'Administration__DataRoot=' + ADMIN, '--entrypoint', '/opt/portkill/api/PortKiller.UpdateAdmin.Api',
                        manifest['applicationImage'], 'db', 'backup', '--output', inside, stream=True)
            candidate = copy.deepcopy(original)
            for service in SERVICES:
                candidate['services'][service]['image'] = manifest['applicationImage']
                candidate['services'][service]['build'] = dict(context=str(self.stage), dockerfile='Application.Dockerfile')
            if self.enable_updates:
                api = candidate['services']['api']
                api.setdefault('environment', {}).update(ServerUpdate__Enabled='true', ServerUpdate__DeploymentRoot=str(self.root), ServerUpdate__Project=self.project,
                    ServerUpdate__VerificationConfigPath='/etc/portkill/verification.json', PORTKILL_DOCKER_GID=group)
                volumes = api.setdefault('volumes', [])
                if not any(volume.get('target') == '/var/run/docker.sock' for volume in volumes):
                    volumes.append(dict(type='bind', source='/var/run/docker.sock', target='/var/run/docker.sock'))
                api['group_add'] = sorted(set(map(str, api.get('group_add', []))) | {group})
            self.save_compose(self.backup / 'compose-new.json', candidate)
            self.docker(*self.compose(self.backup / 'compose-new.json'), 'config', '--quiet')
            saved['state'] = 'prepared'
            write_json(self.backup / 'state.json', saved)
            # The saved switching marker precedes all mutations, so --recover can safely roll back a hard interruption.
            saved['state'] = 'switching'
            write_json(self.backup / 'state.json', saved)
            self.switched = True
            if self.revoke_sessions:
                self.output('停止 API/Worker，离线撤销历史登录；升级后需要重新完成 MFA……')
                self.docker(*self.compose(), 'stop', 'api', 'worker', stream=True)
                self.docker('run', '--rm', '--network', 'none', '--user', '10001:10001', '--volumes-from', containers['api']['Id'],
                            '-e', 'Administration__DataRoot=' + ADMIN, '--entrypoint', '/opt/portkill/api/PortKiller.UpdateAdmin.Api',
                            manifest['applicationImage'], 'admin', 'revoke-sessions', stream=True)
            self.output('替换前端并重建 API、Worker、Notifier；Caddy 与 Monitor 保持运行……')
            self.copy_web(self.stage / 'server-bundle/web')
            shutil.copyfile(self.backup / 'compose-new.json', self.root / 'docker-compose.yml')
            (self.root / 'docker-compose.yml').chmod(0o600)
            self.apply(self.root / 'docker-compose.yml')
            image = json.loads(self.docker('image', 'inspect', manifest['applicationImage']))[0]['Id']
            self.health({service: image for service in SERVICES})
            self.finalize()
            # Persist current web inventory for normal update retention, keeping older open pages' assets for now.
            write_json(self.shared / 'web-current.json', [file.relative_to(self.stage / 'server-bundle/web').as_posix()
                for file in (self.stage / 'server-bundle/web').rglob('*') if file.is_file()], 0o660)
            task = self.shared / 'tasks' / identity
            task.mkdir(mode=0o2770)
            os.chown(task, 10001, 10001)
            task.chmod(0o2770)
            write_json(task / 'status.json', dict(id=identity, packageId=identity, version=manifest['version'], actorId='ssh-install', state='succeeded',
                stage='SSH 安装完成', updatedUtcMs=int(time.time() * 1000), error=None, rollbackOf='ssh-install'), 0o660)
            saved['state'] = 'succeeded'
            saved['targetImages'] = {service: image for service in SERVICES}
            write_json(self.backup / 'state.json', saved)
            self.succeeded(saved)
            self.clear_pending(identity)
            self.output('部署完成。请 Ctrl+F5 刷新原管理网址并重新登录。')
            self.output('备份目录：' + str(self.backup))
        except BaseException:
            if self.switched:
                try:
                    self.rollback()
                except BaseException:
                    self.output('自动恢复未完成，保留 pending 和备份。执行原命令并追加 --recover，数据库不要手工覆盖。')
            else:
                self.clear_pending(identity)
            raise
        finally:
            if self.publication:
                self.publication.close()


def main():
    os.umask(0o077)
    installer = Installer(Path(sys.argv[1]), sys.argv[2])
    def interrupted(signum, frame):
        raise KeyboardInterrupt('安装收到停止信号')
    for name in ('SIGTERM', 'SIGHUP'):
        if hasattr(signal, name):
            signal.signal(getattr(signal, name), interrupted)
    try:
        if len(sys.argv) > 3 and sys.argv[3] == '--recover':
            installer.lock()
            installer.recover()
        else:
            installer.install()
        return 0
    except BaseException as error:
        installer.output('安装未完成：' + str(error))
        return 1
    finally:
        for handle in (installer.publication, installer.control, installer.log):
            if handle:
                handle.close()


if __name__ == '__main__':
    raise SystemExit(main())
