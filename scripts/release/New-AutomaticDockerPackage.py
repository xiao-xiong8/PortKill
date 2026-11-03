"""Build a single-command SSH maintenance package; no Docker or signing key is embedded."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import zipfile

ROOT = Path(__file__).resolve().parents[2]

BOOTSTRAP = r'''import hashlib
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

def main():
    archive = Path(sys.argv[1]).resolve(strict=True)
    os.chdir(archive.parent)
    arguments = sys.argv[2:]
    if arguments not in ([], ['--check'], ['--recover']):
        raise ValueError('Use no arguments, --check, or --recover')
    digest = hashlib.sha256()
    with archive.open('rb') as source:
        for count in range(1000):
            if source.readline() == b'# PORTKILL_ARCHIVE_BELOW\n':
                break
        else:
            raise ValueError('Package header is damaged')
        for block in iter(lambda: source.read(1024 * 1024), b''):
            digest.update(block)
    if digest.hexdigest() != 'PAYLOAD_DIGEST':
        raise ValueError('Package is incomplete or changed; upload the complete file again')
    with zipfile.ZipFile(archive) as package:
        entries = package.infolist()
        names = [entry.filename for entry in entries]
        if len(names) > 10000 or len(names) != len(set(names)):
            raise ValueError('Invalid package inventory')
        for entry in entries:
            name = entry.filename
            if not re.fullmatch(r'[A-Za-z0-9._/-]+', name) or any(part in ('', '.', '..') for part in name.split('/')):
                raise ValueError('Invalid package path')
            if stat.S_ISLNK(entry.external_attr >> 16) or entry.is_dir():
                raise ValueError('Linked or unexpected package entry')
        expanded = sum(entry.file_size for entry in entries)
        if expanded > 2 * 1024**3:
            raise ValueError('Package is too large')
        if arguments == ['--check']:
            if package.testzip() is not None:
                raise ValueError('Package ZIP integrity failed')
            print('PASS: complete package digest, ASCII paths, and ZIP integrity; no deployment was modified.')
            return 0
        if os.geteuid() != 0 or sys.platform != 'linux' or os.uname().machine != 'x86_64' or sys.version_info < (3, 10):
            raise ValueError('Requires sudo, Linux x86-64, and Python 3.10 or later')
        if not shutil.which('docker'):
            raise ValueError('Docker is not installed')
        if shutil.disk_usage(archive.parent).free < expanded + 1024**3:
            raise ValueError('Insufficient space to unpack beside the uploaded file')
        print('Package verified. Preparing automatic upgrade...', flush=True)
        with tempfile.TemporaryDirectory(prefix='.portkill-unpack-', dir=archive.parent) as folder:
            for entry in entries:
                destination = Path(folder) / entry.filename
                destination.parent.mkdir(parents=True, exist_ok=True)
                with package.open(entry) as source, destination.open('xb') as output:
                    shutil.copyfileobj(source, output, 1024 * 1024)
                destination.chmod(0o600)
            return subprocess.run([sys.executable, '-u', str(Path(folder) / '.portkill-server-install/automatic-server-update.py'), *arguments], cwd=folder).returncode

try:
    raise SystemExit(main())
except Exception as error:
    print('Automatic update stopped: ' + str(error), file=sys.stderr, flush=True)
    raise SystemExit(1)
'''


def sha(path):
    with Path(path).open('rb') as source:
        return hashlib.file_digest(source, 'sha256').hexdigest()


def header(digest):
    shell = "#!/usr/bin/env bash\nset -Eeuo pipefail\nrun_file=\"${BASH_SOURCE[0]}\"\ncase \"$run_file\" in /*|[A-Za-z]:/*) ;; *) run_file=\"$(pwd -P)/$run_file\" ;; esac\ncd /\nexport PYTHONUTF8=1\ncommand -v python3 >/dev/null || { echo 'Python 3 is required'; exit 2; }\nexec python3 -u - \"$run_file\" \"$@\" <<'PORTKILL_AUTO_PY'\n"
    return (shell + BOOTSTRAP.replace('PAYLOAD_DIGEST', digest) + '\nPORTKILL_AUTO_PY\nexit 1\n# PORTKILL_ARCHIVE_BELOW\n').encode('utf-8')


def build(bundle, output, work):
    bundle = bundle.resolve(strict=True)
    output, work = output.resolve(), work.resolve()
    if output.suffix != '.run' or not output.name.isascii() or output.exists() or work.exists() or Path(str(output) + '.sha256').exists():
        raise ValueError('Choose new ASCII .run and work paths')
    metadata = json.loads((bundle / 'bundle.json').read_text(encoding='utf-8-sig'))
    if metadata['runtime'] != 'linux-x64' or not metadata['selfContained']:
        raise ValueError('Requires a self-contained Linux x64 bundle')
    for item in metadata['files']:
        path = (bundle / item['path']).resolve(strict=True)
        if not path.is_relative_to(bundle) or sha(path) != item['sha256']:
            raise ValueError('Bundle integrity mismatch: ' + item['path'])
    payload = work / 'payload'
    stage = payload / '.portkill-server-install'
    stage.mkdir(parents=True)
    shutil.copytree(bundle, stage / 'server-bundle')
    source = ROOT / 'deploy/docker/package'
    for original, target in [('install-server-in-place.py', 'install.py'), ('automatic-server-update.py', 'automatic-server-update.py'),
                             ('docker-admin-security.py', 'scripts/docker-admin-security.py'),
                             ('caddy-directory-migration.py', 'scripts/caddy-directory-migration.py'),
                             ('configure-admin-security.py', 'scripts/configure-admin-security.py'),
                             ('enable-client-reports.py', 'scripts/enable-client-reports.py')]:
        destination = stage / target
        destination.parent.mkdir(exist_ok=True)
        destination.write_text((source / original).read_text(encoding='utf-8-sig'), encoding='utf-8', newline='\n')
    dockerfile = ROOT / 'deploy/docker/server-update/Application.Dockerfile'
    shutil.copyfile(dockerfile, stage / 'Application.Dockerfile')
    suffix = hashlib.sha256((sha(bundle / 'bundle.json') + sha(dockerfile)).encode()).hexdigest()[:16]
    files = sorted(path for path in payload.rglob('*') if path.is_file())
    for path in files:
        name = path.relative_to(payload).as_posix()
        if not re.fullmatch(r'[A-Za-z0-9._/-]+', name) or path.suffix.lower() in ('.pem', '.key', '.pfx', '.p12') or path.name in ('.env', 'verification.json'):
            raise ValueError('Unexpected file in automatic package: ' + name)
    manifest = dict(schema=1, kind='portkill-automatic-install', version=metadata['version'], runtime='linux-x64',
                    applicationImage='portkill-update-server:install-' + suffix, sourceCommit=metadata['commit'], sourceDirty=metadata['dirty'],
                    files=[dict(path=path.relative_to(payload).as_posix(), bytes=path.stat().st_size, sha256=sha(path)) for path in files])
    (payload / 'automatic-manifest.json').write_text(json.dumps(manifest, ensure_ascii=True, separators=(',', ':')), encoding='utf-8')
    archive = work / 'payload.zip'
    with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED, compresslevel=6, allowZip64=True) as package:
        for path in sorted(payload.rglob('*')):
            if path.is_file():
                package.write(path, path.relative_to(payload).as_posix())
    output.parent.mkdir(parents=True, exist_ok=True)
    prefix = header(sha(archive))
    (work / 'header.sh').write_bytes(prefix)
    with output.open('xb') as target, archive.open('rb') as content:
        target.write(prefix)
        shutil.copyfileobj(content, target, 1024 * 1024)
    output.chmod(0o755)
    with zipfile.ZipFile(output) as check:
        if check.testzip() is not None:
            raise ValueError('Self-extracting package integrity failure')
    digest = sha(output)
    Path(str(output) + '.sha256').write_text(digest + '  ' + output.name + '\n', encoding='ascii')
    result = dict(package=str(output), bytes=output.stat().st_size, sha256=digest, files=len(files) + 1, checksumOnly=True, dockerRuntimeTested=False)
    (work / 'build.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps(result, ensure_ascii=False))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--bundle', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--work', type=Path, required=True)
    args = parser.parse_args()
    build(args.bundle, args.output, args.work)
