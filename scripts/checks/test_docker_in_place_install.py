"""In-place installer orchestration and recovery against a synthetic Docker command adapter."""
import copy
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock, patch
import hashlib

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import padding, rsa

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('in_place_install', ROOT / 'deploy/docker/package/install-server-in-place.py')
install = importlib.util.module_from_spec(spec)
spec.loader.exec_module(install)
VERIFY = install.verify
builder_spec = importlib.util.spec_from_file_location('in_place_builder', ROOT / 'scripts/release/New-DockerInPlacePackage.py')
builder = importlib.util.module_from_spec(builder_spec)
builder_spec.loader.exec_module(builder)


class InPlaceRunnerBuildTests(unittest.TestCase):
    def test_docker_wrappers_use_the_deployment_directory(self):
        with tempfile.TemporaryDirectory() as temporary:
            app = install.Installer(Path(temporary), 'portkill')
            with patch.object(install.subprocess, 'run', return_value=Mock(returncode=0, stdout='ok')) as run:
                self.assertEqual('ok', app.docker('info'))
            self.assertEqual(app.root, run.call_args.kwargs['cwd'])
            process = Mock(stdout=io.StringIO('build output\n'), wait=Mock(return_value=0), poll=Mock(return_value=0))
            with patch.object(install.subprocess, 'Popen', return_value=process) as popen, patch.object(app, 'output'):
                self.assertEqual('', app.docker('build', stream=True))
            self.assertEqual(app.root, popen.call_args.kwargs['cwd'])
            self.assertTrue(process.stdout.closed)

    def test_runner_reuses_local_application_runtime_and_overrides_nonroot_user(self):
        image = 'portkill-update-server:install-' + 'a' * 16
        dockerfile = builder.runner_dockerfile(image)
        self.assertEqual('FROM ' + image, dockerfile.splitlines()[0])
        self.assertIn('USER 0:0', dockerfile)
        self.assertIn('COPY server-bundle/runner/ runner/', dockerfile)
        self.assertIn('ENTRYPOINT ["/opt/portkill/runner/PortKiller.ServerUpdateRunner"]', dockerfile)
        self.assertNotIn('apt-get', dockerfile)
        self.assertNotIn('docker.io', dockerfile)
        self.assertEqual(1, sum(line.startswith('FROM ') for line in dockerfile.splitlines()))

    def test_runner_does_not_accept_arbitrary_or_unversioned_base_images(self):
        for image in ('debian:bookworm-slim', 'portkill-update-server:latest', 'portkill-update-server:install-' + 'a' * 16 + '\nRUN false'):
            with self.subTest(image=image), self.assertRaises(ValueError):
                builder.runner_dockerfile(image)


class FakeInstaller(install.Installer):
    def __init__(self, root):
        super().__init__(root, 'portkill')
        self.commands = []
        self.failure = ''
        self.up_calls = 0
        self.running = {name: 'sha256:old' for name in install.SERVICES}
        self.original = dict(services={name: dict(image='portkill-update-server:old' if name != 'caddy' else 'caddy:old', environment={'AllowedHosts': 'admin.example', 'TEST': 'a${literal}$z'},
            volumes=[dict(type='bind', source=str(root / 'data/admin'), target=install.ADMIN)]) for name in (*install.SERVICES, 'caddy')})
        self.source_config = copy.deepcopy(self.original)
        self.applied = []

    def lock(self):
        for name in ('', 'tasks', 'packages'):
            (self.shared / name).mkdir(parents=True, exist_ok=True)

    def drain(self):
        if self.failure == 'drain':
            raise RuntimeError('publish still running')

    def output(self, text):
        if self.log:
            self.log.write(text + '\n')

    def containers(self):
        return {name: dict(Id=name, Image=self.running.get(name, 'sha256:caddy'), Config=dict(Image='portkill-update-server:old', Labels={'com.docker.compose.config-hash': 'hash-' + name})) for name in (*install.SERVICES, 'caddy')}

    def health(self, images, timeout=120):
        if self.failure in ('health', 'rollback') and self.running['api'] == 'sha256:new':
            raise RuntimeError('new health failed')
        if self.failure == 'rollback' and self.up_calls > 1:
            raise RuntimeError('old health failed')
        if self.running != images:
            raise ValueError('unexpected running images')

    def docker(self, *args, stream=False):
        args = list(map(str, args))
        self.commands.append(args)
        if args[0] == 'compose':
            file = Path(args[args.index('-f') + 1])
            if 'stop' in args:
                return ''
            if 'config' in args:
                if '--hash' in args:
                    return '\n'.join(name + ' ' + ('changed' if self.failure == 'drift' else 'hash-' + name) for name in (*install.SERVICES, 'caddy'))
                if '--format' in args:
                    return install.compose_text(self.source_config)
                return ''
            if 'up' in args:
                self.up_calls += 1
                config = json.loads(file.read_text().replace('$$', '$'))
                self.applied.append(config)
                if self.failure == 'up' and self.up_calls == 1:
                    raise RuntimeError('compose failed')
                self.running = {name: 'sha256:new' if ':install-' in config['services'][name]['image'] else 'sha256:old' for name in install.SERVICES}
                return ''
        if args[0] == 'build':
            if self.failure == 'build':
                raise RuntimeError('build failed')
            return ''
        if args[0] == 'info':
            return str(self.root / 'docker-storage')
        if args[:2] == ['image', 'tag']:
            return ''
        if args[:2] == ['image', 'inspect']:
            return json.dumps([dict(Id='sha256:new')])
        if args[0] == 'run':
            if 'export-server-schema' in args[-1] and self.failure == 'runtime':
                raise RuntimeError('libhostfxr.so: Permission denied')
            if 'revoke-sessions' in args and self.failure == 'revoke':
                raise RuntimeError('session revocation failed')
            if 'backup' in args:
                if self.failure == 'backup':
                    raise RuntimeError('database schema mismatch')
                (self.backup / 'database-backup').mkdir()
                (self.backup / 'database-backup/admin.sqlite').write_text('verified snapshot')
            return ''
        raise AssertionError(args)


