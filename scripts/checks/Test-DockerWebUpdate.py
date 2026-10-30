"""Exercise the real Bash web updater with disposable files and a fake Docker CLI."""
import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import tarfile
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
BASH = shutil.which("bash") if os.name != "nt" else "C:/Program Files/Git/bin/bash.exe"


def write(path, text):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8", newline="\n")
    if os.name != "nt":
        path.chmod(0o755)


def shell_path(path):
    result = Path(path).resolve().as_posix()
    return "/" + result[0].lower() + result[2:] if os.name == "nt" else result


class WebUpdateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="portkill-web-test-")
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.package = self.base / "package with spaces"
        self.target = self.base / "deployment with spaces"
        self.web = self.target / "server-bundle/web"
        self.mock = self.base / "mock"
        self.log = self.base / "calls.log"
        write(self.package / "docker-web-update.sh", (ROOT / "deploy/docker/docker-web-update.sh").read_text(encoding="utf-8"))
        write(self.package / "server-bundle/web/index.html", '<script src="/assets/new.js"></script>new index\n')
        write(self.package / "server-bundle/web/assets/new.js", "new javascript\n")
        write(self.web / "index.html", "old index\n")
        write(self.web / "assets/old.js", "old javascript\n")
        for name, content in ((".env", "SECRET=test\n"), ("docker-compose.yml", "original compose\n"),
                              ("docker/Caddyfile", "original caddy\n"), ("data/admin/db", "original database\n"),
                              ("data/public/manifest.json", "original manifest\n"), ("data/state/state", "original state\n")):
            write(self.target / name, content)
        self.protected = {str(path.relative_to(self.target)): path.read_bytes()
                          for path in self.target.rglob("*") if path.is_file() and self.web not in path.parents}
        self.sums()
        write(self.mock / "id", '#!/usr/bin/env bash\necho 0\n')
        write(self.mock / "uname", '#!/usr/bin/env bash\necho Linux\n')
        # Git Bash has no flock; actual OS advisory locking is checked on Ubuntu.
        write(self.mock / "flock", '#!/usr/bin/env bash\n[[ "${FAKE_LOCKED:-0}" != 1 ]]\n')
        write(self.mock / "docker", '''#!/usr/bin/env bash
set -eu
printf '%s\n' "$*" >> "$FAKE_LOG"
if [[ "$1" == compose ]]; then
    case " $* " in
        *" config --quiet "*) exit 0 ;;
        *" ps -q caddy "*) [[ "${FAKE_MISSING:-0}" == 1 ]] || echo caddy-one; exit 0 ;;
    esac
    echo 'unexpected compose mutation' >&2; exit 81
fi
if [[ "$1" == inspect ]]; then
    case "$3" in
        *State.Running*) echo true ;;
        *Config.Labels*) echo "${FAKE_PROJECT:-portkill}" ;;
        *Mounts*) echo "${FAKE_MOUNT:-$FAKE_WEB}" ;;
        *) exit 82 ;;
    esac
    exit 0
fi
if [[ "$1" == exec && "$3" == sha256sum ]]; then
    relative="${4#/srv/web/}"
    if [[ "${FAKE_BAD_ASSET:-0}" == 1 && "$relative" == assets/new.js ]]; then
        echo "0000000000000000000000000000000000000000000000000000000000000000  $4"
    else
        sha256sum "$FAKE_WEB/$relative"
    fi
    exit 0
fi
exit 83
''')
        self.env = os.environ.copy()
        self.env.update(FAKE_BIN=shell_path(self.mock), FAKE_LOG=shell_path(self.log), FAKE_WEB=shell_path(self.web))

    def sums(self):
        write(self.package / "SHA256SUMS.txt", "".join(
            f"{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.relative_to(self.package).as_posix()}\n"
            for path in sorted(self.package.rglob("*")) if path.is_file() and path.name != "SHA256SUMS.txt"))

    def run_script(self, rollback=None, script=None):
        args = [BASH, "-c", 'export PATH="$FAKE_BIN:/usr/bin:/bin"; exec /bin/bash "$@"',
                "web-test", shell_path(script or self.package / "docker-web-update.sh")]
        if rollback:
            args.append("--rollback")
        args += [shell_path(self.target), "portkill"]
        if rollback:
            args.append(shell_path(rollback))
        result = subprocess.run(args, env=self.env, capture_output=True, encoding="utf-8", errors="replace", timeout=30)
        self.calls = self.log.read_text(encoding="utf-8") if self.log.exists() else ""
        return result

    def assert_protected(self):
        for name, contents in self.protected.items():
            self.assertEqual(contents, (self.target / name).read_bytes(), name)
        self.assertNotIn(" build ", self.calls)
        self.assertNotIn(" stop ", self.calls)
        self.assertNotIn(" restart ", self.calls)
        self.assertNotIn(" up ", self.calls)

    def test_update_and_rollback_preserve_mount_data_and_old_assets(self):
        inode = self.web.stat().st_ino
        result = self.run_script()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(inode, self.web.stat().st_ino)
        self.assertEqual((self.package / "server-bundle/web/index.html").read_bytes(), (self.web / "index.html").read_bytes())
        self.assertTrue((self.web / "assets/old.js").is_file())
        backup, = self.base.glob("PortKill-web-backup-*")
        with tarfile.open(backup / "web.tar.gz") as archive:
            self.assertEqual(b"old index\n", archive.extractfile("./index.html").read())
        result = self.run_script(rollback=backup)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(b"old index\n", (self.web / "index.html").read_bytes())
        self.assertTrue((self.web / "assets/new.js").is_file())
        self.assertEqual(2, len(list(self.base.glob("PortKill-web-backup-*"))))
        self.assertEqual(inode, self.web.stat().st_ino)
        self.assert_protected()

    def test_tampered_package_is_rejected_before_backup(self):
        write(self.package / "server-bundle/web/assets/new.js", "tampered\n")
        self.assertNotEqual(0, self.run_script().returncode)
        self.assertEqual(b"old index\n", (self.web / "index.html").read_bytes())
        self.assertEqual([], list(self.base.glob("PortKill-web-backup-*")))
        self.assert_protected()

    def test_wrong_project_is_rejected(self):
        self.env["FAKE_PROJECT"] = "ubuntu"
        self.assertNotEqual(0, self.run_script().returncode)
        self.assertEqual(b"old index\n", (self.web / "index.html").read_bytes())
        self.assert_protected()

    def test_wrong_mount_is_rejected(self):
        self.env["FAKE_MOUNT"] = "/other/deployment/server-bundle/web"
        self.assertNotEqual(0, self.run_script().returncode)
        self.assertEqual(b"old index\n", (self.web / "index.html").read_bytes())
        self.assert_protected()

    def test_missing_caddy_is_rejected(self):
        self.env["FAKE_MISSING"] = "1"
        self.assertNotEqual(0, self.run_script().returncode)
        self.assertEqual(b"old index\n", (self.web / "index.html").read_bytes())
        self.assert_protected()

    def test_concurrent_update_is_rejected(self):
        self.env["FAKE_LOCKED"] = "1"
        self.assertNotEqual(0, self.run_script().returncode)
        self.assertEqual(b"old index\n", (self.web / "index.html").read_bytes())
        self.assert_protected()

    def overwrite_in_place(self):
        for path in self.package.rglob("*"):
            if path.is_file():
                destination = self.target / path.relative_to(self.package)
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(path, destination)
        return self.target / "docker-web-update.sh"

    def test_in_place_extraction_is_verified_without_copy_or_backup(self):
        script = self.overwrite_in_place()
        before = {p.relative_to(self.web): p.read_bytes() for p in self.web.rglob("*") if p.is_file()}
        inode = self.web.stat().st_ino
        for _ in range(2):
            result = self.run_script(script=script)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn("直接覆盖已生效", result.stdout)
            self.assertIn("未生成覆盖前备份", result.stdout)
            self.assertEqual([], list(self.base.glob("PortKill-web-backup-*")))
            self.assertEqual(inode, self.web.stat().st_ino)
            self.assertEqual(before, {p.relative_to(self.web): p.read_bytes() for p in self.web.rglob("*") if p.is_file()})
        self.assertFalse((self.target / "portkill-login-update").exists())
        self.assert_protected()

    def test_in_place_tampered_payload_is_rejected(self):
        script = self.overwrite_in_place()
        write(self.web / "assets/new.js", "tampered\n")
        self.assertNotEqual(0, self.run_script(script=script).returncode)
        self.assertEqual([], list(self.base.glob("PortKill-web-backup-*")))
        self.assert_protected()

    def test_in_place_wrong_project_is_rejected(self):
        script = self.overwrite_in_place()
        self.env["FAKE_PROJECT"] = "ubuntu"
        self.assertNotEqual(0, self.run_script(script=script).returncode)
        self.assertEqual([], list(self.base.glob("PortKill-web-backup-*")))
        self.assert_protected()

    def test_in_place_container_asset_mismatch_is_rejected(self):
        script = self.overwrite_in_place()
        self.env["FAKE_BAD_ASSET"] = "1"
        result = self.run_script(script=script)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("assets/new.js", result.stderr)
        self.assertEqual([], list(self.base.glob("PortKill-web-backup-*")))
        self.assert_protected()

    def test_nested_package_is_rejected(self):
        nested = self.target / "nested package"
        shutil.copytree(self.package, nested)
        self.assertNotEqual(0, self.run_script(script=nested / "docker-web-update.sh").returncode)
        self.assertEqual(b"old index\n", (self.web / "index.html").read_bytes())
        self.assert_protected()

    def test_tampered_backup_is_rejected_without_changing_current_web(self):
        self.assertEqual(0, self.run_script().returncode)
        backup, = self.base.glob("PortKill-web-backup-*")
        with (backup / "web.tar.gz").open("ab") as stream:
            stream.write(b"tampered")
        contents = (self.web / "index.html").read_bytes()
        self.assertNotEqual(0, self.run_script(rollback=backup).returncode)
        self.assertEqual(contents, (self.web / "index.html").read_bytes())
        self.assertEqual(1, len(list(self.base.glob("PortKill-web-backup-*"))))
        self.assert_protected()


if __name__ == "__main__":
    unittest.main(verbosity=2)
