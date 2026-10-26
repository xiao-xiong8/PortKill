"""Check the Compose edit used by the state-directory hotfix without running Docker."""
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = (ROOT / "deploy/docker/fix-publish-state.sh").read_text(encoding="utf-8")
EDIT = SCRIPT.split("python3 - <<'PY'\n", 1)[1].split("\nPY\n", 1)[0]
COMPOSE = """services:
  api:
    volumes:
    - ./data/public:/srv/portkill/stable:ro
  worker:
    image: portkill-update-server:28d19c7
    volumes:
    - ./data/admin:/var/lib/portkill/admin
    - ./data/public:/srv/portkill/stable
    - ./verification.json:/etc/portkill/verification.json:ro
  notifier:
    volumes:
    - ./data/public:/srv/portkill/stable:ro
  caddy:
    volumes:
    - ./data/public:/srv/public:ro
"""
MOUNT = "    - ./data/state:/srv/portkill/.stable-update-state\n"


class StateMountTests(unittest.TestCase):
    def run_edit(self, root):
        return subprocess.run([sys.executable, "-c", EDIT], cwd=root, text=True, capture_output=True)

    def test_only_worker_mount_changes_and_original_is_backed_up(self):
        with tempfile.TemporaryDirectory(prefix="PortKill-StateFix-Test-") as root:
            path = Path(root) / "docker-compose.yml"
            path.write_text(COMPOSE, encoding="utf-8")
            result = self.run_edit(root)
            self.assertEqual(0, result.returncode, result.stderr)
            changed = path.read_text(encoding="utf-8")
            self.assertEqual(COMPOSE, changed.replace(MOUNT, ""))
            self.assertIn(MOUNT, changed.split("  worker:\n", 1)[1].split("  notifier:\n", 1)[0])
            self.assertEqual(COMPOSE, path.with_name(path.name + ".before-state-fix").read_text(encoding="utf-8"))
            result = self.run_edit(root)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual(changed, path.read_text(encoding="utf-8"))

    def test_conflicting_state_mount_is_not_overwritten(self):
        original = COMPOSE.replace("    - ./data/admin:/var/lib/portkill/admin\n",
                                   "    - ./different-state:/srv/portkill/.stable-update-state\n")
        with tempfile.TemporaryDirectory(prefix="PortKill-StateFix-Test-") as root:
            path = Path(root) / "docker-compose.yml"
            path.write_text(original, encoding="utf-8")
            result = self.run_edit(root)
            self.assertNotEqual(0, result.returncode)
            self.assertEqual(original, path.read_text(encoding="utf-8"))
            self.assertFalse(path.with_name(path.name + ".before-state-fix").exists())

    def test_unsupported_public_root_is_not_modified(self):
        original = COMPOSE.replace("./data/public:/srv/portkill/stable\n", "./custom:/other/root\n")
        with tempfile.TemporaryDirectory(prefix="PortKill-StateFix-Test-") as root:
            path = Path(root) / "docker-compose.yml"
            path.write_text(original, encoding="utf-8")
            self.assertNotEqual(0, self.run_edit(root).returncode)
            self.assertEqual(original, path.read_text(encoding="utf-8"))

    def test_only_worker_is_recreated_without_build_or_pull(self):
        self.assertIn("docker compose up -d --no-deps --no-build --pull never worker", SCRIPT)
        self.assertNotIn("|| true", SCRIPT)


if __name__ == "__main__":
    unittest.main(verbosity=2)
