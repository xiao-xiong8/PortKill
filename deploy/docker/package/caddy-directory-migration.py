"""Move only Caddy's config mount, with a durable recovery fence and TLS checks."""
import copy
import hashlib
import http.client
import json
from pathlib import Path
import re
import socket
import ssl
import time
import uuid


def sha(data):
    return hashlib.sha256(data).hexdigest()


def ingress(installer, security=False):
    info = installer.containers()['caddy']
    environment = dict(item.split('=', 1) for item in info['Config']['Env'] if '=' in item)
    ports = info.get('NetworkSettings', {}).get('Ports', {}).get('443/tcp') or []
    bindings = [p for p in ports if p.get('HostIp') in ('', '0.0.0.0', '127.0.0.1')]
    if not bindings:
        raise ValueError('Caddy 没有可供本机 TLS 验收的 IPv4 HTTPS 端口')
    port = int(bindings[0]['HostPort'])
    admin, downloads = environment['ADMIN_DOMAIN'], environment['UPDATES_DOMAIN']
    def request(domain, path, expected_type, body=True):
        connection = http.client.HTTPSConnection(domain, port, timeout=10, context=ssl.create_default_context())
        connection._create_connection = lambda address, timeout, source_address: socket.create_connection(('127.0.0.1', port), timeout, source_address)
        try:
            connection.request('GET', path, headers={'Host': domain, 'Connection': 'close'})
            response = connection.getresponse()
            if response.status != 200 or expected_type not in response.getheader('Content-Type', '').lower():
                raise ValueError('TLS 入口验收失败：' + path + ' HTTP ' + str(response.status))
            headers = {key.lower(): value for key, value in response.getheaders()}
            content = response.read(4 * 1024**2 + 1) if body else b''
            if len(content) > 4 * 1024**2:
                raise ValueError('Unexpected oversized ingress response')
            if security and domain == admin:
                required = {'x-frame-options': 'DENY', 'x-content-type-options': 'nosniff',
                            'referrer-policy': 'no-referrer', 'cross-origin-opener-policy': 'same-origin',
                            'cross-origin-resource-policy': 'same-origin'}
                if any(headers.get(k) != v for k, v in required.items()) or any(k in headers for k in ('server', 'via', 'alt-svc')):
                    raise ValueError('TLS 入口安全头验收失败：' + path)
                if not headers.get('strict-transport-security', '').startswith('max-age=') or \
                        not headers.get('permissions-policy') or not (headers.get('content-security-policy') or headers.get('content-security-policy-report-only')):
                    raise ValueError('TLS 入口安全策略不完整：' + path)
            return content
        finally:
            connection.close()
    deadline = time.monotonic() + 60
    while True:
        try:
            html = request(admin, '/login?returnTo=/overview', 'text/html')
            expected = (installer.web / 'index.html').read_bytes()
            if html != expected:
                raise ValueError('TLS 登录页与当前 Web 文件不一致')
            for asset in re.findall(r'(?:src|href)="(/assets/[^"<>]+)"', html.decode('utf-8'))[:2]:
                content_type = 'javascript' if asset.endswith('.js') else 'text/css'
                request(admin, asset, content_type)
            json.loads(request(admin, '/api/v1/auth/csrf', 'application/json'))
            manifest = request(downloads, '/stable/manifest.json', 'application/json')
            if manifest != (installer.root / 'data/public/manifest.json').read_bytes():
                raise ValueError('TLS 更新清单与当前发布文件不一致')
            request(downloads, '/stable/events', 'text/event-stream', body=False)
            installer.output('本机 HTTPS 入口验收通过：登录页、静态资源、API、更新清单及事件连接。')
            return
        except (OSError, ValueError, http.client.HTTPException) as error:
            if time.monotonic() >= deadline:
                raise RuntimeError('HTTPS 入口验收未通过：' + str(error)) from None
            time.sleep(2)


