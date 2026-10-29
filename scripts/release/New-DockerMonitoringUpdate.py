"""Prepare a monitoring overlay from the existing kit without modifying it (requires PyYAML)."""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import zipfile

import yaml

REPOSITORY = Path(__file__).resolve().parents[2]


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def compose_monitoring(original, template, image):
    result = copy.deepcopy(original)
    if "monitor" in result["services"]:
        raise ValueError("Existing monitor must be reviewed before generating this overlay")
    api = result["services"]["api"]
    if api.get("networks") != ["api_proxy"] or result["services"]["notifier"].get("networks") != ["portkill"]:
        raise ValueError("Kit network topology differs from the verified deployment")
    if not isinstance(api.get("environment"), dict) or not isinstance(api.get("volumes"), list):
        raise ValueError("Kit API environment/volumes differs from the verified deployment")
    if api["build"] != dict(context=".", dockerfile="docker/Dockerfile"):
        raise ValueError("Kit API build differs from the verified deployment")
    for expected in ("./data/admin:/var/lib/portkill/admin", "./data/public:/srv/portkill/stable:ro"):
        if expected not in api["volumes"]:
            raise ValueError("Kit data mounts differ from the verified deployment")
    api["environment"].update(template["services"]["api"]["environment"])
    api["volumes"] += template["services"]["api"]["volumes"]
    api["image"] = image
    result["services"]["monitor"] = copy.deepcopy(template["services"]["monitor"])
    result.setdefault("volumes", {}).update(template["volumes"])
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--kit", type=Path, required=True)
    parser.add_argument("--bundle", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    kit, bundle = args.kit.resolve(strict=True), args.bundle.resolve(strict=True)
    output = args.output.resolve()
    if output.exists() or output == kit or kit in output.parents or output.with_suffix(".zip").exists():
        raise SystemExit("Choose a new output directory outside the original kit")
    metadata = json.loads((bundle / "bundle.json").read_text(encoding="utf-8-sig"))
    if metadata.get("runtime") != "linux-x64":
        raise SystemExit("This kit requires the verified linux-x64 bundle")
    protected = [".env", "verification.json", "docker-compose.yml", "docker/Dockerfile", "docker/Caddyfile"]
    before = {name: sha(kit / name) for name in protected}
    original = yaml.safe_load((kit / "docker-compose.yml").read_text(encoding="utf-8-sig"))
    template = yaml.safe_load((REPOSITORY / "deploy/docker/monitor/compose.monitoring.example.yaml").read_text(encoding="utf-8"))
    api_files = sorted(path for path in (bundle / "api").rglob("*") if path.is_file())
    digest = hashlib.sha256("".join(f"{path.relative_to(bundle / 'api').as_posix()}:{sha(path)}\n" for path in api_files).encode()).hexdigest()[:12]
    compose = compose_monitoring(original, template, f"portkill-update-server:monitoring-{digest}")
    output.mkdir(parents=True)
    for component in ("api", "web"):
        shutil.copytree(bundle / component, output / "server-bundle" / component)
    shutil.copytree(REPOSITORY / "deploy/docker/monitor", output / "docker/monitor", ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))
    (output / "docker-compose.yml").write_text(yaml.safe_dump(compose, allow_unicode=True, sort_keys=False), encoding="utf-8", newline="\n")
    shutil.copyfile(kit / "docker-compose.yml", output / "docker-compose.before-monitoring.yml")
    update = dict(schema=1, scope="api-web-monitor", originalComposeSha256=before["docker-compose.yml"],
                  originalApiImage=original["services"]["api"]["image"], image=compose["services"]["api"]["image"],
                  sourceCommit=subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=REPOSITORY, text=True).strip(),
                  sourceDirty=bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=REPOSITORY)))
    (output / "monitoring-update.json").write_text(json.dumps(update, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    (output / "部署说明.md").write_text(
        "# 运行总览增量包\n\n适配 PortKill-Docker-Kit-20261026；保留原包，先备份再覆盖。\n\n"
        "只覆盖主Compose、server-bundle/api、server-bundle/web、docker/monitor。Worker、Notifier、Tool、Caddy配置及数据沿用原部署。\n\n"
        "1. 核对当前主Compose与 monitoring-update.json 的 originalComposeSha256 相符；若服务器配置后来改过，先人工合并，不直接覆盖。\n"
        "2. 在服务器本地备份主Compose和API/Web目录，记录当前API镜像ID。保留Web根目录，按文件覆盖，勿重命名整个挂载目录。\n"
        "3. 在原.env增加MONITOR_COMPOSE_PROJECT=已有项目名，可选MONITOR_INTERFACE=实际主网卡。现有COMPOSE_PROJECT_NAME优先。\n"
        "4. sha256sum -c SHA256SUMS.txt 验证包文件；复制后执行 docker compose config --quiet。\n"
        "5. docker compose build api monitor，然后 docker compose up -d --no-deps api monitor。后续启动仍用 docker compose up -d。\n"
        "6. 登录运行总览，按docker/monitor/README.md核对宿主机资源、业务状态与重启行为。\n\n"
        "失败时停止monitor，恢复服务器本地备份的Compose、API/Web及原镜像引用，再执行原项目up命令。禁止down -v。\n\n"
        "本包没有.env、verification.json或业务数据；不包含服务器凭据。当前机器若无Docker，生成包不代表Linux实机验收完成。\n",
        encoding="utf-8", newline="\n")
    payload = sorted(path for path in output.rglob("*") if path.is_file())
    for path in payload:
        relative = path.relative_to(output)
        if path.name in (".env", "verification.json") or "data" in relative.parts or path.suffix.lower() in (".key", ".pem", ".pfx", ".p12"):
            raise ValueError(f"Private deployment file cannot be packaged: {relative}")
    (output / "SHA256SUMS.txt").write_text("".join(f"{sha(path)}  {path.relative_to(output).as_posix()}\n" for path in payload), encoding="utf-8", newline="\n")
    with zipfile.ZipFile(output.with_suffix(".zip"), "w", zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(output.rglob("*")):
            if path.is_file():
                archive.write(path, path.relative_to(output).as_posix())
    if before != {name: sha(kit / name) for name in protected}:
        raise RuntimeError("Original kit configuration unexpectedly changed")
    print(json.dumps(dict(output=str(output), archive=str(output.with_suffix(".zip")), originalKitUnchanged=True), ensure_ascii=False))


if __name__ == "__main__":
    main()
