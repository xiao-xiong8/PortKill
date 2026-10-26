"""Publisher deployment tests using an isolated synthetic Docker daemon."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

REPOSITORY = Path(__file__).resolve().parents[2]
BASH = str(Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "Git/bin/bash.exe") if os.name == "nt" else shutil.which("bash")
MOCK = r'''
import json, os, pathlib, sys
root = pathlib.Path(os.environ["PORTKILL_TEST_ROOT"])
path = root / "state.json"
s = json.loads(path.read_text())
a = sys.argv[1:]
with (root / "calls.jsonl").open("a") as log: log.write(json.dumps(a) + "\n")
def save(): path.write_text(json.dumps(s))
def image(ref): return s.get({"portkill-update-server:existing":"image", "portkill-publisher:update-base":"pending", "portkill-publisher:previous":"previous"}.get(ref, ""), ref)
if a[:2] == ["compose", "config"]:
    if "--format" in a:
        mounts = [] if s.get("no_mount") else [{"type":"bind", "source":str(root / "data/state"), "target":"/srv/portkill/.stable-update-state"}]
        print(json.dumps({"services":{"worker":{"image":"portkill-update-server:existing", "build":{"context":str(root),"dockerfile":"docker/Dockerfile"}, "volumes":mounts}}}))
elif a[:2] == ["compose", "ps"]:
    if "-q" in a: print("worker-current")
elif a[:1] == ["inspect"]:
    print(json.dumps([{"Id":"worker-current", "Image":s["running"], "Config":{"Image":"portkill-update-server:existing"}}]))
elif a[:2] == ["image", "inspect"]:
    value = image(a[-1])
    if value not in ["sha256:old", "sha256:new"]: sys.exit(1)
    print(value if "--format" in a else json.dumps([{"Id":value}]))
elif a[:2] == ["image", "tag"]:
    key = {"portkill-update-server:existing":"image", "portkill-publisher:update-base":"pending", "portkill-publisher:previous":"previous"}[a[-1]]
    if s.get("fail_pin") and key == "pending": sys.exit(1)
    s[key] = image(a[-2]); save()
elif a[:2] == ["image", "rm"]:
    assert a[2:] == ["portkill-publisher:update-base"]
    s.pop("pending", None); save()
elif a[:4] == ["compose", "--progress", "plain", "build"]:
    assert a[4:] == ["worker"]
    s["image"] = "sha256:old" if s.get("same_image") else "sha256:new"; save()
    if s.get("fail_build"): sys.exit(1)
elif a[:1] == ["run"]:
    assert a[1:5] == ["--rm", "--network", "none", "--mount"]
    assert a[-1] == "--self-check-tool-owner"
    assert "/opt/portkill/worker/PortKiller.UpdatePublisher.Worker" in a
    if s.get("fail_check"): sys.exit(1)
    print('{"Check":"publisher-tool-owner","Succeeded":true,"HasCurrent":false}')
elif a[:2] == ["compose", "up"]:
    assert a[2:] == ["-d", "--no-deps", "--no-build", "--pull", "never", "--force-recreate", "worker"]
    s["ups"] = s.get("ups", 0) + 1; save()
    if s.get("fail_up_once") and s["ups"] == 1: sys.exit(1)
    s["running"] = s["image"]; save()
elif a[:2] == ["compose", "exec"]:
    assert a[2:] == ["-T", "worker", "/bin/true"]
    if s.get("fail_running") and s["running"] == "sha256:new": sys.exit(1)
else: raise RuntimeError(repr(a))
'''


class PublisherUpdaterTests(unittest.TestCase):
    def setUp(self):
        parent = REPOSITORY / ".local/tmp"
        parent.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="publisher-test-", dir=parent)
        self.root = Path(self.temp.name).resolve()
        self.assertTrue(self.root.is_relative_to(parent.resolve()))
        self.addCleanup(self.temp.cleanup)
        for directory in ["docker", "mock-bin", "data/state", "server-bundle/worker"]:
            (self.root / directory).mkdir(parents=True)
        for name in ["update-publisher.sh", "resolve-notifier-image.py"]:
            (self.root / "docker" / name).write_text((REPOSITORY / "deploy/docker" / name).read_text(encoding="utf-8"), encoding="utf-8", newline="\n")
        self.protected = {".env":"SYNTHETIC=unchanged\n", "verification.json":"{}", "docker-compose.yml":"# isolated fixture\n", "docker/Dockerfile":"# fixture\n", "data/state/marker":"synthetic persistent data"}
        for name, content in self.protected.items(): (self.root / name).write_text(content, encoding="utf-8")
        payload = self.root / "server-bundle/worker/fixture.txt"
        payload.write_text("synthetic payload")
        files = [payload, self.root / "docker/update-publisher.sh", self.root / "docker/resolve-notifier-image.py"]
        (self.root / "docker/publisher-update.sha256").write_text("".join(f"{hashlib.sha256(f.read_bytes()).hexdigest()}  {f.relative_to(self.root).as_posix()}\n" for f in files), encoding="ascii", newline="\n")
        (self.root / "mock.py").write_text(MOCK, encoding="utf-8")
        wrappers = {
            "docker": '#!/usr/bin/env bash\nexec "$PORTKILL_TEST_PYTHON" "$PORTKILL_TEST_DOCKER" "$@"\n',
            "python3": '#!/usr/bin/env bash\nexec "$PORTKILL_TEST_PYTHON" "$@"\n',
            "id": '#!/usr/bin/env bash\necho 0\n',
        }
        if os.name == "nt":
            wrappers["flock"] = '#!/usr/bin/env bash\nexit 0\n'
            (self.root / "mock-bin/docker.cmd").write_text('@"%PORTKILL_TEST_PYTHON%" "%PORTKILL_TEST_DOCKER%" %*\n', encoding="ascii")
        for name, body in wrappers.items():
            file = self.root / "mock-bin" / name
            file.write_text(body, encoding="utf-8", newline="\n")
            file.chmod(0o755)
        self.state = {"image":"sha256:old", "running":"sha256:old"}

    def run_update(self):
        (self.root / "state.json").write_text(json.dumps(self.state))
        environment = dict(os.environ, PYTHONUTF8="1", MSYS_NO_PATHCONV="1", PORTKILL_TEST_ROOT=str(self.root), PORTKILL_TEST_PYTHON=Path(sys.executable).as_posix(), PORTKILL_TEST_DOCKER=(self.root / "mock.py").as_posix(), PORTKILL_TEST_BIN=(self.root / "mock-bin").as_posix(), PORTKILL_TEST_SCRIPT=(self.root / "docker/update-publisher.sh").as_posix())
        prefix = 'export PATH="$(cygpath -u "$PORTKILL_TEST_BIN"):$PATH";' if os.name == "nt" else 'export PATH="$PORTKILL_TEST_BIN:$PATH";'
        result = subprocess.run([BASH, "--noprofile", "--norc", "-c", prefix + ' exec bash "$PORTKILL_TEST_SCRIPT"'], cwd=self.root, env=environment, capture_output=True, text=True, encoding="utf-8", timeout=40)
        self.state = json.loads((self.root / "state.json").read_text())
        calls = self.root / "calls.jsonl"
        self.calls = [json.loads(line) for line in calls.read_text().splitlines()] if calls.exists() else []
        for name, content in self.protected.items(): self.assertEqual((self.root / name).read_text(encoding="utf-8"), content)
        return result

    def test_success_checks_before_recreating_only_worker(self):
        result = self.run_update()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(self.state["running"], "sha256:new")
        self.assertEqual(self.state["previous"], "sha256:old")
        self.assertEqual(self.state["ups"], 1)
        self.assertLess(next(i for i,c in enumerate(self.calls) if c[0] == "run"), next(i for i,c in enumerate(self.calls) if c[:2] == ["compose", "up"]))

    def test_failures_restore_image_tag_and_leave_old_container(self):
        for failure in ["fail_build", "fail_check"]:
            with self.subTest(failure=failure):
                self.state = {"image":"sha256:old", "running":"sha256:old", failure:True}
                result = self.run_update()
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(self.state["image"], "sha256:old")
                self.assertEqual(self.state["running"], "sha256:old")
                self.assertEqual(self.state["previous"], "sha256:old")
                self.assertNotIn("ups", self.state)

    def test_cutover_failures_roll_back(self):
        for failure in ["fail_up_once", "fail_running"]:
            with self.subTest(failure=failure):
                self.state = {"image":"sha256:old", "running":"sha256:old", failure:True}
                result = self.run_update()
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(self.state["running"], "sha256:old")
                self.assertEqual(self.state["ups"], 2)

    def test_unchanged_build_preserves_previous_image(self):
        self.state.update(same_image=True, previous="older-image")
        result = self.run_update()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertNotIn("ups", self.state)
        self.assertEqual(self.state["previous"], "older-image")

    def test_pin_failure_stops_before_build(self):
        self.state["fail_pin"] = True
        result = self.run_update()
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(any("build" in c or "up" in c for c in self.calls))

    def test_missing_mount_stops_before_build(self):
        self.state["no_mount"] = True
        result = self.run_update()
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(any("build" in c or "up" in c for c in self.calls))

    def test_changed_payload_stops_before_docker(self):
        (self.root / "server-bundle/worker/fixture.txt").write_text("changed fixture")
        result = self.run_update()
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.calls, [])


if __name__ == "__main__":
    unittest.main(verbosity=2)
