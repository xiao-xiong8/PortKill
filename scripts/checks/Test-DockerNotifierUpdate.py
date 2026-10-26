"""Local tests for the notifier updater; Docker calls use an isolated synthetic daemon."""
import hashlib
import http.server
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import threading
import unittest


REPOSITORY = Path(__file__).resolve().parents[2]
BASH = shutil.which("bash")
if os.name == "nt":
    BASH = str(Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "Git/bin/bash.exe")

MOCK_DOCKER = r'''
import json, os, pathlib, sys
root = pathlib.Path(os.environ["PORTKILL_TEST_ROOT"])
path = root / "mock-state.json"
state = json.loads(path.read_text())
args = sys.argv[1:]
with (root / "mock-calls.jsonl").open("a") as log:
    log.write(json.dumps(args) + "\n")
def save(): path.write_text(json.dumps(state))
def resolve(reference):
    tagged = {"portkill-update-server:existing": state["image"], "portkill-notifier:previous": state.get("previous"), "portkill-notifier:update-base": state.get("pending"), "portkill-notifier:latest": state.get("snapshot")}
    image = tagged.get(reference, reference)
    return image if image in state["available"] else None
def missing():
    print("No such image", file=sys.stderr); sys.exit(1)
if args == ["--probe"]:
    json.load(sys.stdin)
    if state.get("fail_probe") and state["running"] == "sha256:new": sys.exit(1)
    print("SIMULATED SSE probe passed"); sys.exit(0)
if args[:2] == ["compose", "version"]: print("Docker Compose version v2.synthetic")
elif args[:2] == ["compose", "config"]:
    if "--format" in args:
        print(json.dumps({"services":{"notifier":{"image":"portkill-update-server:existing", "build":{"context":str(root),"dockerfile":"docker/Dockerfile"}}}}))
elif args[:2] == ["compose", "ps"]:
    if "-q" in args: print("notifier-current")
elif args[:4] == ["compose", "--progress", "plain", "build"]:
    if args[4:] != ["notifier"]: raise RuntimeError("Unexpected build target")
    if state.get("fail_build"): sys.exit(1)
    old = state["image"]
    state["image"] = state.get("build_result", "sha256:new")
    if state["image"] not in state["available"]: state["available"].append(state["image"])
    if state.get("drop_unpinned_on_build") and old != state["image"] and old not in (state.get("previous"), state.get("pending")):
        state["available"].remove(old)
    save()
elif args[:2] == ["compose", "up"]:
    if args[2:] != ["-d", "--no-deps", "--force-recreate", "notifier"]: raise RuntimeError("Unexpected recreate scope")
    state["ups"] = state.get("ups", 0) + 1; save()
    if state.get("fail_up_once") and state["ups"] == 1: sys.exit(1)
    state["running"] = state["image"]; save()
elif args[:2] == ["image", "tag"]:
    source, target = args[2:]
    image = resolve(source)
    if not image: missing()
    if state.get("fail_pin") and target == "portkill-notifier:update-base": sys.exit(1)
    slot = {"portkill-notifier:previous":"previous", "portkill-notifier:update-base":"pending", "portkill-update-server:existing":"image"}[target]
    state[slot] = image; save()
elif args[:2] == ["image", "inspect"]:
    image = resolve(args[-1])
    if image is None: missing()
    identity = state["images"][image]
    requested_platform = "--platform" in args
    if requested_platform and state.get("platform_unsupported"):
        print("unknown flag: --platform", file=sys.stderr); sys.exit(1)
    image_id = identity["manifest"] if requested_platform else image
    if state.get("inspect_id_is_config"):
        image_id = identity["containerImage"]
    if "--format" in args: print(image_id)
    else: print(json.dumps([{"Id":image_id,"Descriptor":{"digest":identity["manifest"] if requested_platform else image},"Config":{"User":"10001:10001","WorkingDir":"/opt/portkill","Labels":{"io.portkill.recovery.container":identity.get("recovery_container")}}}]))
elif args[:2] == ["image", "ls"]:
    print("\n".join(state["available"]))
elif args[:2] == ["image", "rm"]:
    if args[2:] != ["portkill-notifier:update-base"]: raise RuntimeError("Unexpected image deletion")
    state.pop("pending", None); save()
elif args[:1] == ["run"]:
    if args[1:7] != ["--rm", "--network", "none", "--entrypoint", "/bin/sh", "portkill-notifier:latest"]: raise RuntimeError("Unexpected snapshot check")
    if state.get("fail_snapshot_check"): sys.exit(1)
    print("SIMULATED snapshot self-check passed")
elif args[:1] == ["inspect"]:
    if "--format" in args:
        print("true" if "Running" in args[args.index("--format")+1] else state["running"])
    else:
        identity = state["images"][state["running"]]
        print(json.dumps([{"Id":"notifier-current","Image":identity["containerImage"],"ImageManifestDescriptor":{"digest":identity["manifest"],"platform":{"os":"linux","architecture":"amd64"}},"State":{"Running":True},"NetworkSettings":{"Networks":{"synthetic":{"IPAddress":"127.0.0.1"}}},"Config":{"Image":"portkill-update-server:existing","Cmd":["http://0.0.0.0:8091"]}}]))
else: raise RuntimeError("Unexpected Docker arguments: " + repr(args))
'''


