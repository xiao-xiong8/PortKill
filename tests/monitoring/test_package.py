import copy
import importlib.util
from pathlib import Path
import unittest

import yaml

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("package_monitoring", ROOT / "scripts/release/New-DockerMonitoringUpdate.py")
package = importlib.util.module_from_spec(spec)
spec.loader.exec_module(package)


class PackageTests(unittest.TestCase):
    def setUp(self):
        self.template = yaml.safe_load((ROOT / "deploy/docker/monitor/compose.monitoring.example.yaml").read_text(encoding="utf-8"))
        self.original = dict(services=dict(
            api=dict(build=dict(context=".", dockerfile="docker/Dockerfile"), image="original-api",
                     environment=dict(AllowedHosts="${ADMIN_DOMAIN}"), networks=["api_proxy"],
                     volumes=["./data/admin:/var/lib/portkill/admin", "./data/public:/srv/portkill/stable:ro"]),
            notifier=dict(networks=["portkill"], image="original-notifier"),
            worker=dict(image="original-worker"), caddy=dict(image="original-caddy")),
            volumes=dict(caddy_data=None), networks=dict(api_proxy={}, portkill=None))

    def test_only_api_and_monitor_are_changed(self):
        before = copy.deepcopy(self.original)
        result = package.compose_monitoring(self.original, self.template, "new-api")
        self.assertEqual(before, self.original)
        for name in ("worker", "notifier", "caddy"):
            self.assertEqual(before["services"][name], result["services"][name])
        self.assertEqual("new-api", result["services"]["api"]["image"])
        self.assertEqual(before["networks"], result["networks"])
        self.assertEqual(["api_proxy", "portkill"], result["services"]["monitor"]["networks"])
        self.assertIn("monitor_snapshot:/run/portkill-monitor:ro", result["services"]["api"]["volumes"])
        self.assertNotIn("depends_on", result["services"]["api"])
        self.assertNotIn("ports", result["services"]["monitor"])
        self.assertTrue(result["services"]["monitor"]["read_only"])

    def test_unknown_mounts_or_topology_are_not_silently_replaced(self):
        for change in (lambda api: api.update(networks=["other"]), lambda api: api.update(volumes=[]),
                       lambda api: api.update(build=dict(context="other", dockerfile="Dockerfile"))):
            original = copy.deepcopy(self.original)
            change(original["services"]["api"])
            with self.assertRaises(ValueError):
                package.compose_monitoring(original, self.template, "new-api")

    def test_existing_monitor_requires_review(self):
        self.original["services"]["monitor"] = {}
        with self.assertRaises(ValueError):
            package.compose_monitoring(self.original, self.template, "new-api")


if __name__ == "__main__":
    unittest.main()
