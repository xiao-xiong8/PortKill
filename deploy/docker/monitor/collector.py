#!/usr/bin/env python3
"""Bounded, read-only observations for the PortKill Compose project."""
from __future__ import annotations

import base64
import copy
import json
import os
import signal
import subprocess
import threading
import time
import urllib.error
import urllib.request
import urllib.parse
from collections import deque
from pathlib import Path

SERVICES = ("api", "worker", "notifier", "caddy")
MAX_BYTES = 512 * 1024


def utc_ms():
    return time.time_ns() // 1_000_000


def cpu_counters(text):
    values = [int(value) for value in text.splitlines()[0].split()[1:9]]
    if len(values) < 8 or any(value < 0 for value in values):
        raise ValueError("cpu_counters")
    return sum(values), values[3] + values[4]


def cpu_percent(previous, current):
    if previous is None:
        return None
    total, idle = current[0] - previous[0], current[1] - previous[1]
    return round((total - idle) * 100 / total, 2) if total > 0 and 0 <= idle <= total else None


def memory_values(text):
    fields = {line.split(":", 1)[0]: int(line.split()[1]) * 1024 for line in text.splitlines() if ":" in line}
    total, available = fields["MemTotal"], fields["MemAvailable"]
    if total <= 0 or not 0 <= available <= total:
        raise ValueError("memory_counters")
    return total, total - available


def default_interface(route):
    candidates = []
    for line in route.splitlines()[1:]:
        fields = line.split()
        if len(fields) >= 8 and fields[1] == "00000000" and int(fields[3], 16) & 1:
            name = fields[0]
            if not name.startswith(("lo", "docker", "br-", "veth", "vxlan")):
                candidates.append((int(fields[6]), name))
    if not candidates:
        raise ValueError("network_interface")
    return min(candidates)[1]


def network_rates(previous, current):
    if previous is None or current[0] != previous[0] or current[3] <= previous[3]:
        return None, None
    rx, tx = current[1] - previous[1], current[2] - previous[2]
    if rx < 0 or tx < 0:
        return None, None
    elapsed = current[3] - previous[3]
    return round(rx / elapsed, 2), round(tx / elapsed, 2)


def write_snapshot(path, snapshot):
    encoded = json.dumps(snapshot, ensure_ascii=False, allow_nan=False, separators=(",", ":")).encode("utf-8")
    if len(encoded) > MAX_BYTES:
        raise ValueError("snapshot_too_large")
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o755)
    temporary = path.with_suffix(".tmp")
    with temporary.open("wb") as stream:
        stream.write(encoded)
        os.chmod(temporary, 0o644)
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temporary, path)


def docker_command(arguments):
    result = subprocess.run(["docker", *arguments], capture_output=True, timeout=8, check=True)
    if len(result.stdout) > 1024 * 1024:
        raise ValueError("docker_output_limit")
    return result.stdout.decode("utf-8")


def docker_observation(project):
    if not project:
        raise ValueError("compose_project_required")
    ids = docker_command(["ps", "-aq", "--filter", f"label=com.docker.compose.project={project}"]).split()
    if len(ids) > 64 or any(len(identifier) > 64 or not all(c in "0123456789abcdef" for c in identifier) for identifier in ids):
        raise ValueError("docker_container_limit")
    rows = []
    if ids:
        template = ('{"service":{{json (index .Config.Labels "com.docker.compose.service")}},'
                    '"oneoff":{{json (index .Config.Labels "com.docker.compose.oneoff")}},'
                    '"containerId":{{json .Id}},"state":{{json .State.Status}},'
                    '"health":{{with (index .State "Health")}}{{json .Status}}{{else}}"not_configured"{{end}},'
                    '"restartCount":{{.RestartCount}}}')
        rows = [json.loads(line) for line in docker_command(["inspect", "--format", template, *ids]).splitlines() if line]
    services = []
    for name in SERVICES:
        matches = [row for row in rows if row.get("service") == name and str(row.get("oneoff", "")).lower() != "true"]
        if len(matches) == 1:
            row = matches[0]
            services.append({key: row[key] for key in ("service", "containerId", "state", "health", "restartCount")})
        else:
            services.append(dict(service=name, containerId=None, state="missing" if not matches else "ambiguous",
                                 health="not_configured", restartCount=0))
    return dict(observedAtUtcMs=utc_ms(), error=None, services=services)


def manifest_envelope_valid(data):
    # Structure only. Signature trust and expiry remain with the API's existing verifier.
    try:
        payload, signature = data.get("Payload"), data.get("Signature")
        if not isinstance(payload, str) or not isinstance(signature, str) or not payload or not signature:
            return False
        decoded = json.loads(base64.b64decode(payload, validate=True))
        return isinstance(decoded, dict) and bool(decoded) and bool(base64.b64decode(signature, validate=True))
    except (ValueError, TypeError):
        return False


