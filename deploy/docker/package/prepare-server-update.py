"""Host bootstrap trust check. Uses stdlib and openssl; never executes package scripts."""
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tarfile
import zipfile
from pathlib import Path


def ensure_space(requirements, reserve=1024**3):
    """Sum simultaneous writes per physical filesystem, including bind-mounted paths."""
    devices = {}
    for path, size in requirements:
        device = os.stat(path).st_dev
        free = shutil.disk_usage(path).free
        previous_size, previous_free = devices.get(device, (0, free))
        devices[device] = (previous_size + size, min(previous_free, free))
    if any(free < required + reserve for required, free in devices.values()):
        raise ValueError('Insufficient disk space before extraction/import; no image has been loaded')


def prepare(package, config, destination, deployment_root=None, docker_root=None):
    def unique(items):
        result = {}
        for key, value in items:
            if key in result:
                raise ValueError('Duplicate JSON property')
            result[key] = value
        return result
    with zipfile.ZipFile(package) as archive:
        names = [e.filename for e in archive.infolist()]
        if len(names) > 10000 or len(set(names)) != len(names):
            raise ValueError('Invalid ZIP entries')
        entry = archive.getinfo('server-manifest.json')
        if entry.file_size > 1024 * 1024 or archive.getinfo('server-manifest.sig').file_size > 8192:
            raise ValueError('Manifest is too large')
        payload = archive.read(entry)
        root = Path(destination)
        manifest_file, signature_file, public_file = root / 'manifest.json', root / 'manifest.sig', root / 'public.pem'
        manifest_file.write_bytes(payload)
        signature_file.write_bytes(archive.read('server-manifest.sig'))
        public = json.loads(Path(config).read_text(encoding='utf-8-sig'))
        public_file.write_text(next(v for k, v in public.items() if k.lower() == 'publickeypem'), encoding='utf-8')
        subprocess.run(['openssl', 'dgst', '-sha256', '-verify', str(public_file), '-signature', str(signature_file), str(manifest_file)], check=True, stdout=subprocess.DEVNULL)
        manifest = json.loads(payload, object_pairs_hook=unique)
        if manifest['kind'] != 'portkill-server-update' or manifest['schema'] != 1 or manifest['runtime'] != 'linux-x64' or manifest['databaseChanges'] is not False:
            raise ValueError('Unsupported server package')
        image = manifest['runnerImage']
        if not re.fullmatch(r'portkill-server-update-runner:[A-Za-z0-9][A-Za-z0-9._-]{0,127}', image):
            raise ValueError('Invalid runner image')
        signed_file = next(f for f in manifest['files'] if f['path'] == 'images/runner.tar.gz')
        if not 0 < signed_file['bytes'] <= 2 * 1024**3:
            raise ValueError('Runner image is too large')
        requirements = [(root, signed_file['bytes'])]
        if deployment_root is not None and docker_root is not None:
            deployment = Path(deployment_root)
            admin = deployment / 'data/admin'
            expanded = sum(file['bytes'] for file in manifest['files'])
            docker_bytes = manifest['dockerBytes']
            if not 0 < expanded <= 6 * 1024**3 or not 0 < docker_bytes <= 24 * 1024**3:
                raise ValueError('Invalid expanded image sizes')
            backup = sum(file.stat().st_size for file in (admin / 'admin.sqlite', admin / 'admin.sqlite-wal') if file.is_file())
            backup += sum(file.stat().st_size for file in (admin / 'auth-keys').glob('*.xml'))
            web = sum(file.stat().st_size for file in (deployment / 'server-bundle/web').rglob('*') if file.is_file())
            requirements += [(admin, expanded + backup * 2 + web * 3), (deployment, expanded + web), (Path(docker_root), docker_bytes * 2)]
        ensure_space(requirements)
        target = root / 'runner.tar.gz'
        digest = hashlib.sha256()
        count = 0
        with archive.open('images/runner.tar.gz') as source, target.open('xb') as output:
            while block := source.read(128 * 1024):
                count += len(block)
                if count > signed_file['bytes']:
                    raise ValueError('Runner size mismatch')
                digest.update(block)
                output.write(block)
        if count != signed_file['bytes'] or digest.hexdigest() != signed_file['sha256']:
            raise ValueError('Runner digest mismatch')
        with tarfile.open(target, 'r|gz') as tar:
            found = False
            expanded = 0
            for item in tar:
                expanded += item.size
                if expanded > 6 * 1024**3 or not (item.isfile() or item.isdir()) or not re.fullmatch(r'[A-Za-z0-9._/-]+', item.name) or any(part in ('', '.', '..') for part in item.name.rstrip('/').split('/')):
                    raise ValueError('Unsafe or oversized runner archive')
                if item.name == 'manifest.json':
                    if found or item.size > 1024 * 1024:
                        raise ValueError('Invalid Docker manifest')
                    data = json.load(tar.extractfile(item))
                    if len(data) != 1 or data[0].get('RepoTags') != [image]:
                        raise ValueError('Unexpected image tags in runner archive')
                    found = True
            if not found:
                raise ValueError('Docker save manifest is required')
        (root / 'runner-image').write_text(image, encoding='ascii')


if __name__ == '__main__':
    if len(sys.argv) not in (4, 6):
        raise SystemExit('Usage: prepare-server-update.py package.zip verification.json private-temp-dir [deployment-root docker-root]')
    prepare(*sys.argv[1:])