def probe_directory(installer, image, directory):
    """Test real daemon visibility across atomic replacement before proxy downtime."""
    name = '.portkill-probe-' + uuid.uuid4().hex
    path = directory / name
    second = directory / (name + '.next')
    container = None
    try:
        path.write_bytes(b'first'); path.chmod(0o644)
        container = installer.docker('run', '-d', '--rm', '--network', 'none', '--read-only', '--cap-drop', 'ALL',
            '--entrypoint', '/bin/sh', '--mount', 'type=bind,src=' + str(directory) + ',dst=/probe,readonly', image, '-c', 'sleep 120')
        if not re.fullmatch(r'[a-f0-9]{12,64}', container):
            raise ValueError('Unexpected probe container identity')
        if installer.docker('exec', container, 'cat', '/probe/' + name) != 'first':
            raise ValueError('Docker daemon 无法读取新配置目录')
        second.write_bytes(b'second'); second.chmod(0o644)
        second.replace(path)
        if installer.docker('exec', container, 'cat', '/probe/' + name) != 'second':
            raise ValueError('Docker 配置目录不支持原子替换后的即时可见性，尚未切换站点')
    finally:
        if container and re.fullmatch(r'[a-f0-9]{12,64}', container):
            installer.docker('rm', '-f', container)
        path.unlink(missing_ok=True); second.unlink(missing_ok=True)


