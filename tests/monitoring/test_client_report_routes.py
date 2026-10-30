import importlib.util
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
PACKAGE = ROOT / "deploy/docker/package"
spec = importlib.util.spec_from_file_location("client_report_routes", PACKAGE / "enable-client-reports.py")
routes = importlib.util.module_from_spec(spec)
spec.loader.exec_module(routes)
BASH = "C:/Program Files/Git/bin/bash.exe" if os.name == "nt" else shutil.which("bash")
ORIGINAL = """{$ADMIN_DOMAIN} {
    handle /api/v1/* {
        reverse_proxy api:5081
    }
}
{$UPDATES_DOMAIN} {
    handle /stable/events {
        reverse_proxy notifier:8091
    }
    handle /stable/* {
        file_server
    }
    handle {
        respond 404
    }
}
"""


def shell_path(path):
    value = Path(path).resolve().as_posix()
    return "/" + value[0].lower() + value[2:] if os.name == "nt" else value


def write(path, content):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(content, encoding="utf-8", newline="\n")
    if os.name != "nt":
        path.chmod(0o755)


class ClientReportRoutesTests(unittest.TestCase):
    def test_only_two_post_endpoints_are_exposed_and_host_filtering_is_preserved(self):
        changed = routes.configure_caddy(ORIGINAL)
        self.assertEqual(ORIGINAL, changed.replace(routes.REPORT_ROUTES, ""))
        self.assertIn("method POST", routes.REPORT_ROUTES)
        self.assertIn("header_up Host {$ADMIN_DOMAIN}", routes.REPORT_ROUTES)
        self.assertNotIn("/api/v1/*", routes.REPORT_ROUTES)
        self.assertEqual(changed, routes.configure_caddy(changed))

    def test_custom_layout_is_rejected_without_guessing(self):
        for content in (ORIGINAL.replace("{$UPDATES_DOMAIN}", "custom.example"),
                        ORIGINAL + ORIGINAL, ORIGINAL.replace("handle /stable/events", "handle /events"),
                        ORIGINAL.replace("handle /stable/events", "handle /api/v1/client-installations/report")):
            with self.assertRaises(ValueError):
                routes.configure_caddy(content)

    def run_updater(self, *, invalid=False, reload_failure=False, mount_failure=False, already_configured=False):
        with tempfile.TemporaryDirectory(prefix="portkill-report-routes-") as directory:
            base = Path(directory)
            target = base / "deployment with spaces"
            package = base / "update with spaces"
            mocks = base / "mock"
            log = base / "calls.log"
            for name in ("enable-client-reports.py", "docker-client-reports-update.sh"):
                write(package / name, (PACKAGE / name).read_text(encoding="utf-8"))
            original = routes.configure_caddy(ORIGINAL) if already_configured else ORIGINAL
            write(target / "docker/Caddyfile", original)
            write(target / ".env", "EXISTING_SECRET=unchanged\n")
            write(target / "docker-compose.yml", "existing compose\n")
            write(mocks / "python3", f"#!/usr/bin/env bash\nexec '{shell_path(sys.executable)}' \"$@\"\n")
            write(mocks / "docker", """#!/usr/bin/env bash
set -eu
printf '%s\n' "$*" >> "$FAKE_LOG"
case " $* " in
    *" ps -q caddy "*) echo caddy-one ;;
    *" inspect "*) [[ "$FAKE_MOUNT_FAILURE" == 0 ]] && echo "$FAKE_CONFIG" || echo /wrong/Caddyfile ;;
    *" caddy validate "*) cat >/dev/null; [[ "$FAKE_INVALID" == 0 ]] ;;
    *" caddy reload "*) [[ "$FAKE_RELOAD_FAILURE" == 0 ]] ;;
    *) exit 90 ;;
esac
""")
            env = dict(os.environ, FAKE_BIN=shell_path(mocks), FAKE_LOG=shell_path(log),
                       FAKE_CONFIG=shell_path(target / "docker/Caddyfile"), FAKE_INVALID=str(int(invalid)),
                       FAKE_RELOAD_FAILURE=str(int(reload_failure)), FAKE_MOUNT_FAILURE=str(int(mount_failure)))
            result = subprocess.run([BASH, "-c", 'export PATH="$FAKE_BIN:/usr/bin:/bin"; exec /bin/bash "$@"',
                                     "report-test", shell_path(package / "docker-client-reports-update.sh"),
                                     shell_path(target), "portkill"], env=env, capture_output=True, encoding="utf-8", timeout=30)
            self.assertEqual("EXISTING_SECRET=unchanged\n", (target / ".env").read_text(encoding="utf-8"))
            self.assertEqual("existing compose\n", (target / "docker-compose.yml").read_text(encoding="utf-8"))
            config = (target / "docker/Caddyfile").read_text(encoding="utf-8")
            calls = log.read_text(encoding="utf-8") if log.exists() else ""
            if invalid or reload_failure or mount_failure:
                self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
                self.assertEqual(original, config)
            else:
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                self.assertEqual(routes.configure_caddy(ORIGINAL), config)
                self.assertEqual(0 if already_configured else 1, len(list((target / "docker").glob("Caddyfile.before-client-reports-*"))))
                self.assertIn(" caddy validate ", calls)
                self.assertIn(" caddy reload ", calls)
            for operation in (" down ", " stop ", " restart ", " build "):
                self.assertNotIn(operation, calls)

    @unittest.skipUnless(BASH and Path(BASH).exists(), "Bash required")
    def test_real_script_validates_backs_up_and_reloads(self):
        self.run_updater()

    @unittest.skipUnless(BASH and Path(BASH).exists(), "Bash required")
    def test_failed_validation_preserves_deployment(self):
        self.run_updater(invalid=True)

    @unittest.skipUnless(BASH and Path(BASH).exists(), "Bash required")
    def test_failed_reload_restores_original_config(self):
        self.run_updater(reload_failure=True)

    @unittest.skipUnless(BASH and Path(BASH).exists(), "Bash required")
    def test_wrong_mount_is_rejected(self):
        self.run_updater(mount_failure=True)

    @unittest.skipUnless(BASH and Path(BASH).exists(), "Bash required")
    def test_already_overwritten_config_is_still_loaded(self):
        self.run_updater(already_configured=True)


if __name__ == "__main__":
    unittest.main()
