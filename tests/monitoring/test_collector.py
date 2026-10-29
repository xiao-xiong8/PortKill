import base64
import importlib.util
import io
import json
import os
from pathlib import Path
import tempfile
import threading
import unittest
from unittest.mock import patch
import urllib.error

spec = importlib.util.spec_from_file_location("collector", Path(__file__).resolve().parents[2] / "deploy/docker/monitor/collector.py")
monitor = importlib.util.module_from_spec(spec)
spec.loader.exec_module(monitor)


class CollectorTests(unittest.TestCase):
    def test_cpu_guest_is_not_counted_twice_and_baseline_is_unknown(self):
        first = monitor.cpu_counters("cpu 100 0 20 800 10 0 0 0 80 0")
        second = monitor.cpu_counters("cpu 150 0 20 850 10 0 0 0 110 0")
        self.assertEqual(50, monitor.cpu_percent(first, second))
        self.assertIsNone(monitor.cpu_percent(None, second))
        self.assertIsNone(monitor.cpu_percent(second, second))
        self.assertIsNone(monitor.cpu_percent(second, first))

    def test_memory_uses_available_not_free(self):
        self.assertEqual((102400, 61440), monitor.memory_values("MemTotal: 100 kB\nMemAvailable: 40 kB\nMemFree: 10 kB"))
        with self.assertRaises(ValueError):
            monitor.memory_values("MemTotal: 100 kB\nMemAvailable: 200 kB")

    def test_routes_choose_business_interface_and_lowest_metric(self):
        route = "Iface Destination Gateway Flags RefCnt Use Metric Mask\n"
        route += "docker0 00000000 0 0001 0 0 0 0\nens3 00000000 0 0001 0 0 20 0\neth0 00000000 0 0001 0 0 10 0"
        self.assertEqual("eth0", monitor.default_interface(route))

    def test_network_uses_elapsed_monotonic_time_and_breaks_on_reset(self):
        first = ("eth0", 100, 200, 10)
        self.assertEqual((100, 200), monitor.network_rates(first, ("eth0", 300, 600, 12)))
        for current in (("ens3", 300, 600, 12), ("eth0", 0, 600, 12), ("eth0", 300, 600, 10)):
            self.assertEqual((None, None), monitor.network_rates(first, current))

    def collector(self, directory):
        root = Path(directory)
        (root / "stat").write_text("cpu 100 0 20 800 10 0 0 0 80 0")
        (root / "meminfo").write_text("MemTotal: 100 kB\nMemAvailable: 40 kB")
        stats = root / "class/net/eth0/statistics"
        stats.mkdir(parents=True)
        (stats / "rx_bytes").write_text("100")
        (stats / "tx_bytes").write_text("200")
        return monitor.Collector(dict(MONITOR_HOST_PROC=str(root), MONITOR_HOST_SYS=str(root), MONITOR_INTERFACE="eth0",
                                      MONITOR_DOWNLOAD_URL="https://updates.example/stable/manifest.json", MONITOR_OUTPUT=str(root / "snapshot/status.json")))

    def test_resource_failure_retains_observation_time_and_history_is_bounded(self):
        with tempfile.TemporaryDirectory() as directory:
            collector = self.collector(directory)
            for index in range(190):
                with patch.object(monitor, "utc_ms", return_value=1_000_000 + index * 5000):
                    collector.sample_resources()
            self.assertEqual(181, len(collector.history))
            before = collector.state["resources"]["memory"]["observedAtUtcMs"]
            (Path(directory) / "meminfo").unlink()
            collector.sample_resources()
            memory = collector.state["resources"]["memory"]
            self.assertEqual(before, memory["observedAtUtcMs"])
            self.assertEqual("memory_unavailable", memory["error"])
            self.assertIsNone(collector.history[-1]["memoryPercent"])

    def test_history_is_evicted_by_time_too(self):
        with tempfile.TemporaryDirectory() as directory:
            collector = self.collector(directory)
            with patch.object(monitor, "utc_ms", return_value=1_000_000):
                collector.sample_resources()
            with patch.object(monitor, "utc_ms", return_value=2_000_000):
                collector.sample_resources()
            self.assertEqual(1, len(collector.history))

    def test_disk_deduplication_and_non_privileged_free_space(self):
        with tempfile.TemporaryDirectory() as directory:
            collector = self.collector(directory)
            stats = type("Stats", (), dict(f_frsize=4096, f_blocks=1000, f_bavail=100))()
            with patch.object(monitor.os, "stat", return_value=type("Stat", (), dict(st_dev=5))()), patch.object(monitor.os, "statvfs", return_value=stats, create=True):
                collector.sample_disks()
            disks = collector.state["resources"]["disks"]
            self.assertEqual(1, len(disks))
            self.assertEqual("管理数据 / 更新包", disks[0]["label"])
            self.assertEqual(409600, disks[0]["freeBytes"])

    def test_docker_stopped_and_missing_are_different_from_query_failure(self):
        row = dict(service="api", oneoff="False", containerId="a" * 64, state="exited", health="not_configured", restartCount=2)
        with patch.object(monitor, "docker_command", side_effect=["a" * 12, json.dumps(row)]):
            result = monitor.docker_observation("existing-project")
        self.assertEqual("exited", result["services"][0]["state"])
        self.assertEqual("missing", result["services"][1]["state"])
        with tempfile.TemporaryDirectory() as directory:
            collector = self.collector(directory)
            collector.state["docker"] = result
            with patch.object(monitor, "docker_observation", side_effect=OSError()):
                collector.sample_docker()
            self.assertEqual(result["observedAtUtcMs"], collector.state["docker"]["observedAtUtcMs"])
            self.assertEqual("docker_unavailable", collector.state["docker"]["error"])

    def test_duplicate_service_is_ambiguous(self):
        row = dict(service="api", containerId="a" * 64, state="running", health="healthy", restartCount=0)
        with patch.object(monitor, "docker_command", side_effect=["aaa bbb", json.dumps(row) + "\n" + json.dumps(row)]):
            self.assertEqual("ambiguous", monitor.docker_observation("p")["services"][0]["state"])

    def response(self, body):
        response = io.BytesIO(json.dumps(body).encode())
        response.status = 200
        return response

    def test_http_200_is_not_application_health(self):
        with patch.object(monitor.urllib.request, "urlopen", return_value=self.response(dict(database="unavailable", manifest=dict(status="expired")))):
            result = monitor.http_observation("api", "http://api/health/ready")
            self.assertEqual("failed", result["status"])
            self.assertEqual("expired", result["manifestStatus"])
        with patch.object(monitor.urllib.request, "urlopen", return_value=self.response(dict(running=True, manifestValid=False, failure="private text"))):
            result = monitor.http_observation("notifier", "http://notifier/health")
            self.assertEqual("healthy", result["status"])
            self.assertFalse(result["manifestValid"])
            self.assertNotIn("private", json.dumps(result))

    def test_http_404_and_transport_errors_remain_distinct(self):
        error = urllib.error.HTTPError("https://example", 404, "Not Found", {}, None)
        with patch.object(monitor.urllib.request, "urlopen", side_effect=error):
            self.assertEqual("missing", monitor.http_observation("download", "https://example")["status"])
        with patch.object(monitor.urllib.request, "urlopen", side_effect=OSError("secret")):
            result = monitor.http_observation("download", "https://example")
            self.assertIsNone(result["observedAtUtcMs"])
            self.assertEqual("probe_unavailable", result["error"])

    def test_download_rejects_error_json_and_malformed_signed_envelopes(self):
        payload = base64.b64encode(json.dumps(dict(schema=2, version="1.2.3")).encode()).decode()
        signature = base64.b64encode(b"structural-test-not-a-verified-signature").decode()
        invalid = [{}, dict(error="maintenance"), dict(Payload=payload),
                   dict(Payload=payload, Signature=123), dict(Payload="%%%", Signature=signature),
                   dict(Payload=payload, Signature="%%%"), dict(Payload=payload, Signature=""),
                   dict(Payload=base64.b64encode(b"not-json").decode(), Signature=signature),
                   dict(Payload=base64.b64encode(b"[]").decode(), Signature=signature),
                   dict(Payload=base64.b64encode(b"{}").decode(), Signature=signature),
                   dict(payload=payload, signature=signature)]
        for body in invalid:
            with self.subTest(body=body), patch.object(monitor.urllib.request, "urlopen", return_value=self.response(body)):
                result = monitor.http_observation("download", "https://example/stable/manifest.json")
                self.assertEqual("failed", result["status"])
                self.assertIsNotNone(result["observedAtUtcMs"])
                self.assertIsNone(result["error"])
                self.assertEqual(200, result["httpStatus"])

    def test_download_accepts_signed_envelope_structure_without_claiming_signature_trust(self):
        body = dict(Payload=base64.b64encode(json.dumps(dict(Schema=2, Version="1.2.3")).encode()).decode(),
                    Signature=base64.b64encode(b"structural-test-not-a-verified-signature").decode())
        with patch.object(monitor.urllib.request, "urlopen", return_value=self.response(body)):
            result = monitor.http_observation("download", "https://example/stable/manifest.json")
        self.assertEqual("healthy", result["status"])
        self.assertIsNone(result["manifestValid"])
        self.assertNotIn("payload", result)
        self.assertNotIn("signature", result)
        self.assertNotIn("Payload", result)
        self.assertNotIn("Signature", result)

    def test_failed_probe_preserves_last_observation_time_without_claiming_health(self):
        with tempfile.TemporaryDirectory() as directory:
            collector = self.collector(directory)
            with patch.object(monitor.urllib.request, "urlopen", return_value=self.response(dict(database="healthy", manifest=dict(status="healthy")))):
                collector.sample_probe("api")
            before = collector.state["appChecks"][0]["observedAtUtcMs"]
            with patch.object(monitor.urllib.request, "urlopen", side_effect=OSError()):
                collector.sample_probe("api")
            failed = collector.state["appChecks"][0]
            self.assertEqual(before, failed["observedAtUtcMs"])
            self.assertEqual("unknown", failed["status"])
            self.assertIsNotNone(failed["error"])

    def test_atomic_snapshot_replaces_complete_json_and_rejects_oversize(self):
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / "status.json"
            monitor.write_snapshot(target, dict(number=1))
            monitor.write_snapshot(target, dict(number=2))
            self.assertEqual(2, json.loads(target.read_text())["number"])
            with self.assertRaises(ValueError):
                monitor.write_snapshot(target, dict(data="x" * monitor.MAX_BYTES))
            self.assertEqual(2, json.loads(target.read_text())["number"])

    @unittest.skipIf(os.name == "nt", "Concurrent replace semantics require the target Linux filesystem")
    def test_atomic_snapshot_can_be_read_during_replacements(self):
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / "status.json"
            monitor.write_snapshot(target, dict(number=0))
            failures = []
            def read():
                for _ in range(100):
                    try:
                        json.loads(target.read_text())
                    except Exception as error:
                        failures.append(error)
            reader = threading.Thread(target=read)
            reader.start()
            for index in range(50):
                monitor.write_snapshot(target, dict(number=index))
            reader.join()
            self.assertFalse(failures)
            self.assertEqual(49, json.loads(target.read_text())["number"])
            with self.assertRaises(ValueError):
                monitor.write_snapshot(target, dict(data="x" * monitor.MAX_BYTES))
            self.assertEqual(49, json.loads(target.read_text())["number"])


if __name__ == "__main__":
    unittest.main()