def http_observation(name, url, host=None):
    observation = dict(name=name, observedAtUtcMs=None, error=None, status="unknown", httpStatus=None,
                       database=None, manifestStatus=None, manifestValid=None)
    try:
        request = urllib.request.Request(url, headers={"Accept": "application/json", "User-Agent": "PortKillMonitor/1"})
        if host:
            request.add_header("Host", host)
        try:
            response = urllib.request.urlopen(request, timeout=5)
        except urllib.error.HTTPError as error:
            observation.update(observedAtUtcMs=utc_ms(), status="missing" if error.code == 404 else "failed", httpStatus=error.code)
            error.close()
            return observation
        with response:
            body = response.read(65537)
            observation["httpStatus"] = response.status
        if len(body) > 65536:
            raise ValueError("response_too_large")
        data = json.loads(body)
        if not isinstance(data, dict):
            raise ValueError("response_not_object")
        if name == "api":
            database = data.get("database")
            manifest = data.get("manifest", {}).get("status") if isinstance(data.get("manifest"), dict) else None
            observation.update(status="healthy" if database == "healthy" else "failed",
                               database=database if database in ("healthy", "unavailable") else "unknown",
                               manifestStatus=manifest if manifest in ("healthy", "warning", "critical", "expired", "missing", "unavailable") else "unknown")
        elif name == "notifier":
            observation.update(status="healthy" if data.get("running") is True else "failed",
                               manifestValid=data.get("manifestValid") if isinstance(data.get("manifestValid"), bool) else None)
        else:
            observation["status"] = "healthy" if manifest_envelope_valid(data) else "failed"
        observation["observedAtUtcMs"] = utc_ms()
    except (OSError, ValueError, urllib.error.URLError, TimeoutError):
        observation["error"] = "probe_unavailable"
    return observation


