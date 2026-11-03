"""Build a small self-extracting repair that reuses an exact uploaded .run package."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import zipfile

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('automatic_builder', Path(__file__).with_name('New-AutomaticDockerPackage.py'))
builder = importlib.util.module_from_spec(spec)
spec.loader.exec_module(builder)


def build(base, updated, output, work):
    base, updated = base.resolve(strict=True), updated.resolve(strict=True)
    output, work = output.resolve(), work.resolve()
    if not base.name.isascii() or output.suffix != '.run' or not output.name.isascii() or output.exists() or work.exists() or Path(str(output) + '.sha256').exists():
        raise ValueError('Choose a new ASCII .run output and work directory')
    stage = work / 'payload/.portkill-server-install'
    stage.mkdir(parents=True)
    (stage / 'automatic-server-update.py').write_text((ROOT / 'deploy/docker/package/repair-automatic-update.py').read_text(encoding='utf-8'), encoding='utf-8', newline='\n')
    replacements = []
    with zipfile.ZipFile(base) as previous, zipfile.ZipFile(updated) as current:
        if not set(previous.namelist()).issubset(set(current.namelist())):
            raise ValueError('Repair does not remove base package files')
        for name in sorted(current.namelist()):
            content = current.read(name)
            if name not in previous.namelist() or hashlib.sha256(previous.read(name)).digest() != hashlib.sha256(content).digest():
                target = stage / 'files' / name
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(content)
                replacements.append(dict(path=name, sha256=hashlib.sha256(content).hexdigest()))
        specification = dict(baseFile=base.name, baseSha256=builder.sha(base),
                             manifestSha256=hashlib.sha256(current.read('automatic-manifest.json')).hexdigest(),
                             replacements=replacements)
    if not replacements:
        raise ValueError('No changed files to repair')
    (stage / 'repair.json').write_text(json.dumps(specification, separators=(',', ':')), encoding='utf-8')
    archive = work / 'payload.zip'
    with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as package:
        for file in sorted((work / 'payload').rglob('*')):
            if file.is_file():
                package.write(file, file.relative_to(work / 'payload').as_posix())
    prefix = builder.header(builder.sha(archive))
    (work / 'header.sh').write_bytes(prefix)
    with output.open('xb') as target:
        target.write(prefix + archive.read_bytes())
    output.chmod(0o755)
    with zipfile.ZipFile(output) as package:
        if package.testzip() is not None:
            raise ValueError('Repair ZIP integrity failure')
    digest = builder.sha(output)
    Path(str(output) + '.sha256').write_text(digest + '  ' + output.name + '\n', encoding='ascii')
    result = dict(package=str(output), bytes=output.stat().st_size, sha256=digest, baseFile=base.name, changedFiles=len(replacements), dockerRuntimeTested=False)
    (work / 'build.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps(result, ensure_ascii=False))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('base', 'updated', 'output', 'work'):
        parser.add_argument('--' + name, type=Path, required=True)
    args = parser.parse_args()
    build(args.base, args.updated, args.output, args.work)
