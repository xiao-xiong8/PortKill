"""Package the built administration web app for an existing Caddy Docker kit."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import zipfile

REPOSITORY = Path(__file__).resolve().parents[2]
WEB = REPOSITORY / "src/Web/PortKiller.UpdateAdmin.Web"


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path, help="New output directory")
    args = parser.parse_args()
    output = args.output.resolve()
    archive_path = Path(str(output) + ".zip")
    if output.exists() or archive_path.exists():
        raise SystemExit("Output already exists; choose a new path")
    dist = WEB / "dist"
    html = (dist / "index.html").read_text(encoding="utf-8")
    references = re.findall(r'(?:src|href)="(/assets/[^"?]+)', html)
    if not references or any(not (dist / name.lstrip("/")).is_file() for name in references):
        raise SystemExit("Build the web app first; its index/assets are incomplete")
    files = sorted(path for path in dist.rglob("*") if path.is_file())
    if any(path.is_symlink() or not path.relative_to(dist).as_posix().isascii()
           or path.suffix.lower() in (".map", ".pem", ".key", ".pfx", ".p12")
           or path.name in (".env", "verification.json") for path in files):
        raise SystemExit("Unexpected or private file in dist")
    source_files = [WEB / "index.html", WEB / "package.json", WEB / "pnpm-lock.yaml", WEB / "vite.config.ts"]
    source_files += sorted(path for path in (WEB / "src").rglob("*") if path.is_file())
    source_files += sorted(path for path in (WEB / "public").rglob("*") if path.is_file())
    # Vite preserves public-file timestamps when copying icons into dist.
    generated = [dist / "index.html"] + [dist / name.lstrip("/") for name in references]
    oldest_output = min(path.stat().st_mtime for path in generated)
    if any(path.stat().st_mtime > oldest_output for path in source_files):
        raise SystemExit("Sources are newer than dist; run pnpm build before packaging")
    output.mkdir(parents=True)
    shutil.copytree(dist, output / "server-bundle/web")
    (output / "docker-web-update.sh").write_text(
        (REPOSITORY / "deploy/docker/docker-web-update.sh").read_text(encoding="utf-8-sig"),
        encoding="utf-8", newline="\n")
    record = {
        "schema": 1,
        "component": "admin-web",
        "scope": "Caddy bind mount: server-bundle/web -> /srv/web",
        "generatedUtc": datetime.now(timezone.utc).isoformat(),
        "sourceCommit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=REPOSITORY, text=True).strip(),
        "sourceDirty": bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=REPOSITORY, text=True).strip()),
        "sourceFingerprint": hashlib.sha256("".join(
            f"{path.relative_to(WEB).as_posix()}:{sha(path)}\n" for path in source_files).encode()).hexdigest(),
        "webFiles": [{"path": path.relative_to(dist).as_posix(), "bytes": path.stat().st_size, "sha256": sha(path)} for path in files],
        "requiresImageBuild": False,
        "requiresContainerRestart": False,
        "requiresDatabaseMigration": False,
        "supportsInPlaceOverwrite": True,
        "includesPrivateConfiguration": False,
        "requiresExistingBackend": "Existing PortKill monitoring deployment with the unchanged authentication API",
    }
    (output / "web-update.json").write_text(json.dumps(record, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    (output / "README.md").write_text("""# PortKill Docker 登录页替换包

本包包含正式 Vue 前端：鹈鹕骑行登录页、主题与天气切换、账号密码、验证器绑定、动态码及恢复码登录，同时保留运行总览等管理功能。适用于已经升级监控功能的现有 Docker 部署，不是全新安装包。

只更新 Caddy 挂载的 `server-bundle/web`，不构建镜像、不重启容器、不修改 Compose、Caddyfile、.env、数据库、账号、证书和发布数据。保留 Web 根目录及旧哈希资源，包内文件名全部为 ASCII。

## 使用

将 ZIP 上传到现有部署目录，直接在当前目录覆盖并校验，无需创建 `portkill-login-update` 或另一个 `server-bundle`：

```bash
cd /home/ubuntu/PortKill-Docker-Kit-20261026
unzip -o ARCHIVE_NAME
sudo bash ./docker-web-update.sh . portkill-docker-kit-20261026
```

