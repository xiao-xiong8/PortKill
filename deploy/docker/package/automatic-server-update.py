"""Local root maintenance entry point for the self-checking, self-extracting SSH package."""
import argparse
from contextlib import contextmanager
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import signal
import subprocess
import sys
import uuid
from types import SimpleNamespace

sys.dont_write_bytecode = True


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


core_path = Path(__file__).with_name('install.py')
if not core_path.is_file():
    core_path = Path(__file__).with_name('install-server-in-place.py')
core = load('automatic_install_core', core_path)


def sha(path):
    digest = hashlib.sha256()
    with Path(path).open('rb') as source:
        for block in iter(lambda: source.read(1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()


def verify_package(package):
    package = core.local(package)
    manifest = json.loads((package / 'automatic-manifest.json').read_text(encoding='utf-8'))
    if manifest.get('schema') != 1 or manifest.get('kind') != 'portkill-automatic-install' or manifest.get('runtime') != 'linux-x64':
        raise ValueError('自动升级包格式无效。')
    names = set()
    for item in manifest['files']:
        name = item['path']
        if not re.fullmatch(r'[A-Za-z0-9._/-]+', name) or any(part in ('', '.', '..') for part in name.split('/')) or name in names:
            raise ValueError('自动升级包文件路径无效。')
        names.add(name)
        path = core.local(package / name)
        if not path.is_file() or path.stat().st_size != item['bytes'] or sha(path) != item['sha256']:
            raise ValueError('自动升级包校验失败：' + name)
    actual = {path.relative_to(package).as_posix() for path in package.rglob('*') if path.is_file()}
    if actual != names | {'automatic-manifest.json'}:
        raise ValueError('自动升级包包含未记录的文件。')
    if not re.fullmatch(r'portkill-update-server:install-[a-f0-9]{16}', manifest['applicationImage']):
        raise ValueError('自动升级镜像标签无效。')
    return manifest


def docker(*args):
    result = subprocess.run(['docker', *map(str, args)], capture_output=True, text=True, encoding='utf-8', cwd=Path(__file__).resolve().parent)
    if result.returncode:
        raise RuntimeError('Docker 预检失败，请确认 Docker 服务已启动。')
    return result.stdout.strip()


def discover():
    ids = docker('ps', '-aq', '--filter', 'label=com.docker.compose.service=api').splitlines()
    candidates = set()
    if ids:
        for info in json.loads(docker('inspect', *ids)):
            labels = info.get('Config', {}).get('Labels') or {}
            directory = labels.get('com.docker.compose.project.working_dir')
            project = labels.get('com.docker.compose.project')
            if not directory or not project:
                continue
            root = Path(directory)
            mounts = info.get('Mounts', [])
            if any(m.get('Type') == 'bind' and m.get('Destination') == core.ADMIN
                   and Path(m.get('Source', '')) == root / 'data/admin' for m in mounts):
                candidates.add((str(root), project))
    if len(candidates) != 1:
        raise ValueError('未找到唯一的 PortKill 部署（检测到 ' + str(len(candidates)) + ' 个）；已停止，不会猜选站点。')
    directory, project = next(iter(candidates))
    root = core.local(directory)
    if not root.is_absolute() or root == Path('/') or not re.fullmatch(r'[a-z0-9][a-z0-9_-]{0,62}', project):
        raise ValueError('检测到的部署路径或项目名无效。')
    for name in ('docker-compose.yml', '.env', 'verification.json'):
        if not core.local(root / name).is_file():
            raise ValueError('现有部署缺少 ' + name + '，未修改原站点。')
    return root, project


@contextmanager
def operation_lock(root):
    import fcntl
    folder = core.local(root / '.portkill-auto-updates')
    folder.mkdir(exist_ok=True, mode=0o700)
    lock = core.local(folder / 'operation.lock')
    with lock.open('a+b') as handle:
        try:
            fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            raise ValueError('另一个自动升级正在运行，请等待它完成。')
        yield folder


def persist_package(source, folder):
    identity = sha(source / 'automatic-manifest.json')
    target = core.local(folder / identity)
    if target.exists():
        verify_package(target)
        return target
    core.check_space([(folder, core.tree_bytes(source))])
    temporary = core.local(folder / ('.prepare-' + uuid.uuid4().hex))
    try:
        shutil.copytree(source, temporary)
        verify_package(temporary)
        temporary.rename(target)
    finally:
        if temporary.exists():
            shutil.rmtree(temporary)
    return target


class AutomaticInstaller(core.Installer):
    def __init__(self, root, project, package, candidate=None):
        super().__init__(root, project, revoke_sessions=True, enable_updates=False)
        self.package = core.local(package)
        self.stage = core.local(self.package / '.portkill-server-install')
        self.proxy = load('automatic_proxy_' + uuid.uuid4().hex, self.stage / 'scripts/docker-admin-security.py')
        migration_path = self.stage / 'scripts/caddy-directory-migration.py'
        if not migration_path.is_file():
            # Earlier interrupted packages predate mount migration. Use this
            # verified entry point's helper while retaining their saved package.
            migration_path = Path(__file__).with_name('scripts') / 'caddy-directory-migration.py'
        self.migration = load('automatic_migration_' + uuid.uuid4().hex, migration_path)
        self.core_module = core
        self.candidate = core.local(candidate) if candidate else None
        self.completion = root / '.portkill-auto-updates' / (sha(self.package / 'automatic-manifest.json') + '.completed.json')

    def verify_package(self):
        return verify_package(self.package)

    def verification_description(self):
        return '安装包自校验、文件摘要、运行项目及剩余空间检查通过。'

    def proxy_operation(self, mode):
        source = self.proxy_source().read_text(encoding='utf-8-sig')
        enforce = bool(re.search(r'(?m)^\s*Content-Security-Policy\s+"', source))
        ages = re.findall(r'Strict-Transport-Security\s+"max-age=(\d+)"', source)
        hsts = 31536000 if '31536000' in ages else 86400
        args = SimpleNamespace(mode=mode, candidate=self.candidate, project=self.project,
                               include_client_reports=True, enforce=enforce, hsts=hsts, automatic=True)
        with self.proxy.acquire_lock(self.root):
            return self.proxy.execute(None, args, self.root)

    def prepare_proxy(self):
        self.lock()
        self.drain()
        try:
            with self.proxy.acquire_lock(self.root):
                self.migration.migrate(self)
            self.candidate = self.proxy_operation('prepare')
        finally:
            if self.publication:
                self.publication.close()
                self.publication = None
        self.output('代理候选已自动验证；实际站点配置将在应用健康检查通过后加载。')

    def proxy_source(self):
        directory = self.root / 'docker/caddy-conf/Caddyfile'
        return directory if directory.is_file() else self.root / 'docker/Caddyfile'

    def recover_proxy(self):
        self.lock()
        self.drain()
        try:
            with self.proxy.acquire_lock(self.root):
                self.migration.migrate(self, recover_only=True)
        finally:
            if self.publication:
                self.publication.close()
                self.publication = None

    def extra_state(self):
        return dict(kind='portkill-automatic-install', automaticPackage=str(self.package), proxyCandidate=str(self.candidate))

    def finalize(self):
        self.output('应用健康检查通过，正在加载管理站安全头及客户端上报路由……')
        self.proxy_operation('apply')
        self.migration.ingress(self, security=True)

    def rollback_extra(self):
        saved = json.loads((self.backup / 'state.json').read_text(encoding='utf-8'))
        self.candidate = core.local(saved['proxyCandidate'])
        self.proxy_operation('rollback')
        self.migration.ingress(self)
        self.completion.unlink(missing_ok=True)

    def succeeded(self, state):
        core.write_json(self.completion, dict(root=str(self.root), project=self.project, backup=str(self.backup)))

    def already_installed(self):
        if not self.completion.is_file():
            return False
        record = json.loads(self.completion.read_text(encoding='utf-8'))
        backup = core.local(record['backup'])
        if record['root'] != str(self.root) or record['project'] != self.project or backup.parent != self.admin / 'server-install-backups':
            raise ValueError('已完成的安装记录不匹配，未修改原站点。')
        state = json.loads((backup / 'state.json').read_text(encoding='utf-8'))
        if state['state'] != 'succeeded':
            raise ValueError('该包已有安装记录但尚未成功，需先恢复。')
        candidate = core.local(state['proxyCandidate'])
        if candidate.parent != self.root / '.portkill-security':
            raise ValueError('已完成的代理配置记录不匹配，未修改原站点。')
        if sha(self.proxy_source()) != sha(candidate / 'candidate.Caddyfile'):
            raise ValueError('应用已安装但代理配置已变化，已停止并保留现有配置。')
        self.config(self.containers())
        self.health(state['targetImages'])
        self.migration.ingress(self, security=True)
        return True


def close(installer):
    for handle in (installer.publication, installer.control, installer.log):
        if handle:
            handle.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--recover', action='store_true')
    args = parser.parse_args()
    os.umask(0o077)
    package = Path(__file__).resolve().parent.parent
    installer = None
    try:
        if sys.version_info < (3, 10) or os.geteuid() != 0:
            raise ValueError('需要 sudo 和 Python 3.10 或更新版本。')
        verify_package(package)
        docker('compose', 'version')
        root, project = discover()
        os.chdir(root)
        print('自动识别到原部署：' + str(root) + '；项目：' + project, flush=True)
        with operation_lock(root) as folder:
            pending = core.local(root / 'data/admin/server-updates/pending')
            if pending.exists():
                identity = pending.read_text().strip()
                if not re.fullmatch(r'[a-f0-9]{32}', identity):
                    raise ValueError('存在无法识别的更新任务，保留现场并停止。')
                state = json.loads(core.local(root / 'data/admin/server-install-backups' / identity / 'state.json').read_text(encoding='utf-8'))
                stored = core.local(state.get('automaticPackage', '/invalid'))
                if state.get('kind') != 'portkill-automatic-install' or stored.parent != folder:
                    raise ValueError('另一个发布或服务器更新任务尚未完成，本次自动升级停止。')
                verify_package(stored)
                installer = AutomaticInstaller(root, project, stored, state['proxyCandidate'])
                installer.lock()
                installer.recover()
                print('此前中断的自动升级已恢复；如需再次升级，重新执行同一条命令。', flush=True)
                return 0
            mount_pending = core.local(root / '.portkill-security/mount-migrations/pending')
            if args.recover and not mount_pending.exists():
                print('没有需要恢复的自动升级任务，原站点未修改。', flush=True)
                return 0
            stored = persist_package(package, folder)
            installer = AutomaticInstaller(root, project, stored)
            logs = core.local(root / '.portkill-server-install-logs')
            logs.mkdir(exist_ok=True, mode=0o700)
            installer.log = (logs / ('automatic-' + uuid.uuid4().hex + '.log')).open('x', encoding='utf-8')
            installer.output('本次自动部署日志：' + installer.log.name)
            if args.recover:
                installer.recover_proxy()
                print('此前中断的代理挂载迁移已恢复；如需升级，重新执行不带 --recover 的原命令。', flush=True)
                return 0
            if installer.already_installed():
                print('此版本已安装且服务健康，无需重复升级；现有登录保持有效。', flush=True)
                return 0
            def interrupted(signum, frame):
                raise KeyboardInterrupt('收到终止信号，正在尝试恢复旧服务')
            for name in ('SIGTERM', 'SIGHUP'):
                if hasattr(signal, name):
                    signal.signal(getattr(signal, name), interrupted)
            installer.prepare_proxy()
            installer.install()
            print('自动升级完成。请刷新原管理网址，重新登录并完成 MFA。', flush=True)
            return 0
    except BaseException as error:
        message = '自动升级未完成：' + str(error)
        if installer:
            installer.output(message)
        else:
            print(message, flush=True)
        return 1
    finally:
        if installer:
            close(installer)


if __name__ == '__main__':
    raise SystemExit(main())
