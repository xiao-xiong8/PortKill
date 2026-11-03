"""Sign a precompiled Docker install kit that can be unzipped in the existing deployment directory."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import zipfile

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import padding

REPOSITORY = Path(__file__).resolve().parents[2]


def digest(path):
    with path.open('rb') as source:
        return hashlib.file_digest(source, 'sha256').hexdigest()


def runner_dockerfile(application_image):
    if not re.fullmatch(r'portkill-update-server:install-[a-f0-9]{16}', application_image):
        raise ValueError('Runner must use the exact application image from this install kit')
    # The installer builds the application first. Reuse its libraries and Docker CLI layers locally.
    return f'''FROM {application_image}
USER 0:0
WORKDIR /opt/portkill
COPY server-bundle/runner/ runner/
RUN chmod +x runner/PortKiller.ServerUpdateRunner
ENTRYPOINT ["/opt/portkill/runner/PortKiller.ServerUpdateRunner"]
'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--bundle', type=Path, required=True)
    parser.add_argument('--private-key', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--version', required=True)
    args = parser.parse_args()
    bundle = args.bundle.resolve(strict=True)
    output = args.output.resolve()
    if output.exists() or output.with_suffix('.zip').exists() or args.private_key.resolve().is_relative_to(bundle):
        raise SystemExit('Choose a new output path and keep signing keys outside the bundle')
    metadata = json.loads((bundle / 'bundle.json').read_text(encoding='utf-8-sig'))
    if metadata['runtime'] != 'linux-x64' or not metadata['selfContained']:
        raise ValueError('Requires a self-contained Linux x64 bundle')
    for item in metadata['files']:
        file = (bundle / item['path']).resolve(strict=True)
        if not file.is_relative_to(bundle) or digest(file) != item['sha256']:
            raise ValueError('Bundle integrity mismatch: ' + item['path'])
    output.mkdir(parents=True)
    stage = output / '.portkill-server-install'
    shutil.copytree(bundle, stage / 'server-bundle')
    source = REPOSITORY / 'deploy/docker/package'
    for original, target in [('install-server-in-place.py', '.portkill-server-install/install.py'),
                             ('docker-server-install.sh', 'docker-server-install.sh'),
                             ('docker-server-update-bootstrap.sh', 'docker-server-update-bootstrap.sh'),
                             ('docker-server-update-recover.sh', 'docker-server-update-recover.sh'),
                             ('prepare-server-update.py', 'prepare-server-update.py'), ('SERVER-UPDATE.txt', 'SERVER-UPDATE.txt')]:
        file = output / target
        file.parent.mkdir(parents=True, exist_ok=True)
        file.write_text((source / original).read_text(encoding='utf-8-sig'), encoding='utf-8', newline='\n')
    suffix = digest(bundle / 'bundle.json')[:16]
    application_image = 'portkill-update-server:install-' + suffix
    (stage / 'Application.Dockerfile').write_text((REPOSITORY / 'deploy/docker/server-update/Application.Dockerfile').read_text(encoding='utf-8-sig'), encoding='utf-8', newline='\n')
    (stage / 'Runner.Dockerfile').write_text(runner_dockerfile(application_image), encoding='utf-8', newline='\n')
    files = sorted(file for file in output.rglob('*') if file.is_file())
    for file in files:
        if file.name in ('.env', 'verification.json') or file.suffix.lower() in ('.pem', '.key', '.pfx', '.p12') or 'data' in file.relative_to(output).parts:
            raise ValueError('Private deployment file in package')
    manifest = dict(schema=1, kind='portkill-in-place-install', version=args.version, runtime='linux-x64', databaseFingerprint=metadata['databaseFingerprint'],
                    applicationImage=application_image, runnerImage='portkill-server-update-runner:install-' + suffix,
                    files=[dict(path=file.relative_to(output).as_posix(), bytes=file.stat().st_size, sha256=digest(file)) for file in files])
    payload = json.dumps(manifest, ensure_ascii=False, separators=(',', ':')).encode()
    key = serialization.load_pem_private_key(args.private_key.read_bytes(), password=None)
    (stage / 'install-manifest.json').write_bytes(payload)
    (stage / 'install-manifest.sig').write_bytes(key.sign(payload, padding.PKCS1v15(), hashes.SHA256()))
    readme = f'''PortKill Docker 原目录安装包 {args.version}

适用：已有 Ubuntu x86-64 Docker Compose 部署。
把 ZIP 上传到 /home/ubuntu/PortKill-Docker-Kit-20261026，然后执行：

cd /home/ubuntu/PortKill-Docker-Kit-20261026
unzip -o {output.name}.zip
sudo bash ./docker-server-install.sh . portkill-docker-kit-20261026

不需要新建其他部署目录，不需要自行合并 Compose。
包内无 .env、verification.json、数据库、认证密钥、证书或签名私钥。
解压仅放置新发布文件和安装脚本，运行安装脚本后才替换网站并重建业务容器。
本包包含预编译 Linux 程序；镜像在服务器构建，首次需要访问 Docker 基础镜像源。
Runner 复用本次已经构建的应用镜像，不重复下载 Debian 依赖；应用镜像的首次构建仍需要软件源。
脚本核对原部署公钥签名、文件摘要、Compose 项目及挂载，先构建、备份，再切换。
不会执行数据库迁移或自动恢复业务数据库；Caddy、Monitor 不重建。
成功后 Ctrl+F5 刷新原管理网址，管理员可在“服务器更新”页面上传后续签名离线更新 ZIP。

日志：当前目录 .portkill-server-install-logs/install-时间.log
备份：data/admin/server-install-backups/本次编号
不要删除 .portkill-server-install：当前 Compose 构建路径引用其中的发布文件。
失败时先保留日志；自动回退失败或断电中断后，执行：
sudo bash ./docker-server-install.sh . portkill-docker-kit-20261026 --recover

该 ZIP 是 SSH 安装包，不能上传到“服务器更新”页面。网页使用的签名镜像离线 ZIP
由 scripts/release/New-ServerUpdatePackage.py 在有 Docker 的发布机生成。
本地已验证程序构建和逻辑回归；未在 Ubuntu/Docker 实机运行该安装包。
'''
    (output / 'INSTALL.txt').write_text(readme, encoding='utf-8', newline='\n')
    files = sorted(file for file in output.rglob('*') if file.is_file())
    (output / 'INSTALL-SHA256SUMS.txt').write_text(''.join(f'{digest(file)}  {file.relative_to(output).as_posix()}\n' for file in files), encoding='ascii', newline='\n')
    archive = output.with_suffix('.zip')
    with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED, compresslevel=6, allowZip64=True) as zip:
        for file in sorted(output.rglob('*')):
            if file.is_file():
                info = zipfile.ZipInfo(file.relative_to(output).as_posix())
                info.create_system = 3
                info.external_attr = (0o100755 if file.suffix == '.sh' or file.name.startswith('PortKiller.') and not file.suffix else 0o100644) << 16
                info.compress_type = zipfile.ZIP_DEFLATED
                with file.open('rb') as source_file, zip.open(info, 'w', force_zip64=True) as target_file:
                    shutil.copyfileobj(source_file, target_file, 128 * 1024)
    with zipfile.ZipFile(archive) as check:
        if check.testzip() is not None:
            raise ValueError('ZIP integrity failure')
    Path(str(archive) + '.sha256').write_text(f'{digest(archive)}  {archive.name}\n', encoding='ascii', newline='\n')
    print(json.dumps(dict(package=str(archive), bytes=archive.stat().st_size, sha256=digest(archive), files=len(files) + 1), ensure_ascii=False))


if __name__ == '__main__':
    main()
