"""Automatic discovery, immutable staging, package validation, and self-extraction checks."""
from contextlib import redirect_stdout
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch
import zipfile

ROOT = Path(__file__).resolve().parents[2]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, ROOT / path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


auto = load('auto_update', 'deploy/docker/package/automatic-server-update.py')
builder = load('auto_builder', 'scripts/release/New-AutomaticDockerPackage.py')


class AutomaticTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.deployment = self.root / 'deployment'
        self.deployment.mkdir()
        for name in ('docker-compose.yml', '.env', 'verification.json'):
            (self.deployment / name).write_text('{}')

    def container(self, project='portkill', root=None):
        root = root or self.deployment
        return dict(Config=dict(Labels={'com.docker.compose.project.working_dir': str(root), 'com.docker.compose.project': project}),
                    Mounts=[dict(Type='bind', Destination=auto.core.ADMIN, Source=str(root / 'data/admin'))])

    def test_discovery_subprocess_uses_existing_script_directory(self):
        with patch.object(auto.subprocess, 'run', return_value=Mock(returncode=0, stdout='ok')) as run:
            self.assertEqual('ok', auto.docker('ps'))
        self.assertEqual(Path(auto.__file__).resolve().parent, run.call_args.kwargs['cwd'])

    def test_detects_existing_mount_and_project_without_fixed_folder_names(self):
        with patch.object(auto, 'docker', side_effect=['container-id', json.dumps([self.container('my-original-project')])]):
            self.assertEqual((self.deployment, 'my-original-project'), auto.discover())

    def test_multiple_projects_or_no_correct_mount_are_never_guessed(self):
        other = self.container('second-project')
        with patch.object(auto, 'docker', side_effect=['one\ntwo', json.dumps([self.container(), other])]):
            with self.assertRaises(ValueError):
                auto.discover()
        wrong = self.container()
        wrong['Mounts'][0]['Source'] = str(self.root / 'unrelated')
        with patch.object(auto, 'docker', side_effect=['one', json.dumps([wrong])]):
            with self.assertRaises(ValueError):
                auto.discover()

    def fixture_package(self):
        package = self.root / 'package'
        file = package / '.portkill-server-install/install.py'
        file.parent.mkdir(parents=True)
        file.write_bytes(b'original installer')
        manifest = dict(schema=1, kind='portkill-automatic-install', runtime='linux-x64',
                        applicationImage='portkill-update-server:install-' + 'a' * 16,
                        files=[dict(path=file.relative_to(package).as_posix(), bytes=file.stat().st_size,
                                    sha256=hashlib.sha256(file.read_bytes()).hexdigest())])
        (package / 'automatic-manifest.json').write_text(json.dumps(manifest))
        return package, file, manifest

    def test_package_hashes_and_unrecorded_files_are_rejected(self):
        package, file, _ = self.fixture_package()
        auto.verify_package(package)
        file.write_bytes(b'tampered installer')
        with self.assertRaises(ValueError):
            auto.verify_package(package)
        file.write_bytes(b'original installer')
        (package / 'extra.py').write_text('print(1)')
        with self.assertRaises(ValueError):
            auto.verify_package(package)

    def test_versioned_staging_is_reused_and_tampering_is_not_overwritten(self):
        package, file, _ = self.fixture_package()
        folder = self.deployment / '.portkill-auto-updates'
        folder.mkdir()
        with patch.object(auto.core, 'check_space'):
            stored = auto.persist_package(package, folder)
            self.assertEqual(stored, auto.persist_package(package, folder))
            self.assertNotEqual(stored, package)
            target = stored / file.relative_to(package)
            target.write_text('modified')
            with self.assertRaises(ValueError):
                auto.persist_package(package, folder)
            self.assertEqual('modified', target.read_text())

    def test_unsafe_manifest_path_is_rejected(self):
        package, _, manifest = self.fixture_package()
        manifest['files'][0]['path'] = '../escape.py'
        (package / 'automatic-manifest.json').write_text(json.dumps(manifest))
        with self.assertRaises(ValueError):
            auto.verify_package(package)

    def test_completed_package_checks_health_without_new_revocation(self):
        instance = object.__new__(auto.AutomaticInstaller)
        instance.root, instance.project = self.deployment, 'portkill'
        instance.admin = self.deployment / 'data/admin'
        instance.completion = self.root / 'done.json'
        instance.migration = Mock()
        backup = instance.admin / 'server-install-backups' / ('a' * 32)
        backup.mkdir(parents=True)
        candidate = instance.root / '.portkill-security' / 'candidate'
        candidate.mkdir(parents=True)
        (candidate / 'candidate.Caddyfile').write_text('same policy')
        (instance.root / 'docker').mkdir()
        (instance.root / 'docker/Caddyfile').write_text('same policy')
        (backup / 'state.json').write_text(json.dumps(dict(state='succeeded', targetImages={'api': 'new'}, proxyCandidate=str(candidate))))
        instance.completion.write_text(json.dumps(dict(root=str(instance.root), project='portkill', backup=str(backup))))
        with patch.object(instance, 'containers', return_value={}), patch.object(instance, 'config') as config, patch.object(instance, 'health') as health:
            self.assertTrue(instance.already_installed())
            config.assert_called_once()
            health.assert_called_once_with({'api': 'new'})
        (instance.root / 'docker/Caddyfile').write_text('changed policy')
        with self.assertRaises(ValueError):
            instance.already_installed()

    def test_proxy_automation_preserves_enforced_csp_and_long_hsts(self):
        directory = self.deployment / 'docker'
        directory.mkdir()
        (directory / 'Caddyfile').write_text('Content-Security-Policy "self"\nStrict-Transport-Security "max-age=31536000"')
        instance = object.__new__(auto.AutomaticInstaller)
        instance.root, instance.project, instance.candidate = self.deployment, 'portkill', None
        from contextlib import nullcontext
        from types import SimpleNamespace
        calls = []
        instance.proxy = SimpleNamespace(acquire_lock=lambda root: nullcontext(), execute=lambda parser, args, root: calls.append(args))
        instance.proxy_operation('prepare')
        self.assertTrue(calls[0].enforce)
        self.assertEqual(31536000, calls[0].hsts)
        self.assertTrue(calls[0].include_client_reports)

    def test_migration_precedes_candidate_and_publication_lease_is_released_on_failure(self):
        from contextlib import nullcontext
        instance = object.__new__(auto.AutomaticInstaller)
        instance.root = self.deployment
        calls = []
        instance.lock = lambda: calls.append('lock')
        instance.drain = lambda: calls.append('drain')
        lease = Mock()
        instance.publication = lease
        instance.proxy = Mock()
        instance.proxy.acquire_lock.return_value = nullcontext()
        instance.migration = Mock()
        instance.migration.migrate.side_effect = RuntimeError('migration failed')
        instance.proxy_operation = Mock()
        with self.assertRaisesRegex(RuntimeError, 'migration failed'):
            instance.prepare_proxy()
        self.assertEqual(['lock', 'drain'], calls)
        instance.proxy_operation.assert_not_called()
        lease.close.assert_called_once()
        self.assertIsNone(instance.publication)


class MainFlowTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.folder = self.root / '.portkill-auto-updates'
        self.folder.mkdir()
        self.package = self.folder / ('a' * 64)
        self.package.mkdir()
        self.instance = Mock(publication=None, control=None, log=None)
        self.instance.already_installed.return_value = False
        from contextlib import nullcontext
        patches = [patch.object(sys, 'argv', ['automatic']),
                   patch.object(auto.os, 'chdir'),
                   patch.object(auto.os, 'geteuid', return_value=0, create=True),
                   patch.object(auto, 'verify_package', return_value={}),
                   patch.object(auto, 'docker', return_value=''),
                   patch.object(auto, 'discover', return_value=(self.root, 'portkill')),
                   patch.object(auto, 'operation_lock', return_value=nullcontext(self.folder)),
                   patch.object(auto, 'persist_package', return_value=self.package),
                   patch.object(auto, 'AutomaticInstaller', return_value=self.instance),
                   patch.object(auto.signal, 'signal')]
        for item in patches:
            item.start()
            self.addCleanup(item.stop)

    def pending(self, kind):
        identity = 'b' * 32
        shared = self.root / 'data/admin/server-updates'
        shared.mkdir(parents=True)
        (shared / 'pending').write_text(identity)
        backup = self.root / 'data/admin/server-install-backups' / identity
        backup.mkdir(parents=True)
        (backup / 'state.json').write_text(json.dumps(dict(kind=kind, automaticPackage=str(self.package),
                                                           proxyCandidate=str(self.root / '.portkill-security/candidate'))))

    def test_normal_run_prepares_policy_and_installs_without_prompt(self):
        with redirect_stdout(io.StringIO()):
            self.assertEqual(0, auto.main())
        self.instance.prepare_proxy.assert_called_once()
        self.instance.install.assert_called_once()
        self.instance.recover.assert_not_called()
        auto.os.chdir.assert_called_once_with(self.root)

    def test_same_command_recovers_own_interruption_without_new_install(self):
        self.pending('portkill-automatic-install')
        with redirect_stdout(io.StringIO()):
            self.assertEqual(0, auto.main())
        self.instance.lock.assert_called_once()
        self.instance.recover.assert_called_once()
        self.instance.install.assert_not_called()
        self.instance.prepare_proxy.assert_not_called()

    def test_foreign_pending_is_preserved_without_recovery_or_upgrade(self):
        self.pending('other-server-update')
        with redirect_stdout(io.StringIO()):
            self.assertEqual(1, auto.main())
        self.instance.install.assert_not_called()
        self.instance.recover.assert_not_called()
        self.assertTrue((self.root / 'data/admin/server-updates/pending').is_file())

    def test_completed_run_checks_status_without_reinstall(self):
        self.instance.already_installed.return_value = True
        with redirect_stdout(io.StringIO()):
            self.assertEqual(0, auto.main())
        self.instance.install.assert_not_called()
        self.instance.prepare_proxy.assert_not_called()

    def test_recover_handles_mount_migration_without_application_pending(self):
        pending = self.root / '.portkill-security/mount-migrations/pending'
        pending.parent.mkdir(parents=True)
        pending.write_text('b' * 32)
        with patch.object(sys, 'argv', ['automatic', '--recover']), redirect_stdout(io.StringIO()):
            self.assertEqual(0, auto.main())
        self.instance.recover_proxy.assert_called_once()
        self.instance.prepare_proxy.assert_not_called()
        self.instance.install.assert_not_called()