class InPlaceInstallTests(unittest.TestCase):
    def test_application_failure_keeps_successful_directory_mount_as_rollback_baseline(self):
        caddy = self.app.source_config['services']['caddy']
        caddy['volumes'] = [dict(type='bind', source=str(self.root / 'docker/caddy-conf'), target='/etc/caddy', read_only=True)]
        self.app.save_compose(self.root / 'docker-compose.yml', self.app.source_config)
        before = (self.root / 'docker-compose.yml').read_bytes()
        self.app.failure = 'health'
        with self.assertRaisesRegex(RuntimeError, 'new health failed'):
            self.app.install()
        self.assertEqual(before, (self.root / 'docker-compose.yml').read_bytes())
        self.assertEqual(caddy, self.app.applied[-1]['services']['caddy'])
        self.assertEqual('rolledBack', json.loads((self.app.backup / 'state.json').read_text())['state'])

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.app = FakeInstaller(self.root)
        for path in (self.app.admin, self.app.web, self.app.stage / 'server-bundle/web', self.root / 'docker-storage'):
            path.mkdir(parents=True, exist_ok=True)
        self.original = {'docker-compose.yml': '# old compose\n', '.env': 'SECRET=keep-me\n', 'verification.json': '{}', 'data/admin/admin.sqlite': 'business data', 'server-bundle/web/index.html': 'old web'}
        for name, text in self.original.items():
            (self.root / name).write_text(text)
        (self.app.stage / 'server-bundle/web/index.html').write_text('new web')
        self.manifest = dict(version='0.1.0', applicationImage='portkill-update-server:install-' + 'a' * 16, runnerImage='portkill-server-update-runner:install-' + 'a' * 16)
        self.addCleanup(patch.stopall)
        patch.object(install, 'verify', return_value=self.manifest).start()
        patch.object(install, 'socket_group', return_value='998').start()
        patch.object(install.os, 'chown', create=True).start()

    def tearDown(self):
        for handle in (self.app.log, self.app.control, self.app.publication):
            if handle:
                handle.close()

    def unchanged_data(self):
        for name in ('.env', 'verification.json', 'data/admin/admin.sqlite'):
            self.assertEqual(self.original[name], (self.root / name).read_text())

    def test_success_enables_updates_and_recreates_only_application_services(self):
        self.app.install()
        self.assertEqual(1, self.app.up_calls)
        candidate = self.app.applied[0]
        self.assertEqual('true', candidate['services']['api']['environment']['ServerUpdate__Enabled'])
        self.assertEqual('a${literal}$z', candidate['services']['api']['environment']['TEST'])
        self.assertEqual(self.app.original['services']['caddy'], candidate['services']['caddy'])
        self.assertIn('998', candidate['services']['api']['group_add'])
        up = next(args for args in self.app.commands if args[0] == 'compose' and 'up' in args)
        self.assertEqual(['api', 'worker', 'notifier'], up[-3:])
        self.assertEqual('new web', (self.app.web / 'index.html').read_text())
        self.assertFalse(self.app.pending.exists())
        self.assertTrue((self.app.backup / 'database-backup/admin.sqlite').is_file())
        self.unchanged_data()

    def test_automatic_mode_keeps_existing_permissions_and_revokes_before_service_start(self):
        self.app.enable_updates = False
        self.app.revoke_sessions = True
        with patch.object(install, 'socket_group', side_effect=AssertionError('must preserve socket permissions')):
            self.app.install()
        api = self.app.applied[0]['services']['api']
        self.assertNotIn('ServerUpdate__Enabled', api['environment'])
        self.assertNotIn('group_add', api)
        self.assertFalse(any(v.get('target') == '/var/run/docker.sock' for v in api['volumes']))
        self.assertEqual(1, sum(command[0] == 'build' for command in self.app.commands))
        backup = next(i for i, c in enumerate(self.app.commands) if c[0] == 'run' and 'backup' in c)
        stop = next(i for i, c in enumerate(self.app.commands) if c[0] == 'compose' and 'stop' in c)
        revoke = next(i for i, c in enumerate(self.app.commands) if 'revoke-sessions' in c)
        start = next(i for i, c in enumerate(self.app.commands) if c[0] == 'compose' and 'up' in c)
        self.assertLess(backup, stop)
        self.assertLess(stop, revoke)
        self.assertLess(revoke, start)
        command = self.app.commands[revoke]
        self.assertEqual('10001:10001', command[command.index('--user') + 1])
        self.unchanged_data()

    def test_revocation_failure_recovers_stopped_application_without_replacing_database(self):
        self.app.revoke_sessions = True
        self.app.failure = 'revoke'
        with self.assertRaisesRegex(RuntimeError, 'revocation'):
            self.app.install()
        self.assertEqual('old web', (self.app.web / 'index.html').read_text())
        self.assertFalse(self.app.pending.exists())
        self.assertEqual(1, self.app.up_calls)
        self.unchanged_data()

    def test_nonroot_runtime_probe_has_no_live_mounts_and_precedes_backup_and_stop(self):
        self.app.revoke_sessions = True
        self.app.install()
        checks = [(i, command) for i, command in enumerate(self.app.commands) if 'export-server-schema' in command[-1]]
        self.assertEqual(1, len(checks))
        position, command = checks[0]
        self.assertEqual('10001:10001', command[command.index('--user') + 1])
        self.assertEqual('none', command[command.index('--network') + 1])
        self.assertIn('--read-only', command)
        self.assertNotIn('--mount', command)
        self.assertNotIn('--volumes-from', command)
        self.assertLess(position, next(i for i, c in enumerate(self.app.commands) if 'backup' in c))
        self.assertLess(position, next(i for i, c in enumerate(self.app.commands) if 'stop' in c))

    def test_runtime_permission_failure_never_stops_services_or_creates_pending(self):
        self.app.failure = 'runtime'
        with self.assertRaisesRegex(RuntimeError, 'libhostfxr'):
            self.app.install()
        self.assertFalse(self.app.pending.exists())
        self.assertIsNone(self.app.backup)
        self.assertFalse(any('stop' in c or 'revoke-sessions' in c or 'backup' in c or 'up' in c for c in self.app.commands))
        self.assertEqual('old web', (self.app.web / 'index.html').read_text())
        self.unchanged_data()

    def test_proxy_finalize_failure_rolls_back_application_and_keeps_recovery_fence_if_proxy_restore_fails(self):
        self.app.finalize = lambda: (_ for _ in ()).throw(RuntimeError('proxy apply failed'))
        restored = []
        self.app.rollback_extra = lambda: restored.append(True)
        with self.assertRaisesRegex(RuntimeError, 'proxy apply'):
            self.app.install()
        self.assertEqual([True], restored)
        self.assertFalse(self.app.pending.exists())
        self.assertEqual('old web', (self.app.web / 'index.html').read_text())
        self.unchanged_data()

    def test_failed_proxy_restore_keeps_pending_and_a_retry_can_recover(self):
        self.app.finalize = lambda: (_ for _ in ()).throw(RuntimeError('proxy apply failed'))
        self.app.rollback_extra = lambda: (_ for _ in ()).throw(RuntimeError('proxy restore failed'))
        with self.assertRaisesRegex(RuntimeError, 'proxy apply'):
            self.app.install()
        self.assertTrue(self.app.pending.is_file())
        self.app.rollback_extra = lambda: None
        self.app.recover()
        self.assertFalse(self.app.pending.exists())
        self.assertEqual('old web', (self.app.web / 'index.html').read_text())
        self.unchanged_data()

    def test_build_drift_backup_or_publication_failure_never_changes_live_web(self):
        for failure in ('build', 'drift', 'backup', 'drain'):
            with self.subTest(failure=failure):
                self.app.failure = failure
                with self.assertRaises((ValueError, RuntimeError)):
                    self.app.install()
                self.assertEqual(0, self.app.up_calls)
                self.assertEqual('old web', (self.app.web / 'index.html').read_text())
                self.assertFalse(self.app.pending.exists())
                self.unchanged_data()
                if self.app.log:
                    self.app.log.close()
                    self.app.log = None
                for file in (self.root / '.portkill-server-install-logs').glob('*.log'):
                    file.unlink()

    def test_failed_compose_or_health_restores_original_configuration_and_web(self):
        for failure in ('up', 'health'):
            with self.subTest(failure=failure):
                self.app.failure = failure
                self.app.up_calls = 0
                self.app.switched = False
                with self.assertRaises(RuntimeError):
                    self.app.install()
                self.assertEqual(2, self.app.up_calls)
                self.assertEqual('old web', (self.app.web / 'index.html').read_text())
                self.assertEqual(self.original['docker-compose.yml'], (self.root / 'docker-compose.yml').read_text())
                self.assertFalse(self.app.pending.exists())
                self.unchanged_data()
                if self.app.log:
                    self.app.log.close()
                    self.app.log = None
                for file in (self.root / '.portkill-server-install-logs').glob('*.log'):
                    file.unlink()

    def test_failed_rollback_keeps_fence_and_recover_can_restore_old_services(self):
        self.app.failure = 'rollback'
        with self.assertRaises(RuntimeError):
            self.app.install()
        self.assertTrue(self.app.pending.is_file())
        self.app.failure = ''
        self.app.recover()
        self.assertFalse(self.app.pending.exists())
        self.assertEqual('old web', (self.app.web / 'index.html').read_text())
        self.unchanged_data()

    def test_interrupted_success_clears_only_matching_pending_after_health_check(self):
        self.app.install()
        saved = json.loads((self.app.backup / 'state.json').read_text())
        self.app.pending.write_text(saved['id'])
        self.app.recover()
        self.assertFalse(self.app.pending.exists())
        self.assertEqual('new web', (self.app.web / 'index.html').read_text())

    def test_real_manifest_verification_rejects_tampered_payload_and_wrong_key(self):
        key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
        public = key.public_key().public_bytes(serialization.Encoding.PEM, serialization.PublicFormat.SubjectPublicKeyInfo).decode()
        (self.root / 'verification.json').write_text(json.dumps({'PublicKeyPem': public}))
        file = self.app.stage / 'server-bundle/web/index.html'
        manifest = dict(self.manifest, schema=1, kind='portkill-in-place-install', runtime='linux-x64', files=[dict(
            path=file.relative_to(self.root).as_posix(), bytes=file.stat().st_size, sha256=hashlib.sha256(file.read_bytes()).hexdigest())])
        payload = json.dumps(manifest).encode()
        (self.app.stage / 'install-manifest.json').write_bytes(payload)
        (self.app.stage / 'install-manifest.sig').write_bytes(key.sign(payload, padding.PKCS1v15(), hashes.SHA256()))
        self.assertEqual('0.1.0', VERIFY(self.root, self.app.stage)['version'])
        file.write_text('tampered web')
        with self.assertRaises(ValueError):
            VERIFY(self.root, self.app.stage)
        file.write_text('new web')
        other = rsa.generate_private_key(public_exponent=65537, key_size=2048)
        public = other.public_key().public_bytes(serialization.Encoding.PEM, serialization.PublicFormat.SubjectPublicKeyInfo).decode()
        (self.root / 'verification.json').write_text(json.dumps({'PublicKeyPem': public}))
        with self.assertRaises(Exception):
            VERIFY(self.root, self.app.stage)


if __name__ == '__main__':
    unittest.main()
