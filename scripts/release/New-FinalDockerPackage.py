"""Build a complete Docker deployment kit from a verified existing kit and bundle."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import zipfile

import yaml

REPOSITORY = Path(__file__).resolve().parents[2]
EXPECTED_COMPOSE_SHA256 = "15ae87cb0fb67b1131f6ecf53a6ea43f65dbf58d069b1a3d17919b36610943f1"


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def copy_tree(source, destination):
    shutil.copytree(source, destination, ignore=shutil.ignore_patterns("__pycache__", "*.pyc", ".DS_Store"))


def docker_compose(original, template, app_image, monitor_image):
    result = original
    services = result["services"]
    if "monitor" in services:
        raise ValueError("The reference kit already has a monitor service and needs manual review")
    if services["api"].get("networks") != ["api_proxy"] or services["notifier"].get("networks") != ["portkill"]:
        raise ValueError("Reference network topology differs from the verified deployment")
    if services["api"].get("build") != {"context": ".", "dockerfile": "docker/Dockerfile"}:
        raise ValueError("Reference API build definition differs from the verified deployment")
    for required in ("./data/admin:/var/lib/portkill/admin", "./data/public:/srv/portkill/stable:ro"):
        if required not in services["api"].get("volumes", []):
            raise ValueError("Reference API data mounts differ from the verified deployment")

    services["api"]["environment"].update(template["services"]["api"]["environment"])
    services["api"]["volumes"].extend(template["services"]["api"]["volumes"])
    for name in ("api", "worker", "notifier"):
        services[name]["image"] = app_image
    monitor = template["services"]["monitor"]
    monitor["image"] = monitor_image
    services["monitor"] = monitor
    result.setdefault("volumes", {}).update(template["volumes"])
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--kit", type=Path, required=True, help="Verified PortKill-Docker-Kit source directory")
    parser.add_argument("--bundle", type=Path, required=True, help="Fresh linux-x64 output from Publish-UpdateServer.ps1")
    parser.add_argument("--output", type=Path, required=True, help="New output directory; it must not exist")
    args = parser.parse_args()
    kit = args.kit.resolve(strict=True)
    bundle = args.bundle.resolve(strict=True)
    output = args.output.resolve()
    archive_path = output.with_suffix(".zip")
    if output.exists() or archive_path.exists() or output == kit or output in kit.parents or kit in output.parents:
        raise SystemExit("Choose a new package path outside the reference kit")
    metadata = json.loads((bundle / "bundle.json").read_text(encoding="utf-8-sig"))
    if metadata.get("runtime") != "linux-x64" or metadata.get("selfContained") is not True:
        raise SystemExit("The package requires a self-contained linux-x64 server bundle")
    for item in metadata["files"]:
        path = bundle / item["path"]
        if not path.is_file() or sha(path) != item["sha256"]:
            raise SystemExit(f"Server bundle integrity check failed: {item['path']}")

    protected = (".env", "verification.json", "docker-compose.yml", "docker/Dockerfile", "docker/Caddyfile", "docker/Caddy.Dockerfile")
    before = {name: sha(kit / name) for name in protected}
    if before["docker-compose.yml"] != EXPECTED_COMPOSE_SHA256:
        raise SystemExit("Reference Compose changed; review the topology before packaging")
    original = yaml.safe_load((kit / "docker-compose.yml").read_text(encoding="utf-8-sig"))
    template = yaml.safe_load((REPOSITORY / "deploy/docker/monitor/compose.monitoring.example.yaml").read_text(encoding="utf-8"))

    server_files = sorted(path for name in ("api", "worker", "notifier", "tool") for path in (bundle / name).rglob("*") if path.is_file())
    app_digest = hashlib.sha256("".join(f"{path.relative_to(bundle).as_posix()}:{sha(path)}\n" for path in server_files).encode()).hexdigest()[:12]
    monitor_files = sorted(path for path in (REPOSITORY / "deploy/docker/monitor").rglob("*") if path.is_file() and path.suffix != ".pyc")
    monitor_digest = hashlib.sha256("".join(f"{path.name}:{sha(path)}\n" for path in monitor_files).encode()).hexdigest()[:12]
    app_image = f"portkill-update-server:monitoring-{app_digest}"
    monitor_image = f"portkill-monitor:{monitor_digest}"
    compose = docker_compose(original, template, app_image, monitor_image)

    output.mkdir(parents=True)
    copy_tree(bundle, output / "server-bundle")
    (output / "docker").mkdir()
    for name in ("Dockerfile", "Caddyfile", "Caddy.Dockerfile"):
        shutil.copy2(kit / "docker" / name, output / "docker" / name)
    copy_tree(REPOSITORY / "deploy/docker/monitor", output / "docker/monitor")
    (output / "docker/monitor/README.md").write_text(
        "# 完整部署包内的 monitor\n\n"
        "主 docker-compose.yml 已合入全部监控配置，不要再次合并 compose.monitoring.example.yaml。\n"
        "安装、升级、验收和回退请按包根目录的《使用说明.md》操作。\n"
        "API、Worker、Notifier 统一使用本包新的应用镜像标签；Caddy 配置沿用原包。\n"
        "monitor 只向快照卷写入数据，不开放公网端口；Docker socket 的 ro 挂载不等于 API 只读授权。\n",
        encoding="utf-8", newline="\n")
    for entry in (REPOSITORY / "deploy/docker/package").iterdir():
        if entry.is_file():
            shutil.copy2(entry, output / entry.name)
            if entry.suffix == ".sh":
                (output / entry.name).write_text(entry.read_text(encoding="utf-8-sig"), encoding="utf-8", newline="\n")
    shutil.copyfile(kit / ".env.example", output / ".env.example")
    shutil.copyfile(kit / "verification.json.example", output / "verification.json.example")
    example_env = (output / ".env.example").read_text(encoding="utf-8-sig").splitlines()
    example_env = ["ADMIN_DOMAIN=REPLACE_WITH_ADMIN_DOMAIN" if line.startswith("ADMIN_DOMAIN=")
                   else "UPDATES_DOMAIN=REPLACE_WITH_UPDATES_DOMAIN" if line.startswith("UPDATES_DOMAIN=")
                   else line for line in example_env]
    (output / ".env.example").write_text(
        "\n".join(example_env).rstrip()
        + "\nCOMPOSE_PROJECT_NAME=portkill\nMONITOR_COMPOSE_PROJECT=portkill\nMONITOR_INTERFACE=\n",
        encoding="utf-8", newline="\n")
    (output / "verification.json.example").write_text(
        (output / "verification.json.example").read_text(encoding="utf-8-sig").replace(
            "https://xiaoxiongupdate.duckdns.org/stable/manifest.json",
            "https://updates.example.com/stable/manifest.json"),
        encoding="utf-8", newline="\n")
    (output / "docker-compose.yml").write_text(yaml.safe_dump(compose, allow_unicode=True, sort_keys=False), encoding="utf-8", newline="\n")
    (output / ".dockerignore").write_text(
        "**\n!docker/\n!docker/Dockerfile\n!docker/Caddy.Dockerfile\n!server-bundle/\n!server-bundle/**\n",
        encoding="utf-8", newline="\n")

    commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=REPOSITORY, text=True).strip()
    dirty = bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=REPOSITORY, text=True).strip())
    kit_metadata = {
        "schema": 1,
        "application": "PortKill Update Administration",
        "runtime": "linux-x64",
        "sourceCommit": commit,
        "sourceDirty": dirty,
        "referenceComposeSha256": EXPECTED_COMPOSE_SHA256,
        "applicationImage": app_image,
        "monitorImage": monitor_image,
        "privateKeyIncluded": False,
        "environmentIncluded": False,
        "verificationConfigIncluded": False,
        "dockerDaemonAvailableForBuild": False,
    }
    (output / "kit.json").write_text(json.dumps(kit_metadata, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")

    files = sorted(path for path in output.rglob("*") if path.is_file())
    for path in files:
        relative = path.relative_to(output)
        if path.name in (".env", "verification.json") or "data" in relative.parts or path.suffix.lower() in (".key", ".pem", ".pfx", ".p12"):
            raise ValueError(f"Private deployment material cannot be packaged: {relative}")
    (output / "SHA256SUMS.txt").write_text(
        "".join(f"{sha(path)}  {path.relative_to(output).as_posix()}\n" for path in files),
        encoding="utf-8", newline="\n")
    with zipfile.ZipFile(archive_path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6, allowZip64=True) as archive:
        for path in sorted(output.rglob("*")):
            if path.is_file():
                archive.write(path, path.relative_to(output).as_posix())
    if before != {name: sha(kit / name) for name in protected}:
        raise RuntimeError("The reference deployment kit changed while packaging")
    print(json.dumps({"output": str(output), "archive": str(archive_path), "applicationImage": app_image,
                      "monitorImage": monitor_image, "referenceKitUnchanged": True}, ensure_ascii=False))


if __name__ == "__main__":
    main()
