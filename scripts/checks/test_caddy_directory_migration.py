"""Exercise actual deployment topology, recovery, and real loopback TLS ingress."""
import copy
from contextlib import redirect_stdout
import datetime
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import importlib.util
import io
import ipaddress
import json
from pathlib import Path
import ssl
import tempfile
import threading
import unittest
from unittest.mock import Mock, patch

from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.x509.oid import NameOID

ROOT = Path(__file__).resolve().parents[2]


def load(name, file):
    spec = importlib.util.spec_from_file_location(name, ROOT / 'deploy/docker/package' / file)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


migration = load('migration', 'caddy-directory-migration.py')
core = load('migration_core', 'install-server-in-place.py')


class MigrationInstaller(core.Installer):
    def __init__(self, root):
        super().__init__(root, 'portkill-docker-kit-20261026')
        self.core_module = core
        fixture = (ROOT / 'scripts/checks/fixtures/actual-deployment-compose.json').read_text()
        self.running = json.loads(fixture.replace('DEPLOYMENT_ROOT', root.as_posix()))
        self.before = copy.deepcopy(self.running)
        self.save_compose(root / 'docker-compose.yml', self.running)
        (root / 'docker').mkdir()
        (root / 'docker/Caddyfile').write_text('original tested proxy configuration')
        self.commands = []
        self.fail_up = False
        self.fail_rollback = False
        self.probe_failure = False
        self.probe = None
        self.image = 'sha256:caddy'

    def output(self, value):
        pass

    def containers(self):
        mounts = [dict(Destination=v['target'], Source=v['source']) for v in self.running['services']['caddy']['volumes']]
        return {'caddy': dict(Id='caddy-id', Image=self.image, Mounts=mounts)}

    def config(self, containers):
        current = json.loads((self.root / 'docker-compose.yml').read_text().replace('$$', '$'))
        if current != self.running:
            raise ValueError('configuration drift')
        return copy.deepcopy(current)

    def docker(self, *args, stream=False):
        args = list(map(str, args))
        self.commands.append(args)
        if args[0] == 'compose':
            if 'config' in args:
                return ''
            if 'up' in args:
                file = Path(args[args.index('-f') + 1])
                value = json.loads(file.read_text().replace('$$', '$'))
                if self.fail_up:
                    self.fail_up = False
                    raise RuntimeError('migration up failed')
                if self.fail_rollback and file.name == 'compose-rollback.json':
                    raise RuntimeError('rollback interrupted')
                self.running = value
                return ''
        if args[:2] == ['image', 'inspect']:
            return json.dumps([dict(Id=self.image)])
        if args[0] == 'run':
            mount = args[args.index('--mount') + 1]
            self.probe = Path(mount.split('src=', 1)[1].split(',dst=')[0])
            return 'a' * 64
        if args[0] == 'exec' and 'cat' in args:
            if self.probe_failure:
                return 'stale'
            return (self.probe / Path(args[-1]).name).read_text()
        if args[0] == 'exec' and 'sha256sum' in args:
            return hashlib.sha256((self.root / 'docker/caddy-conf/Caddyfile').read_bytes()).hexdigest() + '  file'
        if args[0] in ('cp', 'exec', 'rm'):
            return ''
        raise AssertionError(args)


class MigrationTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name).resolve()
        self.app = MigrationInstaller(self.root)
        self.original_bytes = (self.root / 'docker-compose.yml').read_bytes()
        self.ingress = patch.object(migration, 'ingress').start()
        self.addCleanup(patch.stopall)

    def pending(self):
        return self.root / '.portkill-security/mount-migrations/pending'

    def test_actual_compose_preserves_every_setting_except_caddy_config_mount(self):
        migration.migrate(self.app)
        after = copy.deepcopy(self.app.running)
        mount = next(v for v in after['services']['caddy']['volumes'] if v['target'] == '/etc/caddy')
        self.assertEqual(str(self.root / 'docker/caddy-conf'), mount['source'])
        original_mount = next(v for v in self.app.before['services']['caddy']['volumes'] if v['target'] == '/etc/caddy/Caddyfile')
        mount['source'], mount['target'] = original_mount['source'], original_mount['target']
        self.assertEqual(self.app.before, after)
        self.assertFalse(self.pending().exists())
        up = [c for c in self.app.commands if 'up' in c]
        self.assertEqual(1, len(up))
        self.assertEqual('caddy', up[0][-1])
        self.assertIn('--no-deps', up[0])
        self.assertEqual(2, self.ingress.call_count)

    def test_up_failure_restores_original_compose_and_caddy(self):
        self.app.fail_up = True
        with self.assertRaisesRegex(RuntimeError, 'up failed'):
            migration.migrate(self.app)
        self.assertEqual(self.original_bytes, (self.root / 'docker-compose.yml').read_bytes())
        self.assertEqual(self.app.before, self.app.running)
        self.assertFalse(self.pending().exists())

    def test_tls_failure_after_migration_rolls_back_without_application_operations(self):
        self.ingress.side_effect = [None, RuntimeError('TLS failed'), None]
        with self.assertRaisesRegex(RuntimeError, 'TLS failed'):
            migration.migrate(self.app)
        self.assertEqual(self.app.before, self.app.running)
        self.assertFalse(self.pending().exists())
        self.assertFalse(any('stop' in c or 'revoke-sessions' in c or 'build' == c[0] for c in self.app.commands))

    def test_interrupted_recovery_leaves_fence_and_next_run_recovers_before_migration(self):
        self.app.fail_up = True
        self.app.fail_rollback = True
        with self.assertRaisesRegex(RuntimeError, 'rollback interrupted'):
            migration.migrate(self.app)
        self.assertTrue(self.pending().exists())
        self.app.fail_rollback = False
        migration.migrate(self.app)
        self.assertFalse(self.pending().exists())
        self.assertIn('/etc/caddy', [v['target'] for v in self.app.running['services']['caddy']['volumes']])

    def test_retry_does_not_recreate_an_already_migrated_proxy(self):
        migration.migrate(self.app)
        self.app.commands.clear()
        migration.migrate(self.app)
        self.assertFalse(any('up' in c for c in self.app.commands))

    def test_recover_only_restores_original_without_starting_another_migration(self):
        self.app.fail_up = True
        self.app.fail_rollback = True
        with self.assertRaises(RuntimeError):
            migration.migrate(self.app)
        self.app.fail_rollback = False
        self.app.commands.clear()
        migration.migrate(self.app, recover_only=True)
        self.assertEqual(self.app.before, self.app.running)
        self.assertEqual(self.original_bytes, (self.root / 'docker-compose.yml').read_bytes())
        self.assertFalse(self.pending().exists())
        self.assertEqual(1, sum('up' in c for c in self.app.commands))

    def test_real_daemon_visibility_probe_failure_precedes_any_proxy_recreation(self):
        self.app.probe_failure = True
        with self.assertRaisesRegex(ValueError, '无法读取'):
            migration.migrate(self.app)
        self.assertFalse(any('up' in c for c in self.app.commands))
        self.assertFalse(self.pending().exists())
        self.assertFalse(list((self.root / 'docker/caddy-conf').glob('.portkill-probe-*')))

    def test_existing_conflicting_directory_config_is_not_overwritten(self):
        directory = self.root / 'docker/caddy-conf'
        directory.mkdir()
        (directory / 'Caddyfile').write_text('other configuration')
        with self.assertRaisesRegex(ValueError, '已有其他内容'):
            migration.migrate(self.app)
        self.assertEqual('other configuration', (directory / 'Caddyfile').read_text())
        self.assertFalse(any('up' in c for c in self.app.commands))