class UpdaterTests(unittest.TestCase):
    def setUp(self):
        temporary_parent = REPOSITORY / ".local/tmp"
        temporary_parent.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(prefix="docker-update-test-", dir=temporary_parent)
        self.root = Path(self.temporary.name).resolve()
        self.assertTrue(self.root.is_relative_to(temporary_parent.resolve()))
        self.addCleanup(self.temporary.cleanup)
        (self.root / "docker").mkdir()
        (self.root / "mock-bin").mkdir()
        (self.root / "data/admin").mkdir(parents=True)
        (self.root / "server-bundle/notifier").mkdir(parents=True)
        for name in ("update.sh", "check-notifier.py", "resolve-notifier-image.py"):
            (self.root / "docker" / name).write_text((REPOSITORY / "deploy/docker" / name).read_text(encoding="utf-8"), encoding="utf-8", newline="\n")
        self.protected = {".env": "SYNTHETIC_ENV=unchanged\n", "verification.json": "{}", "docker-compose.yml": "# synthetic fixture\n", "docker/Dockerfile": "# synthetic fixture\n", "data/admin/marker": "synthetic persistent data"}
        for name, content in self.protected.items():
            (self.root / name).write_text(content, encoding="utf-8")
        payload = self.root / "server-bundle/notifier/fixture.txt"
        payload.write_text("isolated synthetic build input", encoding="utf-8")
        hashes = [f"{hashlib.sha256(file.read_bytes()).hexdigest()}  {file.relative_to(self.root).as_posix()}" for file in [payload, self.root / "docker/update.sh", self.root / "docker/check-notifier.py", self.root / "docker/resolve-notifier-image.py"]]
        (self.root / "docker/notifier-update.sha256").write_text("\n".join(hashes) + "\n", encoding="ascii", newline="\n")
        (self.root / "mock-docker.py").write_text(MOCK_DOCKER, encoding="utf-8")
        wrappers = {
            "docker": '#!/usr/bin/env bash\nexec "$PORTKILL_TEST_PYTHON" "$PORTKILL_TEST_DOCKER" "$@"\n',
            "python3": '#!/usr/bin/env bash\nif [[ "${1:-}" == docker/check-notifier.py ]]; then exec "$PORTKILL_TEST_PYTHON" "$PORTKILL_TEST_DOCKER" --probe; fi\nexec "$PORTKILL_TEST_PYTHON" "$@"\n',
        }
        if os.name == "nt":
            wrappers["flock"] = '#!/usr/bin/env bash\nexit 0\n'
            (self.root / "mock-bin/docker.cmd").write_text('@"%PORTKILL_TEST_PYTHON%" "%PORTKILL_TEST_DOCKER%" %*\n', encoding="ascii")
        for name, text in wrappers.items():
            file = self.root / "mock-bin" / name
            file.write_text(text, encoding="utf-8", newline="\n")
            file.chmod(0o755)
        images = {f"sha256:{name}": {"containerImage": f"sha256:{name}", "manifest": f"sha256:manifest-{name}"} for name in ("old", "new", "previous")}
        self.state = {"image": "sha256:old", "running": "sha256:old", "images": images, "available": list(images)}

    def run_update(self, *args):
        (self.root / "mock-state.json").write_text(json.dumps(self.state), encoding="utf-8")
        environment = dict(os.environ, PYTHONUTF8="1", PORTKILL_TEST_ROOT=str(self.root), PORTKILL_TEST_PYTHON=Path(sys.executable).as_posix(), PORTKILL_TEST_DOCKER=(self.root / "mock-docker.py").as_posix())
        # Windows drive prefixes must be converted before joining a POSIX PATH.
        environment["PORTKILL_TEST_BIN"] = (self.root / "mock-bin").as_posix()
        environment["PORTKILL_TEST_SCRIPT"] = (self.root / "docker/update.sh").as_posix()
        prefix = 'export PATH="$(cygpath -u "$PORTKILL_TEST_BIN"):$PATH";' if os.name == "nt" else 'export PATH="$PORTKILL_TEST_BIN:$PATH";'
        command = [BASH, "--noprofile", "--norc", "-c", prefix + ' exec bash "$PORTKILL_TEST_SCRIPT" "$@"', "test", *args]
        result = subprocess.run(command, cwd=self.root, env=environment, capture_output=True, text=True, encoding="utf-8", timeout=30)
        self.state = json.loads((self.root / "mock-state.json").read_text())
        for name, content in self.protected.items():
            self.assertEqual((self.root / name).read_text(encoding="utf-8"), content)
        calls_path = self.root / "mock-calls.jsonl"
        self.assertTrue(calls_path.exists(), result.stdout + result.stderr)
        self.calls = [json.loads(line) for line in calls_path.read_text().splitlines()]
        return result

    def test_success_rebuilds_only_notifier_and_keeps_one_previous_image(self):
        result = self.run_update()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(self.state["running"], "sha256:new")
        self.assertEqual(self.state["previous"], "sha256:old")
        self.assertEqual(self.state["ups"], 1)
        self.assertNotIn("pending", self.state)
        pin = next(i for i, call in enumerate(self.calls) if call[:2] == ["image", "tag"] and call[-1] == "portkill-notifier:update-base")
        build = self.calls.index(["compose", "--progress", "plain", "build", "notifier"])
        self.assertLess(pin, build)

    def test_build_cannot_remove_the_pinned_running_image(self):
        self.state["drop_unpinned_on_build"] = True
        result = self.run_update()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(self.state["previous"], "sha256:old")
        self.assertIn("sha256:old", self.state["available"])

    def test_containerd_config_id_matches_platform_under_image_index(self):
        self.state["images"]["sha256:old"]["containerImage"] = "sha256:config-old"
        self.state["image"] = "sha256:new"  # Tag already moved by the earlier failed update.
        result = self.run_update()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(self.state["previous"], "sha256:old")
        self.assertEqual(self.state["running"], "sha256:new")
        self.assertFalse(any(call[:3] == ["image", "tag", "sha256:config-old"] for call in self.calls))

    def test_containerd_descriptor_matches_when_inspect_id_is_config(self):
        self.state["images"]["sha256:old"]["containerImage"] = "sha256:config-old"
        self.state.update(image="sha256:new", inspect_id_is_config=True)
        result = self.run_update()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(self.state["previous"], "sha256:old")
        self.assertFalse(any(call[:3] == ["image", "tag", "sha256:config-old"] for call in self.calls))

    def test_legacy_daemon_needs_no_platform_flag(self):
        self.state["platform_unsupported"] = True
        result = self.run_update()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertFalse(any("--platform" in call for call in self.calls))

    def test_missing_actual_running_image_stops_before_build(self):
        self.state["available"].remove("sha256:old")
        self.state["image"] = "sha256:new"
        result = self.run_update()
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.state["running"], "sha256:old")
        self.assertFalse(any("build" in call or "up" in call for call in self.calls))
        self.assertNotIn("previous", self.state)
        self.assertEqual(result.stderr.count("更新未完成，尚未切换通知容器。"), 1, result.stderr)

    def test_containerd_descriptor_rollback_uses_resolvable_tag(self):
        self.state["images"]["sha256:old"]["containerImage"] = "sha256:config-old"
        self.state.update(inspect_id_is_config=True, fail_probe=True)
        result = self.run_update()
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.state["running"], "sha256:old")
        self.assertEqual(self.state["ups"], 2)

    def test_backup_failure_stops_before_build(self):
        self.state["fail_pin"] = True
        result = self.run_update()
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(any("build" in call or "up" in call for call in self.calls))

    def add_snapshot(self, owner="notifier-current"):
        self.state["available"].remove("sha256:old")
        self.state.update(image="sha256:new", snapshot="sha256:snapshot")
        self.state["available"].append("sha256:snapshot")
        self.state["images"]["sha256:snapshot"] = {"containerImage":"sha256:snapshot", "manifest":"sha256:manifest-snapshot", "recovery_container":owner}

    def test_missing_manifest_recovers_using_verified_container_export(self):
        self.add_snapshot()
        result = self.run_update()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(self.state["previous"], "sha256:snapshot")
        self.assertEqual(self.state["running"], "sha256:new")
        self.assertIn("旧容器恢复镜像自检通过", result.stderr)

    def test_snapshot_for_another_container_is_not_used(self):
        self.add_snapshot(owner="unrelated-container")
        result = self.run_update()
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(any("build" in call or "up" in call for call in self.calls))

    def test_failed_snapshot_check_prevents_cutover(self):
        self.add_snapshot()
        self.state["fail_snapshot_check"] = True
        result = self.run_update()
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.state["running"], "sha256:old")
        self.assertFalse(any("build" in call or "up" in call for call in self.calls))

    def test_export_snapshot_can_restore_after_failed_new_probe(self):
        self.add_snapshot()
        self.state["fail_probe"] = True
        result = self.run_update()
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.state["running"], "sha256:snapshot")
        self.assertEqual(self.state["ups"], 2)

    def test_build_failure_keeps_old_container(self):
        self.state["fail_build"] = True
        result = self.run_update()
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.state["running"], "sha256:old")
        self.assertNotIn("ups", self.state)

    def test_recreate_failure_restores_previous_image(self):
        self.state["fail_up_once"] = True
        result = self.run_update()
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.state["running"], "sha256:old")
        self.assertEqual(self.state.get("ups"), 2, result.stdout + result.stderr)

    def test_first_frame_failure_restores_previous_image(self):
        self.state["fail_probe"] = True
        result = self.run_update()
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.state["running"], "sha256:old")
        self.assertEqual(self.state.get("ups"), 2, result.stdout + result.stderr)

    def test_repeated_image_does_not_replace_rollback_or_recreate(self):
        self.state.update(build_result="sha256:old", previous="sha256:previous")
        result = self.run_update()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(self.state["previous"], "sha256:previous")
        self.assertNotIn("ups", self.state)

    def test_checksum_failure_stops_before_build(self):
        (self.root / "server-bundle/notifier/fixture.txt").write_text("changed synthetic input")
        result = self.run_update()
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(any("build" in call or "up" in call for call in self.calls))

    def test_manual_rollback_reuses_fixed_previous_image(self):
        self.state.update(image="sha256:new", running="sha256:new", previous="sha256:old")
        (self.root / "docker/.notifier-previous-image").write_text("portkill-update-server:existing\n")
        result = self.run_update("--rollback")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(self.state["running"], "sha256:old")
        self.assertFalse(any("build" in call for call in self.calls))


class FirstFrameProbeTests(unittest.TestCase):
    def test_real_http_first_frame_without_a_published_manifest(self):
        class Feed(http.server.BaseHTTPRequestHandler):
            def do_GET(self):
                self.send_response(200)
                self.send_header("Content-Type", "text/event-stream")
                self.end_headers()
                self.wfile.write(b": connected\n\n")
                self.wfile.flush()

            def log_message(self, *_):
                pass

        server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Feed)
        worker = threading.Thread(target=server.serve_forever, daemon=True)
        worker.start()
        try:
            container = [{"State":{"Running":True}, "NetworkSettings":{"Networks":{"synthetic":{"IPAddress":"127.0.0.1"}}}, "Config":{"Cmd":[f"http://0.0.0.0:{server.server_port}"]}}]
            result = subprocess.run([sys.executable, str(REPOSITORY / "deploy/docker/check-notifier.py"), "--timeout", "2"], input=json.dumps(container), capture_output=True, text=True, timeout=5)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertIn("SSE first frame verified", result.stdout)
        finally:
            server.shutdown()
            server.server_close()
            worker.join()


if __name__ == "__main__":
    unittest.main(verbosity=2)