class Collector:
    def __init__(self, environ=None):
        env = os.environ if environ is None else environ
        self.proc = Path(env.get("MONITOR_HOST_PROC", "/host/proc"))
        self.sys = Path(env.get("MONITOR_HOST_SYS", "/host/sys"))
        self.interface = env.get("MONITOR_INTERFACE", "")
        if self.interface and (len(self.interface) > 64 or any(c not in "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_.:-" for c in self.interface)):
            raise ValueError("invalid_interface")
        self.project = env.get("MONITOR_COMPOSE_PROJECT", "")
        self.api_host = env.get("MONITOR_API_HOST", "")
        self.paths = [("管理数据", env.get("MONITOR_DATA_ROOT", "/host/data")),
                      ("更新包", env.get("MONITOR_PUBLIC_ROOT", "/host/public"))]
        self.probes = {"api": env.get("MONITOR_API_URL", "http://api:5081/health/ready"),
                       "notifier": env.get("MONITOR_NOTIFIER_URL", "http://notifier:8091/health"),
                       "download": env.get("MONITOR_DOWNLOAD_URL", "")}
        for name, url in self.probes.items():
            if not url or urllib.parse.urlparse(url).scheme not in (("https",) if name == "download" else ("http", "https")):
                raise ValueError(f"invalid_{name}_url")
        self.output = Path(env.get("MONITOR_OUTPUT", "/snapshot/status.json"))
        self.lock, self.stop = threading.Lock(), threading.Event()
        self.previous_cpu = self.previous_network = None
        self.history = deque(maxlen=181)
        self.state = dict(schemaVersion=1, collectorStartedAtUtcMs=utc_ms(), generatedAtUtcMs=utc_ms(),
                          resources=dict(cpu=dict(observedAtUtcMs=None, error=None, percent=None),
                                         memory=dict(observedAtUtcMs=None, error=None, totalBytes=None, usedBytes=None),
                                         network=dict(observedAtUtcMs=None, error=None, interface=None, rxBytesPerSec=None, txBytesPerSec=None), disks=[]),
                          series=[], docker=dict(observedAtUtcMs=None, error=None, services=[]), appChecks=[])

    def sample_resources(self):
        now = utc_ms()
        with self.lock:
            resources = copy.deepcopy(self.state["resources"])
        cpu, memory, network = resources["cpu"], resources["memory"], resources["network"]
        try:
            current = cpu_counters((self.proc / "stat").read_text())
            percent = cpu_percent(self.previous_cpu, current)
            self.previous_cpu = current
            cpu.update(observedAtUtcMs=now if percent is not None else None, error=None, percent=percent)
        except (OSError, ValueError, IndexError):
            cpu["error"] = "cpu_unavailable"
            self.previous_cpu = None
        try:
            total, used = memory_values((self.proc / "meminfo").read_text())
            memory.update(observedAtUtcMs=now, error=None, totalBytes=total, usedBytes=used)
        except (OSError, ValueError, KeyError, IndexError):
            memory["error"] = "memory_unavailable"
        try:
            interface = self.interface or default_interface((self.proc / "1/net/route").read_text())
            stats = self.sys / "class/net" / interface / "statistics"
            current = (interface, int((stats / "rx_bytes").read_text()), int((stats / "tx_bytes").read_text()), time.monotonic())
            rx, tx = network_rates(self.previous_network, current)
            self.previous_network = current
            network.update(observedAtUtcMs=now if rx is not None else None, error=None, interface=interface, rxBytesPerSec=rx, txBytesPerSec=tx)
        except (OSError, ValueError, IndexError):
            network["error"] = "network_unavailable"
            self.previous_network = None
        if self.history and now <= self.history[-1]["timestampUtcMs"]:
            self.history.clear()
        self.history.append(dict(timestampUtcMs=now, cpuPercent=cpu["percent"] if not cpu["error"] else None,
                                 memoryPercent=memory["usedBytes"] / memory["totalBytes"] * 100 if not memory["error"] and memory["totalBytes"] else None,
                                 rxBytesPerSec=network["rxBytesPerSec"] if not network["error"] else None,
                                 txBytesPerSec=network["txBytesPerSec"] if not network["error"] else None,
                                 networkInterface=network["interface"]))
        while self.history and self.history[0]["timestampUtcMs"] < now - 900_000:
            self.history.popleft()
        with self.lock:
            resources["disks"] = self.state["resources"]["disks"]
            self.state.update(resources=resources, series=list(self.history))

    def sample_disks(self):
        disks, devices = [], {}
        for role, path in self.paths:
            try:
                device = os.stat(path).st_dev
                if device in devices:
                    devices[device]["label"] += " / " + role
                    continue
                stats = os.statvfs(path)
                disk = dict(label=role, observedAtUtcMs=utc_ms(), error=None,
                            totalBytes=stats.f_frsize * stats.f_blocks, freeBytes=stats.f_frsize * stats.f_bavail)
                devices[device] = disk
            except OSError:
                disk = dict(label=role, observedAtUtcMs=None, error="disk_unavailable", totalBytes=None, freeBytes=None)
            disks.append(disk)
        with self.lock:
            self.state["resources"]["disks"] = disks

    def sample_docker(self):
        try:
            observation = docker_observation(self.project)
        except (OSError, ValueError, subprocess.SubprocessError, KeyError):
            with self.lock:
                observation = copy.deepcopy(self.state["docker"])
            observation["error"] = "docker_unavailable"
        with self.lock:
            self.state["docker"] = observation

    def sample_probe(self, name):
        observation = http_observation(name, self.probes[name], self.api_host if name == "api" else None)
        with self.lock:
            previous = next((item for item in self.state["appChecks"] if item["name"] == name), None)
            if observation["observedAtUtcMs"] is None and previous:
                observation["observedAtUtcMs"] = previous["observedAtUtcMs"]
            self.state["appChecks"] = [item for item in self.state["appChecks"] if item["name"] != name] + [observation]

    def publish(self):
        with self.lock:
            snapshot = copy.deepcopy(self.state)
        snapshot["generatedAtUtcMs"] = utc_ms()
        write_snapshot(self.output, snapshot)

    def loop(self, action, interval):
        while not self.stop.is_set():
            started = time.monotonic()
            try:
                action()
            except Exception as error:
                # Do not log inspect output, probe bodies or deployment secrets.
                print(f"monitor: {action.__name__}: {type(error).__name__}", flush=True)
            self.stop.wait(max(0.1, interval - (time.monotonic() - started)))

    def run(self):
        threads = [threading.Thread(target=self.loop, args=(self.sample_disks, 30), daemon=True),
                   threading.Thread(target=self.loop, args=(self.sample_docker, 15), daemon=True)]
        threads += [threading.Thread(target=self.loop, args=(lambda name=name: self.sample_probe(name), 30), daemon=True) for name in self.probes]
        for thread in threads:
            thread.start()
        def resources_and_publish():
            self.sample_resources()
            self.publish()
        self.loop(resources_and_publish, 5)


if __name__ == "__main__":
    collector = Collector()
    signal.signal(signal.SIGTERM, lambda *_: collector.stop.set())
    signal.signal(signal.SIGINT, lambda *_: collector.stop.set())
    collector.run()
