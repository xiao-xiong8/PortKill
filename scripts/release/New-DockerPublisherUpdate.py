"""Package the publisher compatibility fix for an existing Docker kit."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import uuid
import zipfile
from datetime import datetime, timezone

REPOSITORY = Path(__file__).resolve().parents[2]
BINARIES = {
    "worker": ["PortKiller.UpdatePublisher.Worker.dll", "PortKiller.UpdateManagement.dll", "PortKiller.UpdateProtocol.dll"],
    "tool": ["PortKiller.UpdateTool.dll", "PortKiller.UpdateProtocol.dll"],
}
SOURCES = [
    "src/Modules/PortKiller.UpdateProtocol/PublisherProcessIdentity.cs",
    "src/Modules/PortKiller.UpdateProtocol/PortKiller.UpdateProtocol.csproj",
    "src/Modules/PortKiller.UpdateManagement/Security/UpdateToolClient.cs",
    "src/Tools/PortKiller.UpdateTool/ToolProcessGuard.cs",
    "src/Services/PortKiller.UpdatePublisher.Worker/Program.cs",
]


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--kit", type=Path, required=True)
    parser.add_argument("--publish", type=Path, default=REPOSITORY / ".local/artifacts/publisher-owner-fix")
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--apply-to-kit", action="store_true")
    args = parser.parse_args()
    kit = args.kit.resolve(strict=True)
    archive = args.archive.resolve()
    if archive.exists():
        raise SystemExit("Archive already exists; choose a new output path.")
    protected = [".env", "verification.json", "docker-compose.yml", "docker/Dockerfile", "docker/Caddyfile"]
    before = {name: sha(kit / name) for name in protected}
    # This small patch is valid only when all other publish/runtime files already match.
    for component, changed in BINARIES.items():
        source = (args.publish / component).resolve(strict=True)
        for file in source.iterdir():
            if file.is_file() and file.name not in changed:
                target = kit / "server-bundle" / component / file.name
                if not target.is_file() or sha(file) != sha(target):
                    raise SystemExit(f"Additional runtime/dependency changes require a full component package: {component}/{file.name}")
    stage = REPOSITORY / ".local/artifacts/publisher-owner-fix" / ("patch-" + uuid.uuid4().hex)
    stage.mkdir(parents=True)
    for component, names in BINARIES.items():
        for name in names:
            target = stage / "server-bundle" / component / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(args.publish / component / name, target)
    for name in ["update-publisher.sh", "resolve-notifier-image.py", "PUBLISHER-FIX.txt"]:
        target = stage / "docker" / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text((REPOSITORY / "deploy/docker" / name).read_text(encoding="utf-8"), encoding="utf-8", newline="\n")
    for name in SOURCES:
        target = stage / "source" / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(REPOSITORY / name, target)
    record = {
        "component": "publisher", "fix": "linux-kernel-owner-start-identity", "revision": 2, "runtime": "linux-x64",
        "generatedUtc": datetime.now(timezone.utc).isoformat(),
        "sourceCommit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=REPOSITORY, text=True).strip(),
        "sourceDirty": bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=REPOSITORY, text=True)),
        "files": [{"path": str(p.relative_to(stage)).replace("\\", "/"), "sha256":sha(p)} for p in sorted(stage.rglob("*.dll"))],
    }
    write_json(stage / "docker/publisher-update.json", record)
    bundle = json.loads((kit / "server-bundle/bundle.json").read_text(encoding="utf-8-sig"))
    for item in bundle["files"]:
        replacement = stage / "server-bundle" / item["path"]
        if replacement.is_file():
            item.update(bytes=replacement.stat().st_size, sha256=sha(replacement))
    bundle["componentUpdates"] = [r for r in bundle.get("componentUpdates", []) if r.get("component") != "publisher"] + [record]
    write_json(stage / "server-bundle/bundle.json", bundle)
    # Keep the existing notifier updater valid after its shared resolver/metadata changes.
    checksum = kit / "docker/notifier-update.sha256"
    if checksum.exists():
        lines = []
        for line in checksum.read_text(encoding="ascii").splitlines():
            digest, name = line.split("  ", 1)
            replacement = stage / name
            lines.append(f"{sha(replacement) if replacement.is_file() else digest}  {name}")
        (stage / "docker/notifier-update.sha256").write_text("\n".join(lines) + "\n", encoding="ascii", newline="\n")
    payload = sorted(p for p in stage.rglob("*") if p.is_file())
    (stage / "docker/publisher-update.sha256").write_text("".join(f"{sha(p)}  {p.relative_to(stage).as_posix()}\n" for p in payload), encoding="ascii", newline="\n")
    archive.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as output:
        for file in sorted(stage.rglob("*")):
            if file.is_file():
                entry = zipfile.ZipInfo(file.relative_to(stage).as_posix())
                entry.create_system = 3
                entry.external_attr = 0o100644 << 16
                entry.compress_type = zipfile.ZIP_DEFLATED
                output.writestr(entry, file.read_bytes())
    with zipfile.ZipFile(archive) as check:
        assert check.testzip() is None
        for name in check.namelist():
            assert name.startswith(("server-bundle/", "docker/", "source/")) and ".." not in Path(name).parts
            assert check.read(name) == (stage / name).read_bytes()
    if args.apply_to_kit:
        for file in stage.rglob("*"):
            if file.is_file():
                target = kit / file.relative_to(stage)
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(file, target)
    assert before == {name: sha(kit / name) for name in protected}
    digest = sha(archive)
    Path(str(archive) + ".sha256").write_text(f"{digest}  {archive.name}\n", encoding="ascii", newline="\n")
    print(json.dumps({"archive":str(archive), "bytes":archive.stat().st_size, "sha256":digest, "appliedToKit":args.apply_to_kit, "protectedConfigurationUnchanged":True}, ensure_ascii=False))


if __name__ == "__main__":
    main()