class BootstrapTests(unittest.TestCase):
    def execute_bootstrap(self, digest):
        previous = Path.cwd()
        try:
            exec(compile(builder.BOOTSTRAP.replace('PAYLOAD_DIGEST', digest), 'bootstrap', 'exec'), {})
        finally:
            os.chdir(previous)

    def test_self_extracting_header_check_and_corruption_rejection(self):
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            archive = folder / 'payload.zip'
            with zipfile.ZipFile(archive, 'w') as zip:
                zip.writestr('ASCII.txt', 'payload')
            package = folder / 'update.run'
            package.write_bytes(builder.header(builder.sha(archive)) + archive.read_bytes())
            with patch.object(sys, 'argv', ['bootstrap', str(package), '--check']), redirect_stdout(io.StringIO()) as output:
                with self.assertRaises(SystemExit) as result:
                    self.execute_bootstrap(builder.sha(archive))
                self.assertEqual(0, result.exception.code)
                self.assertIn('PASS', output.getvalue())
            package.write_bytes(package.read_bytes() + b'changed')
            with patch.object(sys, 'argv', ['bootstrap', str(package), '--check']), patch.object(sys, 'stderr', io.StringIO()):
                with self.assertRaises(SystemExit) as result:
                    self.execute_bootstrap(builder.sha(archive))
                self.assertEqual(1, result.exception.code)

    def test_correct_digest_does_not_allow_traversal_entry(self):
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            archive = folder / 'payload.zip'
            with zipfile.ZipFile(archive, 'w') as zip:
                zip.writestr('../escape', 'payload')
            package = folder / 'update.run'
            package.write_bytes(builder.header(builder.sha(archive)) + archive.read_bytes())
            with patch.object(sys, 'argv', ['bootstrap', str(package), '--check']), patch.object(sys, 'stderr', io.StringIO()):
                with self.assertRaises(SystemExit) as result:
                    self.execute_bootstrap(builder.sha(archive))
                self.assertEqual(1, result.exception.code)

    def test_extracted_child_has_an_explicit_existing_working_directory(self):
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            archive = folder / 'payload.zip'
            with zipfile.ZipFile(archive, 'w') as package:
                package.writestr('.portkill-server-install/automatic-server-update.py', 'pass')
            executable = folder / 'update.run'
            executable.write_bytes(builder.header(builder.sha(archive)) + archive.read_bytes())
            def run(command, **kwargs):
                child = Path(command[2])
                self.assertTrue(child.is_file())
                self.assertEqual(child.parent.parent, Path(kwargs['cwd']))
                self.assertEqual(folder.resolve(), Path.cwd())
                return Mock(returncode=0)
            with patch.object(sys, 'argv', ['bootstrap', str(executable)]), \
                    patch.object(builder.shutil, 'which', return_value='docker'), \
                    patch('os.geteuid', return_value=0, create=True), \
                    patch('os.uname', return_value=Mock(machine='x86_64'), create=True), \
                    patch.object(sys, 'platform', 'linux'), \
                    patch('subprocess.run', side_effect=run) as process, redirect_stdout(io.StringIO()):
                with self.assertRaises(SystemExit) as result:
                    self.execute_bootstrap(builder.sha(archive))
            self.assertEqual(0, result.exception.code)
            process.assert_called_once()


if __name__ == '__main__':
    unittest.main()
