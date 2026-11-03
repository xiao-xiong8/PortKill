"""Proxy policy and reversible deployment orchestration; no real Docker daemon required."""
import contextlib
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]


def load(name):
    spec = importlib.util.spec_from_file_location(name, ROOT / 'deploy/docker/package' / (name + '.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


policy = load('configure-admin-security')
deploy = load('docker-admin-security')
SOURCE = '''{
    email {$ACME_EMAIL}
}
{$ADMIN_DOMAIN} {
    handle /health/* {
        reverse_proxy api:5081
    }
    handle /api/v1/* {
        reverse_proxy api:5081
    }
    handle {
        root * /srv/web
        try_files {path} /index.html
        file_server
    }
}
{$UPDATES_DOMAIN} {
    header Access-Control-Allow-Origin *
    handle /stable/* {
        file_server
    }
}
'''


class PolicyTests(unittest.TestCase):
    def test_idempotent_modes_preserve_download_site(self):
        result = policy.configure(SOURCE)
        self.assertEqual(result, policy.configure(result))
        self.assertEqual(SOURCE.split('{$UPDATES_DOMAIN}')[1], result.split('{$UPDATES_DOMAIN}')[1])
        self.assertIn('protocols h1 h2', result)
        self.assertIn('path /health /health/*', result)
        self.assertIn('Content-Security-Policy-Report-Only "', result)
        enforced = policy.configure(result, True, 31536000)
        self.assertIn('Content-Security-Policy "', enforced)
        self.assertIn('-Content-Security-Policy-Report-Only', enforced)
        self.assertIn('max-age=31536000', enforced)

    def test_custom_routing_headers_and_protocol_drift_require_review(self):
        for text in (SOURCE.replace('reverse_proxy api:5081', 'reverse_proxy custom:5081'),
                     SOURCE.replace('{$ADMIN_DOMAIN} {', '{$ADMIN_DOMAIN} {\n    header X-Test custom'),
                     policy.configure(SOURCE).replace('protocols h1 h2', 'protocols h1 h2 h3')):
            with self.subTest(text=text), self.assertRaises(ValueError):
                policy.configure(text)


class DeploymentTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        (self.root / 'docker').mkdir()
        self.source = self.root / 'docker/Caddyfile'
        self.source.write_text(SOURCE, encoding='utf-8')
        self.inode = self.source.stat().st_ino
        self.commands = []
        self.fail_reload = False
        self.wrong_mount = False
        self.fail_validate = False
        self.detached = False
        self.detached_bytes = SOURCE.encode('utf-8')
        self.restart_failure = False
        self.restart_count = 0
        self.stopped = False
        self.directory_mode = False
        self.patcher = patch.object(deploy.subprocess, 'check_output', side_effect=self.docker)
        self.patcher.start(); self.addCleanup(self.patcher.stop)

    def docker(self, args, **kwargs):
        self.assertEqual(self.root.resolve(), kwargs['cwd'])
        self.commands.append(args)
        if args[1] == 'compose':
            self.assertEqual(str(self.root.resolve() / '.env'), args[args.index('--env-file') + 1])
            return 'caddy-container'
        if args[1] == 'inspect':
            return json.dumps([dict(Image='sha256:caddy', State=dict(Running=not self.stopped), Config=dict(Labels={'com.docker.compose.project': 'portkill'}),
                                    Mounts=[dict(Destination='/etc/caddy' if self.directory_mode else '/etc/caddy/Caddyfile', Source=str(self.source.parent if self.directory_mode else self.source) + ('.other' if self.wrong_mount else ''))])])
        if args[1] == 'restart':
            self.restart_count += 1
            if self.restart_failure:
                self.restart_failure = False
                raise RuntimeError('restart failed')
            self.detached = False
            self.stopped = False
            return 'caddy-container'
        if 'validate' in args and self.fail_validate:
            raise RuntimeError('invalid configuration')
        if 'reload' in args and self.fail_reload:
            self.fail_reload = False
            raise RuntimeError('reload failed')
        if 'sha256sum' in args:
            data = self.detached_bytes if self.detached else self.source.read_bytes()
            return hashlib.sha256(data).hexdigest() + '  /etc/caddy/Caddyfile'
        if 'stat' in args:
            value = self.source.stat()
            return str(value.st_dev) + ':' + str(value.st_ino + (1 if self.detached else 0))
        return 'v2.10.2' if 'version' in args else ''

    def invoke(self, mode, folder=None):
        args = ['security', mode, '--root', str(self.root), '--project', 'portkill']
        if folder:
            args += ['--candidate', str(folder)]
        with patch.object(sys, 'argv', args), contextlib.redirect_stdout(io.StringIO()):
            deploy.main()

    def prepare(self):
        self.invoke('prepare')
        return next(path.parent for path in (self.root / '.portkill-security').glob('*/state.json'))

    def test_apply_repeat_and_rollback_preserve_bind_inode(self):
        folder = self.prepare()
        self.assertEqual(SOURCE, self.source.read_text(encoding='utf-8'))
        self.invoke('apply', folder); self.invoke('apply', folder)
        self.assertIn('X-Frame-Options DENY', self.source.read_text(encoding='utf-8'))
        self.assertEqual(self.inode, self.source.stat().st_ino)
        self.invoke('rollback', folder)
        self.assertEqual(SOURCE, self.source.read_text(encoding='utf-8'))
        self.assertEqual(self.inode, self.source.stat().st_ino)

    def test_directory_mount_updates_atomically_without_restart_or_inode_comparison(self):
        directory = self.root / 'docker/caddy-conf'
        directory.mkdir()
        self.source = directory / 'Caddyfile'
        self.source.write_text(SOURCE, encoding='utf-8')
        self.directory_mode = True
        original_inode = self.source.stat().st_ino
        folder = self.prepare()
        self.invoke('apply', folder)
        self.assertIn('X-Frame-Options DENY', self.source.read_text(encoding='utf-8'))
        self.assertNotEqual(original_inode, self.source.stat().st_ino)
        self.assertEqual(0, self.restart_count)
        self.assertFalse(any('stat' in command for command in self.commands))
        self.invoke('rollback', folder)
        self.assertEqual(SOURCE, self.source.read_text(encoding='utf-8'))
        self.assertEqual(0, self.restart_count)

    def test_directory_reload_failure_restores_old_bytes_without_restarting_proxy(self):
        directory = self.root / 'docker/caddy-conf'
        directory.mkdir()
        self.source = directory / 'Caddyfile'
        self.source.write_text(SOURCE, encoding='utf-8')
        self.directory_mode = True
        folder = self.prepare()
        self.fail_reload = True
        with self.assertRaisesRegex(RuntimeError, 'reload failed'):
            self.invoke('apply', folder)
        self.assertEqual(SOURCE, self.source.read_text(encoding='utf-8'))
        self.assertEqual(0, self.restart_count)
        self.invoke('rollback', folder)
        self.assertEqual(SOURCE, self.source.read_text(encoding='utf-8'))

    def test_failed_reload_restores_previous_bytes_and_reloads(self):
        folder = self.prepare(); self.fail_reload = True
        with self.assertRaisesRegex(RuntimeError, 'reload failed'):
            self.invoke('apply', folder)
        self.assertEqual(SOURCE, self.source.read_text(encoding='utf-8'))
        self.assertGreaterEqual(sum('reload' in args for args in self.commands), 2)
        self.assertEqual(1, self.restart_count)

    def test_prepare_repairs_detached_inode_even_with_identical_bytes_before_upgrade(self):
        self.detached = True
        folder = self.prepare()
        self.assertEqual(1, self.restart_count)
        self.assertEqual(SOURCE, self.source.read_text(encoding='utf-8'))
        self.assertEqual(self.inode, self.source.stat().st_ino)
        self.assertTrue((folder / 'state.json').is_file())
        self.invoke('apply', folder)
        self.assertEqual(1, self.restart_count)

    def test_apply_refreshes_later_detached_mount_and_loads_the_new_bytes(self):
        folder = self.prepare()
        self.detached = True
        self.invoke('apply', folder)
        self.assertEqual(1, self.restart_count)
        self.assertIn('X-Frame-Options DENY', self.source.read_text(encoding='utf-8'))
        self.invoke('rollback', folder)
        self.assertEqual(SOURCE, self.source.read_text(encoding='utf-8'))

    def test_failed_mount_restart_restores_original_before_retry(self):
        folder = self.prepare()
        self.detached = True
        self.restart_failure = True
        with self.assertRaisesRegex(RuntimeError, 'restart failed'):
            self.invoke('apply', folder)
        self.assertEqual(2, self.restart_count)
        self.assertEqual(SOURCE, self.source.read_text(encoding='utf-8'))
        self.assertFalse(self.detached)

    def test_prepare_never_restarts_proxy_for_invalid_host_configuration(self):
        self.detached = True
        self.fail_validate = True
        with self.assertRaisesRegex(RuntimeError, 'invalid configuration'):
            self.prepare()
        self.assertEqual(0, self.restart_count)

    def test_prepare_failed_restart_attempt_recovers_original_service(self):
        self.detached = True
        self.restart_failure = True
        with self.assertRaisesRegex(RuntimeError, 'restart failed'):
            self.prepare()
        self.assertEqual(2, self.restart_count)
        self.assertFalse(self.detached)
        self.assertEqual(SOURCE, self.source.read_text(encoding='utf-8'))

    def test_rollback_can_recover_a_stopped_proxy_with_valid_saved_bytes(self):
        folder = self.prepare()
        self.invoke('apply', folder)
        self.stopped = True
        self.invoke('rollback', folder)
        self.assertEqual(1, self.restart_count)
        self.assertFalse(self.stopped)
        self.assertEqual(SOURCE, self.source.read_text(encoding='utf-8'))

    def test_real_pinned_inode_is_detected_when_host_path_was_atomically_replaced(self):
        pinned = self.root / 'mounted-Caddyfile'
        pinned.hardlink_to(self.source)
        replacement = self.root / 'new-Caddyfile'
        replacement.write_bytes(self.source.read_bytes())
        os.replace(replacement, self.source)
        self.assertNotEqual(pinned.stat().st_ino, self.source.stat().st_ino)
        original_docker = self.docker
        def docker(args, **kwargs):
            if 'stat' in args:
                value = pinned.stat()
                return str(value.st_dev) + ':' + str(value.st_ino)
            if 'sha256sum' in args:
                return hashlib.sha256(pinned.read_bytes()).hexdigest() + '  /etc/caddy/Caddyfile'
            if args[1] == 'restart':
                pinned.unlink()
                pinned.hardlink_to(self.source)
            return original_docker(args, **kwargs)
        with patch.object(deploy.subprocess, 'check_output', side_effect=docker):
            folder = self.prepare()
            self.invoke('apply', folder)
        self.assertEqual(1, self.restart_count)
        self.assertEqual(pinned.stat().st_ino, self.source.stat().st_ino)
        self.assertIn(b'X-Frame-Options DENY', pinned.read_bytes())

    def test_drift_and_tampered_candidate_are_rejected(self):
        folder = self.prepare()
        self.source.write_text(SOURCE + '# custom\n', encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'drift'):
            self.invoke('apply', folder)
        self.source.write_text(SOURCE, encoding='utf-8')
        (folder / 'candidate.Caddyfile').write_text('changed', encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'changed after validation'):
            self.invoke('apply', folder)

    def test_wrong_container_mount_and_invalid_config_do_not_change_live_file(self):
        self.wrong_mount = True
        with self.assertRaisesRegex(ValueError, 'different config mount'):
            self.invoke('prepare')
        self.wrong_mount = False; self.fail_validate = True
        with self.assertRaisesRegex(RuntimeError, 'invalid configuration'):
            self.invoke('prepare')
        self.assertEqual(SOURCE, self.source.read_text(encoding='utf-8'))

    def test_concurrent_proxy_operations_are_rejected(self):
        with deploy.acquire_lock(self.root):
            with self.assertRaisesRegex(ValueError, 'Another proxy'):
                self.invoke('prepare')

    def test_compose_error_identifies_missing_variable_without_exposing_its_message(self):
        error = subprocess.CalledProcessError(1, ['docker', 'compose'], stderr=
            'required variable ADMIN_DOMAIN is missing a value: SECRET=do-not-print')
        with patch.object(deploy.subprocess, 'check_output', side_effect=error):
            with self.assertRaisesRegex(RuntimeError, 'ADMIN_DOMAIN') as result:
                self.invoke('prepare')
        self.assertNotIn('do-not-print', str(result.exception))
        self.assertEqual(SOURCE, self.source.read_text(encoding='utf-8'))

    def test_docker_errors_are_classified_without_exposing_credentials(self):
        cases = [('getcwd: no such file or directory', '工作目录'),
                 ('Cannot connect to the Docker daemon', 'Docker 服务不可达'),
                 ('permission denied', '访问权限不足'),
                 ('open missing: no such file or directory', '文件不存在'),
                 ('unknown failure', 'sudo docker compose ps')]
        for stderr, expected in cases:
            with self.subTest(stderr=stderr):
                error = subprocess.CalledProcessError(17, ['docker'], stderr=(stderr + ' SECRET=private').encode())
                result = str(deploy.command_failure(error))
                self.assertIn(expected, result)
                self.assertIn('17', result)
                self.assertNotIn('private', result)

    def test_automatic_prepare_adds_client_routes_and_security_in_one_candidate(self):
        source = SOURCE.replace('{$UPDATES_DOMAIN} {\n', '{$UPDATES_DOMAIN} {\n    handle /stable/events {\n        reverse_proxy api:5081\n    }\n')
        self.source.write_text(source, encoding='utf-8')
        args = ['security', 'prepare', '--root', str(self.root), '--project', 'portkill', '--include-client-reports']
        with patch.object(sys, 'argv', args), contextlib.redirect_stdout(io.StringIO()):
            deploy.main()
        folder = next(path.parent for path in (self.root / '.portkill-security').glob('*/state.json'))
        candidate = (folder / 'candidate.Caddyfile').read_text(encoding='utf-8')
        self.assertIn('X-Frame-Options DENY', candidate)
        self.assertIn('/api/v1/client-installations/register', candidate)
        self.assertEqual(source, self.source.read_text(encoding='utf-8'))
        self.invoke('apply', folder)
        self.assertEqual(candidate, self.source.read_text(encoding='utf-8'))


if __name__ == '__main__':
    unittest.main()
