"""Build/sign a single offline server-update ZIP. Requires Docker + cryptography; no production access."""
import argparse
import gzip
import hashlib
import json
import re
import shutil
import subprocess
import tempfile
import zipfile
from pathlib import Path

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import padding, rsa

REPOSITORY = Path(__file__).resolve().parents[2]


def digest(path):
    with path.open('rb') as source:
        return hashlib.file_digest(source, 'sha256').hexdigest()


def manifest_bytes(version, fingerprint, app_image, runner_image, files, docker_bytes=1):
    if not re.fullmatch(r'\d+\.\d+\.\d+(?:\.\d+)?', version):
        raise ValueError('Use a numeric release version')
    if not re.fullmatch(r'[a-f0-9]{64}', fingerprint):
        raise ValueError('Missing database fingerprint; rebuild the server bundle')
    return json.dumps(dict(schema=1, kind='portkill-server-update', version=version, runtime='linux-x64',
                          databaseChanges=False, databaseFingerprint=fingerprint, applicationImage=app_image,
                          runnerImage=runner_image, files=files, dockerBytes=docker_bytes), ensure_ascii=False, separators=(',', ':')).encode('utf-8')


def sign(payload, private_key):
    key = serialization.load_pem_private_key(private_key.read_bytes(), password=None)
    if not isinstance(key, rsa.RSAPrivateKey) or key.key_size < 2048:
        raise ValueError('An RSA signing key of at least 2048 bits is required')
    return key.sign(payload, padding.PKCS1v15(), hashes.SHA256())


def save_image(image, target):
    process = subprocess.Popen(['docker', 'save', image], stdout=subprocess.PIPE)
    try:
        with gzip.open(target, 'wb', compresslevel=6) as output:
            shutil.copyfileobj(process.stdout, output, 128 * 1024)
        if process.wait() != 0:
            raise RuntimeError('docker save failed')
    finally:
        if process.poll() is None:
            process.kill()
        process.stdout.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--bundle', type=Path, required=True)
    parser.add_argument('--private-key', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--version', required=True, help='Independent server release version')
    args = parser.parse_args()
    bundle, output = args.bundle.resolve(strict=True), args.output.resolve()
    if output.exists() or args.private_key.resolve().is_relative_to(bundle):
        raise SystemExit('Output must be new; signing keys must stay outside the bundle')
    metadata = json.loads((bundle / 'bundle.json').read_text(encoding='utf-8-sig'))
    if metadata['runtime'] != 'linux-x64' or not metadata['selfContained']:
        raise SystemExit('Requires a self-contained linux-x64 bundle')
    for item in metadata['files']:
        path = (bundle / item['path']).resolve(strict=True)
        if not path.is_relative_to(bundle) or path.is_symlink() or digest(path) != item['sha256']:
            raise SystemExit('Bundle integrity check failed')
    for file in bundle.rglob('*'):
        if file.is_symlink() or file.name == '.env' or file.suffix.lower() in ('.pem', '.key', '.pfx', '.p12'):
            raise SystemExit('Private or linked files cannot be packaged')
    suffix = hashlib.sha256((args.version + digest(bundle / 'bundle.json')).encode()).hexdigest()[:16]
    app_image, runner_image = 'portkill-update-server:server-' + suffix, 'portkill-server-update-runner:' + suffix
    # Validate version/fingerprint before expensive image builds.
    manifest_bytes(args.version, metadata.get('databaseFingerprint', ''), app_image, runner_image, [])
    subprocess.run(['docker', 'info'], check=True, stdout=subprocess.DEVNULL)
    with tempfile.TemporaryDirectory(prefix='portkill-server-build-') as temporary:
        root = Path(temporary)
        context, payload = root / 'context', root / 'payload'
        shutil.copytree(bundle, context / 'server-bundle')
        (payload / 'images').mkdir(parents=True)
        for dockerfile, image in [('Application.Dockerfile', app_image), ('Dockerfile', runner_image)]:
            subprocess.run(['docker', 'build', '--platform', 'linux/amd64', '-f',
                            str(REPOSITORY / 'deploy/docker/server-update' / dockerfile), '-t', image, str(context)], check=True)
        save_image(app_image, payload / 'images/application.tar.gz')
        save_image(runner_image, payload / 'images/runner.tar.gz')
        shutil.copytree(bundle / 'web', payload / 'web')
        files = [dict(path=p.relative_to(payload).as_posix(), bytes=p.stat().st_size, sha256=digest(p))
                 for p in sorted(payload.rglob('*')) if p.is_file()]
        docker_bytes = sum(int(subprocess.check_output(['docker', 'image', 'inspect', '--format', '{{.Size}}', image], text=True))
                           for image in (app_image, runner_image))
        manifest = manifest_bytes(args.version, metadata['databaseFingerprint'], app_image, runner_image, files, docker_bytes)
        (payload / 'server-manifest.json').write_bytes(manifest)
        (payload / 'server-manifest.sig').write_bytes(sign(manifest, args.private_key))
        output.parent.mkdir(parents=True, exist_ok=True)
        partial = output.with_name(output.name + '.partial')
        try:
            with zipfile.ZipFile(partial, 'w', compression=zipfile.ZIP_DEFLATED, allowZip64=True) as archive:
                for file in sorted(payload.rglob('*')):
                    if file.is_file():
                        archive.write(file, file.relative_to(payload).as_posix())
            partial.rename(output)
        finally:
            partial.unlink(missing_ok=True)
    print(json.dumps(dict(package=str(output), bytes=output.stat().st_size, sha256=digest(output),
                          applicationImage=app_image, runnerImage=runner_image)))


if __name__ == '__main__':
    main()