脚本支持包目录与部署目录相同：校验包内 SHA256、指定项目的实际 Caddy 挂载，以及容器内全部 Web 文件与宿主机内容是否一致。直接覆盖已由 unzip 完成，脚本不会将文件复制到自身，不会额外创建临时目录或备份目录。不会操作另一套 `ubuntu` Compose 项目。Ubuntu 需要已有 Docker Compose 以及默认 coreutils、tar、flock，解压需要 unzip。

直接解压会先后覆盖文件，并不保证解压过程中首页与资源同时切换；解压完成后再刷新验收。直接覆盖不能补建覆盖前备份；如果已经覆盖，回退需使用已有的旧版本 Web 备份。若尚未覆盖并希望备份，可在解压前于当前目录运行：

```bash
tar -czf "web-before-login-$(date +%Y%m%d-%H%M%S).tar.gz" -C server-bundle/web .
```

也保留原有的独立目录升级方式：将包解压到部署目录以外，执行 `sudo bash /包目录/docker-web-update.sh /部署目录 Compose项目名`。该方式会自动备份旧 Web，再复制资源、最后原子替换首页；备份写入部署目录旁的 `PortKill-web-backup-XXXXXXXX`，并打印完整回退命令。

## 验收

1. 原管理网址按 Ctrl+F5，退出当前会话后检查新版左右分栏登录页。
2. 检查深浅/跟随系统主题、天气、动画暂停，以及手机布局。
3. 用已有账号完成密码及动态码/恢复码登录；已有账号无需重新绑定验证器。
4. 检查导航、运行总览曲线和原有发布管理功能。

新包已内置资源哈希，后续常规访问会使用新文件。已打开的旧页面仍可能需要刷新。

## 回退

若使用独立目录升级所生成的自动备份，复制脚本最后打印的命令，或使用实际备份目录：

```bash
sudo bash ./docker-web-update.sh --rollback /home/ubuntu/PortKill-Docker-Kit-20261026 portkill-docker-kit-20261026 /home/ubuntu/PortKill-web-backup-实际后缀
```

自动备份回退也会先备份当前前端，再按“资源先行、首页最后”的顺序恢复旧版，不还原数据库。直接覆盖时若曾运行上面的 tar 备份命令，可在当前部署目录用 `tar -xzf 备份文件名 -C server-bundle/web` 恢复，然后强制刷新；这会保留新增哈希资源，并覆盖旧首页。不要删除正在使用的 `server-bundle/web` 目录；无需执行原来的 docker-upgrade.sh 或 docker-continue-after-overwrite.sh。

## 完整性与范围

`SHA256SUMS.txt` 校验包内全部文件；ZIP 旁的 `.sha256` 校验归档本身。`web-update.json` 记录当前源码指纹、基础提交、未提交状态和生产文件清单；本包包含打包时的工作区改动。

本机没有 Docker 引擎，交付检查覆盖生产构建、归档与校验和及升级/回退脚本的隔离测试。服务器的真实挂载、实际账号登录及最终显示需在执行后按上述验收步骤确认。
""".replace("ARCHIVE_NAME", archive_path.name), encoding="utf-8", newline="\n")
    payload = sorted(path for path in output.rglob("*") if path.is_file())
    (output / "SHA256SUMS.txt").write_text("".join(
        f"{sha(path)}  {path.relative_to(output).as_posix()}\n" for path in payload), encoding="ascii", newline="\n")
    with zipfile.ZipFile(archive_path, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for path in sorted(output.rglob("*")):
            if path.is_file():
                entry = zipfile.ZipInfo(path.relative_to(output).as_posix())
                entry.create_system = 3
                entry.external_attr = (0o100755 if path.suffix == ".sh" else 0o100644) << 16
                entry.compress_type = zipfile.ZIP_DEFLATED
                archive.writestr(entry, path.read_bytes())
    with zipfile.ZipFile(archive_path) as archive:
        if archive.testzip() is not None:
            raise RuntimeError("Archive CRC check failed")
        for entry in archive.namelist():
            if archive.read(entry) != (output / entry).read_bytes():
                raise RuntimeError(f"Archive entry differs: {entry}")
    digest = sha(archive_path)
    Path(str(archive_path) + ".sha256").write_text(f"{digest}  {archive_path.name}\n", encoding="ascii", newline="\n")
    print(json.dumps({"archive": str(archive_path), "bytes": archive_path.stat().st_size,
                      "sha256": digest, "webFiles": len(files), "sourceDirty": record["sourceDirty"]}, ensure_ascii=False))


if __name__ == "__main__":
    main()
