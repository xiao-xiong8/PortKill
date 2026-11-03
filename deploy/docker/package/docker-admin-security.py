"""Prepare/apply/rollback Caddy policy and refresh detached single-file mounts."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
import re
from pathlib import Path
import runpy
import subprocess
import time
import uuid


def digest(data):
    return hashlib.sha256(data).hexdigest()


def command_failure(error):
    text = error.stderr or ''
    if isinstance(text, bytes):
        text = text.decode('utf-8', errors='replace')
    lower = text.lower()
    missing = re.search(r'required variable ([a-z_][a-z0-9_]*)', text, re.I)
    if 'getcwd' in lower or 'getwd' in lower or 'current working directory' in lower:
        reason = '工作目录不存在，请切换到原部署目录后重试'
    elif missing:
        reason = '原部署 .env 缺少必填变量：' + missing.group(1)
    elif 'cannot connect to the docker daemon' in lower or 'is the docker daemon running' in lower:
        reason = 'Docker 服务不可达'
    elif 'permission denied' in lower:
        reason = 'Docker 或部署文件访问权限不足'
    elif 'no such file or directory' in lower:
        reason = 'Docker 或 Compose 引用的文件不存在'
    else:
        reason = '请在原部署目录执行 sudo docker compose ps 查看具体错误'
    return RuntimeError('Docker/Compose 命令失败（退出码 ' + str(error.returncode) + '）：' + reason)


def write_in_place(path, data):
    # Atomic rename would leave a single-file Docker bind mount pointing at the previous inode.
    with path.open('r+b') as output:
        output.seek(0)
        output.write(data)
        output.truncate()
        output.flush()
        os.fsync(output.fileno())


def acquire_lock(root):
    folder = root / '.portkill-security'
    if folder.is_symlink():
        raise ValueError('Security backup directory must not be a symlink')
    folder.mkdir(exist_ok=True, mode=0o700)
    handle = (folder / 'operation.lock').open('a+b')
    try:
        if os.name == 'nt':
            import msvcrt
            handle.write(b'0'); handle.flush(); handle.seek(0)
            msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
        else:
            import fcntl
            fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BaseException:
        handle.close()
        raise ValueError('Another proxy security operation is running')
    return handle


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('mode', choices=('prepare', 'apply', 'rollback'))
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--project', required=True)
    parser.add_argument('--candidate', type=Path)
    parser.add_argument('--enforce', action='store_true')
    parser.add_argument('--include-client-reports', action='store_true')
    parser.add_argument('--hsts', type=int, choices=(86400, 31536000), default=86400)
    args = parser.parse_args()
    root = args.root.resolve(strict=True)
    with acquire_lock(root):
        execute(parser, args, root)


def execute(parser, args, root):
    source = root / 'docker/Caddyfile'
    compose = ['docker', 'compose', '--project-directory', str(root), '--env-file', str(root / '.env'), '-f', str(root / 'docker-compose.yml'), '-p', args.project]

    def run(command):
        try:
            return subprocess.check_output(command, text=True, encoding='utf-8', stderr=subprocess.PIPE, cwd=root).strip()
        except subprocess.CalledProcessError as error:
            raise command_failure(error) from None

    container = run(compose + ['ps', '--all', '-q', 'caddy'])
    if not container or '\n' in container:
        raise ValueError('Expected exactly one Caddy container')
    info = json.loads(run(['docker', 'inspect', container]))[0]
    mounts = [m for m in info['Mounts'] if m['Destination'] in ('/etc/caddy', '/etc/caddy/Caddyfile')]
    directory_mount = len(mounts) == 1 and mounts[0]['Destination'] == '/etc/caddy'
    if directory_mount:
        source = root / 'docker/caddy-conf/Caddyfile'
    expected_mount = source.parent if directory_mount else source
    if len(mounts) != 1 or Path(mounts[0]['Source']).resolve() != expected_mount.resolve():
        raise ValueError('Running Caddy uses a different config mount; review deployment before updating')
    if any(p.is_symlink() for p in (source, source.parent)) or not source.is_file():
        raise ValueError('Expected a regular deployment Caddy configuration')
    if info.get('Config', {}).get('Labels', {}).get('com.docker.compose.project') != args.project:
        raise ValueError('Compose project mismatch')

    def validate(path):
        temporary = '/tmp/portkill-security-' + uuid.uuid4().hex
        try:
            run(['docker', 'cp', str(path), container + ':' + temporary])
            run(['docker', 'exec', container, 'caddy', 'validate', '--config', temporary, '--adapter', 'caddyfile'])
        finally:
            run(['docker', 'exec', container, 'rm', '-f', temporary])

    def reload_config():
        run(['docker', 'exec', container, 'caddy', 'reload', '--config', '/etc/caddy/Caddyfile', '--adapter', 'caddyfile'])

    def mounted_file_matches(data, report=False):
        if directory_mount:
            actual = run(['docker', 'exec', container, 'sha256sum', '/etc/caddy/Caddyfile']).split()[0]
            return actual == digest(data)
        host = source.stat()
        identity = run(['docker', 'exec', container, 'stat', '-c', '%d:%i', '/etc/caddy/Caddyfile'])
        actual = run(['docker', 'exec', container, 'sha256sum', '/etc/caddy/Caddyfile']).split()[0]
        expected_identity = str(host.st_dev) + ':' + str(host.st_ino)
        matches = identity == expected_identity and actual == digest(data)
        if report and not matches:
            print('Caddy 文件检查：host=' + expected_identity + ' container=' + identity +
                  ' hostSha256=' + digest(data) + ' containerSha256=' + actual, flush=True)
        # Matching bytes alone misses a detached, single-file mount that happens
        # to contain the same old bytes. Linux bind mounts retain device/inode.
        return matches

    def synchronize_mount(data, force_restart=False):
        if source.read_bytes() != data:
            raise ValueError('Configuration drift detected during mount synchronization')
        if not force_restart and mounted_file_matches(data, report=True):
            return
        if directory_mount:
            if not mounted_file_matches(data):
                raise ValueError('Caddy directory-mounted configuration digest mismatch')
            reload_config()
            return
        print('Caddy 文件挂载未同步，正在重启现有代理容器刷新挂载；公网连接会短暂中断。', flush=True)
        # Restart the exact existing container: retain image, environment,
        # networks, volumes and ID. No Compose re-creation or image selection.
        run(['docker', 'restart', '--timeout', '30', container])
        deadline = time.monotonic() + 30
        while True:
            try:
                current = json.loads(run(['docker', 'inspect', container]))[0]
                if current.get('Image') != info.get('Image'):
                    raise ValueError('Caddy image changed during mount synchronization')
                if not mounted_file_matches(data):
                    raise ValueError('Caddy mount still differs after restart')
                validate(source)
                reload_config()
                print('Caddy 挂载、配置校验及重载均通过。', flush=True)
                return
            except (RuntimeError, ValueError) as error:
                if time.monotonic() >= deadline:
                    raise RuntimeError('Caddy 刷新挂载后未通过校验：' + str(error)) from None
                time.sleep(0.5)

    def write_configuration(data):
        if not directory_mount:
            write_in_place(source, data)
            return
        temporary = source.with_name('.Caddyfile-' + uuid.uuid4().hex)
        try:
            with temporary.open('xb') as output:
                output.write(data)
                output.flush()
                os.fsync(output.fileno())
            temporary.chmod(0o644)
            os.replace(temporary, source)
        finally:
            temporary.unlink(missing_ok=True)

    if args.mode == 'prepare':
        if args.candidate:
            parser.error('prepare generates its own unique candidate directory')
        folder = root / '.portkill-security' / (datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ-') + uuid.uuid4().hex[:8])
        folder.mkdir(parents=True, mode=0o700)
        original = source.read_bytes()
        configure = runpy.run_path(str(Path(__file__).with_name('configure-admin-security.py')))['configure']
        text = original.decode('utf-8-sig')
        if getattr(args, 'include_client_reports', False):
            reports = runpy.run_path(str(Path(__file__).with_name('enable-client-reports.py')))['configure_caddy']
            text = reports(text)
        candidate = configure(text, args.enforce, args.hsts).encode('utf-8')
        (folder / 'original.Caddyfile').write_bytes(original)
        (folder / 'candidate.Caddyfile').write_bytes(candidate)
        # Validate both configurations before refreshing a detached bind.
        # Repair before application downtime, even if both old byte sets match.
        validate(source)
        validate(folder / 'candidate.Caddyfile')
        try:
            synchronize_mount(original)
        except BaseException:
            # The host config has not changed, but a failed restart can leave
            # Caddy stopped. Make one recovery attempt with validated bytes.
            synchronize_mount(original, force_restart=True)
            raise
        state = dict(root=str(root), project=args.project, original=digest(original), candidate=digest(candidate), status='prepared',
                     caddyVersion=run(['docker', 'exec', container, 'caddy', 'version']), image=info.get('Image'))
        (folder / 'state.json').write_text(json.dumps(state, indent=2), encoding='utf-8')
        print('Validated candidate:', folder)
        if not getattr(args, 'automatic', False):
            print('Review candidate.Caddyfile, then apply with --candidate pointing to this directory.')
        return folder
    if not args.candidate:
        parser.error('--candidate is required for apply/rollback')
    folder = args.candidate.resolve(strict=True)
    if folder.parent != (root / '.portkill-security').resolve():
        raise ValueError('Candidate must belong to this deployment')
    state = json.loads((folder / 'state.json').read_text(encoding='utf-8'))
    if state['root'] != str(root) or state['project'] != args.project:
        raise ValueError('Candidate deployment mismatch')
    original, candidate = (folder / 'original.Caddyfile').read_bytes(), (folder / 'candidate.Caddyfile').read_bytes()
    if digest(original) != state['original'] or digest(candidate) != state['candidate']:
        raise ValueError('Candidate or backup changed after validation')
    expected, data = (state['original'], candidate) if args.mode == 'apply' else (state['candidate'], original)
    current = source.read_bytes()
    if args.mode == 'rollback' and not info.get('State', {}).get('Running', True):
        # Recovery may arrive after a failed restart. The candidate state binds
        # these saved bytes to this exact deployment; never overwrite drift.
        if digest(current) not in (expected, digest(data)):
            raise ValueError('Configuration drift detected; refusing stopped-proxy recovery')
        write_configuration(data)
        synchronize_mount(data, force_restart=True)
        state['status'] = 'rolled-back'
        (folder / 'state.json').write_text(json.dumps(state, indent=2), encoding='utf-8')
        print('Stopped proxy recovered with the saved configuration.')
        return
    if digest(current) == digest(data):
        validate(source)
        synchronize_mount(data)
        reload_config()  # Also recovers interruption after writing but before the previous reload.
        print('Configuration already present; validated and reloaded.')
        return
    if digest(current) != expected:
        raise ValueError('Configuration drift detected; refusing to overwrite other changes')
    validate(folder / ('candidate.Caddyfile' if args.mode == 'apply' else 'original.Caddyfile'))
    try:
        write_configuration(data)
        synchronize_mount(data)
        reload_config()
    except BaseException:
        write_configuration(current)
        # A failed restart can leave the container stopped; restart it with
        # the saved, validated original bytes before declaring recovery.
        synchronize_mount(current, force_restart=True)
        reload_config()
        raise
    state['status'] = 'applied' if args.mode == 'apply' else 'rolled-back'
    (folder / 'state.json').write_text(json.dumps(state, indent=2), encoding='utf-8')
    print(state['status'], folder)


if __name__ == '__main__':
    main()