class IngressTests(unittest.TestCase):
    def test_real_tls_uses_domain_verification_loopback_routing_and_checks_headers(self):
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            (folder / 'server-bundle/web').mkdir(parents=True)
            (folder / 'data/public').mkdir(parents=True)
            html = b'<html><script src="/assets/app.js"></script></html>'
            (folder / 'server-bundle/web/index.html').write_bytes(html)
            (folder / 'data/public/manifest.json').write_bytes(b'{"release":1}')
            key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
            subject = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, 'admin.test')])
            now = datetime.datetime.now(datetime.timezone.utc)
            certificate = x509.CertificateBuilder().subject_name(subject).issuer_name(subject).public_key(key.public_key()).serial_number(x509.random_serial_number()).not_valid_before(now - datetime.timedelta(minutes=1)).not_valid_after(now + datetime.timedelta(days=1)).add_extension(x509.SubjectAlternativeName([x509.DNSName('admin.test'), x509.DNSName('updates.test')]), critical=False).sign(key, hashes.SHA256())
            cert_file, key_file = folder / 'cert.pem', folder / 'key.pem'
            cert_file.write_bytes(certificate.public_bytes(serialization.Encoding.PEM))
            key_file.write_bytes(key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption()))
            requests = []
            class Handler(BaseHTTPRequestHandler):
                omit_frame_header = False
                def do_GET(self):
                    requests.append((self.headers['Host'], self.path))
                    content = html
                    content_type = 'text/html'
                    if self.path == '/assets/app.js': content, content_type = b'console.log(1)', 'text/javascript'
                    if self.path.startswith('/api/'): content, content_type = b'{"token":"test"}', 'application/json'
                    if self.path.endswith('/manifest.json'): content, content_type = b'{"release":1}', 'application/json'
                    if self.path == '/stable/events': content, content_type = b': connected\n\n', 'text/event-stream'
                    self.send_response_only(200)
                    self.send_header('Content-Type', content_type)
                    for k,v in {'X-Frame-Options':'DENY','X-Content-Type-Options':'nosniff','Referrer-Policy':'no-referrer','Cross-Origin-Opener-Policy':'same-origin','Cross-Origin-Resource-Policy':'same-origin','Strict-Transport-Security':'max-age=86400','Permissions-Policy':'camera=()','Content-Security-Policy-Report-Only':"default-src 'self'"}.items():
                        if not (self.omit_frame_header and k == 'X-Frame-Options'):
                            self.send_header(k,v)
                    self.send_header('Content-Length', str(len(content)))
                    self.end_headers()
                    self.wfile.write(content)
                def log_message(self, *args): pass
            server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
            context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
            context.load_cert_chain(cert_file, key_file)
            server.socket = context.wrap_socket(server.socket, server_side=True)
            threading.Thread(target=server.serve_forever, daemon=True).start()
            app = Mock(root=folder, web=folder / 'server-bundle/web')
            app.containers.return_value = {'caddy': dict(Config=dict(Env=['ADMIN_DOMAIN=admin.test','UPDATES_DOMAIN=updates.test']),NetworkSettings=dict(Ports={'443/tcp':[dict(HostIp='0.0.0.0',HostPort=str(server.server_port))]}))}
            factory = ssl.create_default_context
            try:
                with patch.object(migration.ssl, 'create_default_context', side_effect=lambda: factory(cafile=str(cert_file))):
                    migration.ingress(app, security=True)
                    self.assertEqual(5, len(requests))
                    Handler.omit_frame_header = True
                    with patch.object(migration.time, 'monotonic', side_effect=[0, 61]):
                        with self.assertRaisesRegex(RuntimeError, '安全头验收失败'):
                            migration.ingress(app, security=True)
            finally:
                server.shutdown(); server.server_close()
            self.assertIn(('admin.test','/api/v1/auth/csrf'), requests)
            self.assertIn(('updates.test','/stable/events'), requests)
            self.assertEqual(6, len(requests))


if __name__ == '__main__':
    unittest.main()
