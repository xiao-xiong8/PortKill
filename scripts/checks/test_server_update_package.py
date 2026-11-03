"""Offline package/signature/bootstrap checks. Uses fresh test keys and never calls Docker."""
import hashlib
import importlib.util
import io
import json
import tarfile
import tempfile
import unittest
from unittest.mock import patch
from types import SimpleNamespace
import zipfile
from pathlib import Path

from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric import rsa

ROOT = Path(__file__).resolve().parents[2]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, ROOT / path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


builder = load('server_package', 'scripts/release/New-ServerUpdatePackage.py')
bootstrap = load('server_bootstrap', 'deploy/docker/package/prepare-server-update.py')


class ServerPackageTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
        self.private = self.root / 'private.pem'
        self.private.write_bytes(key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption()))
        self.config = self.root / 'verification.json'
        self.config.write_text(json.dumps({'PublicKeyPem': key.public_key().public_bytes(serialization.Encoding.PEM, serialization.PublicFormat.SubjectPublicKeyInfo).decode()}))

    def package(self, variant=''):
        image = 'portkill-server-update-runner:test'
        archive = io.BytesIO()
        with tarfile.open(fileobj=archive, mode='w:gz') as tar:
            data = json.dumps([{'RepoTags': [image if variant != 'tags' else 'unexpected:image']}]).encode()
            entry = tarfile.TarInfo('manifest.json')
            entry.size = len(data)
            tar.addfile(entry, io.BytesIO(data))
            if variant == 'link':
                entry = tarfile.TarInfo('link')
                entry.type, entry.linkname = tarfile.SYMTYPE, '/etc/passwd'
                tar.addfile(entry)
        runner = archive.getvalue()
        files = [{'path': 'images/runner.tar.gz', 'bytes': len(runner), 'sha256': hashlib.sha256(runner).hexdigest()}]
        manifest = builder.manifest_bytes('1.2.3', 'a' * 64, 'portkill-update-server:test', image, files, 1024)
        if variant == 'migration':
            value = json.loads(manifest)
            value['databaseChanges'] = True
            manifest = json.dumps(value).encode()
        signature = builder.sign(manifest, self.private)
        if variant == 'signature':
            signature = bytes([signature[0] ^ 1]) + signature[1:]
        if variant == 'digest':
            runner = runner[:-1] + bytes([runner[-1] ^ 1])
        package = self.root / 'update.zip'
        with zipfile.ZipFile(package, 'w') as zip:
            zip.writestr('server-manifest.json', manifest)
            zip.writestr('server-manifest.sig', signature)
            zip.writestr('images/runner.tar.gz', runner)
        return package

    def test_builder_signature_is_accepted_by_host_openssl_verifier(self):
        out = self.root / 'verified'
        out.mkdir()
        bootstrap.prepare(self.package(), self.config, out)
        self.assertEqual('portkill-server-update-runner:test', (out / 'runner-image').read_text())

    def test_signature_digest_migration_tags_and_links_rejected_before_import(self):
        for variant in ('signature', 'digest', 'migration', 'tags', 'link'):
            with self.subTest(variant=variant):
                out = self.root / variant
                out.mkdir()
                with self.assertRaises(Exception):
                    bootstrap.prepare(self.package(variant), self.config, out)
                self.assertFalse((out / 'runner-image').exists())

    def test_invalid_release_version_and_database_fingerprint_rejected(self):
        for version, fingerprint in [('v1.0', 'a' * 64), ('1.0.0', ''), ('1.0.0', 'x' * 64)]:
            with self.subTest(version=version, fingerprint=fingerprint):
                with self.assertRaises(ValueError):
                    builder.manifest_bytes(version, fingerprint, 'app:test', 'runner:test', [])

    def test_full_disk_rejected_before_runner_archive_is_written(self):
        out = self.root / 'low-space'
        out.mkdir()
        package = self.package()
        with patch.object(bootstrap.shutil, 'disk_usage', return_value=SimpleNamespace(free=0)):
            with self.assertRaisesRegex(ValueError, 'Insufficient disk space'):
                bootstrap.prepare(package, self.config, out)
        self.assertFalse((out / 'runner.tar.gz').exists())
        self.assertFalse((out / 'runner-image').exists())

    def test_space_requirements_sum_shared_partition_and_check_separate_docker_partition(self):
        with patch.object(bootstrap.os, 'stat', return_value=SimpleNamespace(st_dev=1)), \
             patch.object(bootstrap.shutil, 'disk_usage', return_value=SimpleNamespace(free=100)):
            with self.assertRaises(ValueError):
                bootstrap.ensure_space([('/temp', 60), ('/admin', 60)], reserve=0)
        with patch.object(bootstrap.os, 'stat', side_effect=lambda path: SimpleNamespace(st_dev=1 if path == '/admin' else 2)), \
             patch.object(bootstrap.shutil, 'disk_usage', side_effect=lambda path: SimpleNamespace(free=1000 if path == '/admin' else 10)):
            with self.assertRaises(ValueError):
                bootstrap.ensure_space([('/admin', 60), ('/docker', 60)], reserve=0)


if __name__ == '__main__':
    unittest.main()
