"""Reconstruct a verified automatic package from the uploaded base and a small delta."""
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import stat
import subprocess
import sys
import tempfile
import zipfile


def sha(path):
    digest = hashlib.sha256()
    with Path(path).open('rb') as source:
        for block in iter(lambda: source.read(1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()


def safe_name(name):
    if not re.fullmatch(r'[A-Za-z0-9._/-]+', name) or any(part in ('', '.', '..') for part in name.split('/')):
        raise ValueError('Invalid repair path')
    return name


def repair(base_directory, patch_directory, arguments):
    if arguments not in ([], ['--recover']):
        raise ValueError('Use no arguments or --recover')
    specification = json.loads((patch_directory / 'repair.json').read_text(encoding='utf-8'))
    base_name = safe_name(specification['baseFile'])
    if '/' in base_name:
        raise ValueError('Base package must be beside the repair file')
    base = base_directory / base_name
    if not base.is_file():
        raise ValueError('请将原升级包保留在修复包同一目录：' + base_name)
    with base.open('rb') as source:
        digest = hashlib.sha256()
        for block in iter(lambda: source.read(1024 * 1024), b''):
            digest.update(block)
        if digest.hexdigest() != specification['baseSha256']:
            raise ValueError('原升级包摘要不符，未修改站点；请使用对应版本的完整升级包')
        with zipfile.ZipFile(source) as package:
            entries = package.infolist()
            names = [safe_name(item.filename) for item in entries]
            if len(names) > 10000 or len(names) != len(set(names)):
                raise ValueError('Invalid base package inventory')
            if any(item.is_dir() or stat.S_ISLNK(item.external_attr >> 16) for item in entries):
                raise ValueError('Invalid base package entry')
            expanded = sum(item.file_size for item in entries)
            if expanded > 2 * 1024**3 or shutil.disk_usage(base_directory).free < expanded + 1024**3:
                raise ValueError('Insufficient space to reconstruct the package')
            print('原 r2 包校验通过，正在合成修订版；原部署尚未修改……', flush=True)
            with tempfile.TemporaryDirectory(prefix='.portkill-repair-', dir=base_directory) as temporary:
                target = Path(temporary)
                for item in entries:
                    path = target / item.filename
                    path.parent.mkdir(parents=True, exist_ok=True)
                    with package.open(item) as content, path.open('xb') as output:
                        shutil.copyfileobj(content, output, 1024 * 1024)
                    path.chmod(0o600)
                for item in specification['replacements']:
                    name = safe_name(item['path'])
                    replacement = patch_directory / 'files' / name
                    if sha(replacement) != item['sha256']:
                        raise ValueError('Repair file checksum mismatch')
                    destination = target / name
                    destination.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copyfile(replacement, destination)
                    destination.chmod(0o600)
                manifest_file = target / 'automatic-manifest.json'
                if sha(manifest_file) != specification['manifestSha256']:
                    raise ValueError('Reconstructed manifest mismatch')
                manifest = json.loads(manifest_file.read_text(encoding='utf-8'))
                inventory = {'automatic-manifest.json'}
                for item in manifest['files']:
                    name = safe_name(item['path'])
                    if name in inventory:
                        raise ValueError('Duplicate reconstructed entry')
                    inventory.add(name)
                    path = target / name
                    if path.stat().st_size != item['bytes'] or sha(path) != item['sha256']:
                        raise ValueError('Reconstructed package checksum mismatch: ' + name)
                if {p.relative_to(target).as_posix() for p in target.rglob('*') if p.is_file()} != inventory:
                    raise ValueError('Reconstructed package inventory mismatch')
                print('修订版文件全部校验通过，开始自动升级。', flush=True)
                return subprocess.run([sys.executable, '-u', str(target / '.portkill-server-install/automatic-server-update.py'),
                                       *arguments], cwd=target).returncode


def main():
    os.umask(0o077)
    script = Path(__file__).resolve()
    try:
        return repair(script.parents[2], script.parent, sys.argv[1:])
    except Exception as error:
        print('修复升级未完成：' + str(error), flush=True)
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
