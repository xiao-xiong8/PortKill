"""Check deployment filename portability without building applications or calling Docker."""
import importlib.util
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('final_docker_package', ROOT / 'scripts/release/New-FinalDockerPackage.py')
builder = importlib.util.module_from_spec(spec)
spec.loader.exec_module(builder)


class DeploymentFilenameTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.source = Path(temporary.name) / 'source'
        self.destination = Path(temporary.name) / 'destination'
        self.source.mkdir()
        self.destination.mkdir()

    def test_ascii_names_preserve_chinese_contents_and_update_references(self):
        (self.source / '使用说明.md').write_bytes('部署说明\r\n请读验证记录.md\r\n'.encode('utf-8'))
        (self.source / '验证记录.md').write_text('按使用说明.md验收。', encoding='utf-8')
        (self.source / 'GUIDE.txt').write_text('使用说明.md；验证记录.md', encoding='utf-8')
        (self.source / 'install.sh').write_bytes(b'#!/bin/sh\r\nexit 0\r\n')
        (self.source / 'helper.py').write_bytes(b'print(1)\r\n')
        builder.copy_deployment_files(self.source, self.destination)
        self.assertEqual({'README.md', 'VERIFICATION.md', 'GUIDE.txt', 'install.sh', 'helper.py'},
                         {path.name for path in self.destination.iterdir()})
        self.assertEqual('部署说明\r\n请读VERIFICATION.md\r\n'.encode('utf-8'),
                         (self.destination / 'README.md').read_bytes())
        self.assertEqual('按README.md验收。', (self.destination / 'VERIFICATION.md').read_text(encoding='utf-8'))
        self.assertEqual('README.md；VERIFICATION.md', (self.destination / 'GUIDE.txt').read_text(encoding='utf-8'))
        self.assertEqual(b'#!/bin/sh\nexit 0\n', (self.destination / 'install.sh').read_bytes())
        self.assertEqual(b'print(1)\r\n', (self.destination / 'helper.py').read_bytes())

    def test_unmapped_non_ascii_filename_is_rejected(self):
        (self.source / '额外文件.txt').write_text('test', encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'ASCII mapping'):
            builder.copy_deployment_files(self.source, self.destination)

    def test_existing_destination_is_not_overwritten(self):
        (self.source / '使用说明.md').write_text('new', encoding='utf-8')
        (self.destination / 'README.md').write_text('existing', encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'Duplicate deployment filename'):
            builder.copy_deployment_files(self.source, self.destination)
        self.assertEqual('existing', (self.destination / 'README.md').read_text(encoding='utf-8'))


if __name__ == '__main__':
    unittest.main()
