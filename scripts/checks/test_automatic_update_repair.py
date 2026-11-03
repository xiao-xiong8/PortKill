"""Verify a delta reconstructs exactly the checksum inventory before execution."""
from contextlib import redirect_stdout
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock, patch
import zipfile

ROOT = Path(__file__).resolve().parents[2]


def load(name, relative):
    spec = importlib.util.spec_from_file_location(name, ROOT / relative)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


repair = load('repair', 'deploy/docker/package/repair-automatic-update.py')
builder = load('repair_builder', 'scripts/release/New-AutomaticDockerRepair.py')


class RepairTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name).resolve()
        self.base = self.root / 'base.run'
        self.updated = self.root / 'updated.run'
        self.contents = {'.portkill-server-install/install.py': b'new verified installer',
                         '.portkill-server-install/server-bundle/api/libhostfxr.so': b'unchanged binary',
                         '.portkill-server-install/scripts/new-migration.py': b'new migration'}
        def inventory(contents):
            result = dict(contents)
            result['automatic-manifest.json'] = json.dumps(dict(files=[dict(path=n, bytes=len(c), sha256=hashlib.sha256(c).hexdigest()) for n, c in contents.items()])).encode()
            return result
        self.expected = inventory(self.contents)
        previous = dict(self.contents)
        del previous['.portkill-server-install/scripts/new-migration.py']
        previous['.portkill-server-install/install.py'] = b'old installer'
        for target, files in ((self.base, inventory(previous)), (self.updated, self.expected)):
            with zipfile.ZipFile(target, 'w') as package:
                for name, content in files.items():
                    package.writestr(name, content)
        self.output = self.root / 'repair.run'
        self.work = self.root / 'work'
        with redirect_stdout(io.StringIO()):
            builder.build(self.base, self.updated, self.output, self.work)
        self.patch = self.work / 'payload/.portkill-server-install'

    def test_complete_reconstruction_matches_updated_package_and_executes_in_existing_directory(self):
        def run(command, **kwargs):
            folder = Path(kwargs['cwd'])
            self.assertEqual(folder, Path(command[2]).parent.parent)
            self.assertEqual(self.expected, {p.relative_to(folder).as_posix(): p.read_bytes() for p in folder.rglob('*') if p.is_file()})
            self.assertEqual('--recover', command[-1])
            return Mock(returncode=7)
        with patch.object(repair.subprocess, 'run', side_effect=run) as process, redirect_stdout(io.StringIO()):
            self.assertEqual(7, repair.repair(self.root, self.patch, ['--recover']))
        process.assert_called_once()
        with zipfile.ZipFile(self.output) as package:
            self.assertIsNone(package.testzip())
            self.assertFalse(any(n.endswith('libhostfxr.so') for n in package.namelist()))

    def test_changed_or_missing_base_never_executes_child(self):
        with patch.object(repair.subprocess, 'run') as process:
            self.base.write_bytes(self.base.read_bytes() + b'changed')
            with self.assertRaisesRegex(ValueError, '摘要不符'):
                repair.repair(self.root, self.patch, [])
            self.base.unlink()
            with self.assertRaisesRegex(ValueError, '同一目录'):
                repair.repair(self.root, self.patch, [])
        process.assert_not_called()

    def test_changed_replacement_never_executes_child(self):
        replacement = self.patch / 'files/.portkill-server-install/install.py'
        replacement.write_bytes(b'tampered')
        with patch.object(repair.subprocess, 'run') as process, redirect_stdout(io.StringIO()):
            with self.assertRaisesRegex(ValueError, 'checksum mismatch'):
                repair.repair(self.root, self.patch, [])
        process.assert_not_called()

    def test_paths_cannot_escape_package(self):
        for name in ('../x', '/absolute', 'a//b', './x', 'a/../b', 'a\\b'):
            with self.subTest(name=name), self.assertRaises(ValueError):
                repair.safe_name(name)


if __name__ == '__main__':
    unittest.main()