def migrate(installer, recover_only=False):
    core = installer.core_module
    root = installer.root
    parent = core.local(root / '.portkill-security/mount-migrations')
    parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    pending = core.local(parent / 'pending')
    compose = root / 'docker-compose.yml'
    directory = core.local(root / 'docker/caddy-conf')
    source = core.local(root / 'docker/Caddyfile')
    def apply(file):
        installer.docker(*installer.compose(file), 'up', '-d', '--force-recreate', '--no-deps', '--no-build', '--pull', 'never', 'caddy', stream=True)
    def ready():
        deadline = time.monotonic() + 30
        while True:
            try:
                current = installer.containers()
                installer.config(current)
                return current
            except ValueError:
                if time.monotonic() >= deadline:
                    raise
                time.sleep(1)
    def restore(folder, saved):
        if saved['root'] != str(root) or saved['project'] != installer.project:
            raise ValueError('代理迁移恢复记录与当前站点不一致')
        original = (folder / 'compose-before').read_bytes()
        candidate = folder / 'compose-after.json'
        if sha(original) != saved['before'] or sha(candidate.read_bytes()) != saved['after'] or \
                sha(compose.read_bytes()) not in (saved['before'], saved['after']):
            raise ValueError('代理迁移期间 Compose 被其他操作修改，保留现场并停止')
        if sha(source.read_bytes()) != saved['config']:
            raise ValueError('原 Caddy 配置已变化，停止自动恢复')
        rollback = folder / 'compose-rollback.json'
        if sha(rollback.read_bytes()) != saved['rollback']:
            raise ValueError('代理迁移回退配置摘要不符')
        installer.output('恢复挂载迁移前的 Compose 和 Caddy；应用及数据库保持原状态……')
        # Re-create even if interrupted just before/after the previous up command.
        temporary = compose.with_name('docker-compose.migration-tmp')
        temporary.write_bytes(original); temporary.chmod(0o600); temporary.replace(compose)
        apply(rollback)
        ready()
        ingress(installer)
        saved['state'] = 'rolledBack'; core.write_json(folder / 'state.json', saved)
        pending.unlink(missing_ok=True)
    if pending.exists():
        identity = pending.read_text().strip()
        if not re.fullmatch(r'[a-f0-9]{32}', identity):
            raise ValueError('代理迁移恢复标记无效')
        folder = core.local(parent / identity)
        saved = json.loads((folder / 'state.json').read_text(encoding='utf-8'))
        restore(folder, saved)
    if recover_only:
        return
    containers = installer.containers()
    original = installer.config(containers)
    service = original['services']['caddy']
    mounts = [v for v in service.get('volumes', []) if v.get('target', '').startswith('/etc/caddy')]
    if len(mounts) != 1:
        raise ValueError('Caddy 配置挂载不唯一，不能自动迁移')
    mount = mounts[0]
    if mount.get('type') != 'bind' or not mount.get('read_only'):
        raise ValueError('Caddy 配置需要现有只读 bind 挂载')
    if mount['target'] == '/etc/caddy' and Path(mount['source']) == directory:
        installer.output('Caddy 已使用专用配置目录，核对实际挂载与 HTTPS 入口……')
        mounted = [m for m in containers['caddy']['Mounts'] if m['Destination'] == '/etc/caddy']
        if len(mounted) != 1 or Path(mounted[0]['Source']) != directory:
            raise ValueError('运行中的 Caddy 目录挂载与 Compose 不一致')
        probe_directory(installer, containers['caddy']['Image'], directory)
        ingress(installer)
        return
    if mount['target'] != '/etc/caddy/Caddyfile' or Path(mount['source']) != source:
        raise ValueError('不是已核对的原单文件挂载，停止自动迁移')
    mounted = [m for m in containers['caddy']['Mounts'] if m['Destination'] == '/etc/caddy/Caddyfile']
    if len(mounted) != 1 or Path(mounted[0]['Source']) != source:
        raise ValueError('运行中的原配置挂载与 Compose 不一致')
    ingress(installer)
    original_bytes = source.read_bytes()
    directory.mkdir(exist_ok=True, mode=0o755)
    directory.chmod(0o755)
    target = core.local(directory / 'Caddyfile')
    if target.exists() and target.read_bytes() != original_bytes:
        raise ValueError('新配置目录已有其他内容，未覆盖')
    target.write_bytes(original_bytes); target.chmod(0o644)
    probe_directory(installer, containers['caddy']['Image'], directory)
    # Validate expected bytes in the existing image without touching its active file.
    temporary = '/tmp/portkill-migration-' + uuid.uuid4().hex
    try:
        installer.docker('cp', str(target), containers['caddy']['Id'] + ':' + temporary)
        installer.docker('exec', containers['caddy']['Id'], 'caddy', 'validate', '--config', temporary, '--adapter', 'caddyfile')
    finally:
        installer.docker('exec', containers['caddy']['Id'], 'rm', '-f', temporary)
    image = json.loads(installer.docker('image', 'inspect', service['image']))[0]['Id']
    if image != containers['caddy']['Image']:
        raise ValueError('Caddy 镜像标签已漂移，停止迁移')
    updated = copy.deepcopy(original)
    replacement = next(v for v in updated['services']['caddy']['volumes'] if v['target'] == '/etc/caddy/Caddyfile')
    replacement['source'], replacement['target'] = str(directory), '/etc/caddy'
    identity = uuid.uuid4().hex
    folder = core.local(parent / identity); folder.mkdir(mode=0o700)
    (folder / 'compose-before').write_bytes(compose.read_bytes())
    installer.save_compose(folder / 'compose-rollback.json', original)
    installer.save_compose(folder / 'compose-after.json', updated)
    installer.docker(*installer.compose(folder / 'compose-after.json'), 'config', '--quiet')
    if installer.config(installer.containers()) != original or source.read_bytes() != original_bytes:
        raise ValueError('迁移预检期间部署发生变化，尚未切换')
    saved = dict(root=str(root), project=installer.project, state='switching', before=sha(compose.read_bytes()),
                 after=sha((folder / 'compose-after.json').read_bytes()), rollback=sha((folder / 'compose-rollback.json').read_bytes()), config=sha(original_bytes), image=image)
    core.write_json(folder / 'state.json', saved)
    with pending.open('x') as output:
        output.write(identity)
    try:
        installer.output('阶段一：仅重建 Caddy，迁移到专用配置目录；公网连接会短暂中断。')
        temporary_compose = compose.with_name('docker-compose.migration-tmp')
        temporary_compose.write_bytes((folder / 'compose-after.json').read_bytes())
        temporary_compose.chmod(0o600); temporary_compose.replace(compose)
        apply(compose)
        current = ready()
        if current['caddy']['Image'] != image:
            raise ValueError('代理迁移改变了 Caddy 镜像')
        installer.config(current)
        actual_mount = [m for m in current['caddy']['Mounts'] if m['Destination'] == '/etc/caddy']
        if len(actual_mount) != 1 or Path(actual_mount[0]['Source']) != directory:
            raise ValueError('Caddy 专用目录挂载未生效')
        if installer.docker('exec', current['caddy']['Id'], 'sha256sum', '/etc/caddy/Caddyfile').split()[0] != sha(original_bytes):
            raise ValueError('迁移后 Caddy 配置内容不一致')
        ingress(installer)
        saved['state'] = 'succeeded'; core.write_json(folder / 'state.json', saved)
        pending.unlink()
        installer.output('阶段一完成：目录挂载及 HTTPS 入口通过；后续应用回退保留该挂载结构。')
    except BaseException:
        restore(folder, saved)
        raise
