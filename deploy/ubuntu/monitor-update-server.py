#!/usr/bin/env python3
"""One-shot, dependency-free health aggregation for the update server.

The monitor deliberately reads only bounded health responses and aggregate
status. It never sends cookies, reads authentication keys, or logs response
bodies, so journald can be used as the free alert sink.
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


def env(name: str, default: str) -> str:
    return os.environ.get(name, default)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="PortKiller update server health monitor")
    parser.add_argument("--data-root", default=env("PORTKILL_MONITOR_DATA_ROOT", "/var/lib/portkiller-updates/admin"))
    parser.add_argument("--public-root", default=env("PORTKILL_MONITOR_PUBLIC_ROOT", "/srv/portkiller-updates/stable"))
    parser.add_argument("--api-url", default=env("PORTKILL_MONITOR_API_URL", "http://127.0.0.1:5081/health/live"))
    parser.add_argument("--notifier-url", default=env("PORTKILL_MONITOR_NOTIFIER_URL", "http://127.0.0.1:8091/health"))
    parser.add_argument("--download-url", default=env("PORTKILL_MONITOR_DOWNLOAD_URL", "http://127.0.0.1:8080/stable/manifest.json"))
    parser.add_argument("--status-path", default=env("PORTKILL_MONITOR_STATUS_PATH", "/run/portkiller-update-monitor/status.json"))
    parser.add_argument("--minimum-free-bytes", type=int, default=int(env("PORTKILL_MONITOR_MIN_FREE_BYTES", str(1 * 1024 * 1024 * 1024))))
    parser.add_argument("--minimum-free-percent", type=float, default=float(env("PORTKILL_MONITOR_MIN_FREE_PERCENT", "10")))
    parser.add_argument("--queue-warning", type=int, default=int(env("PORTKILL_MONITOR_QUEUE_WARNING", "10")))
    parser.add_argument("--queue-critical", type=int, default=int(env("PORTKILL_MONITOR_QUEUE_CRITICAL", "50")))
    parser.add_argument("--manifest-warning-days", type=float, default=float(env("PORTKILL_MONITOR_MANIFEST_WARNING_DAYS", "14")))
    parser.add_argument("--manifest-critical-days", type=float, default=float(env("PORTKILL_MONITOR_MANIFEST_CRITICAL_DAYS", "7")))
    parser.add_argument("--skip-systemd", action="store_true", default=env("PORTKILL_MONITOR_SKIP_SYSTEMD", "0") == "1")
    parser.add_argument("--timeout-seconds", type=float, default=float(env("PORTKILL_MONITOR_TIMEOUT_SECONDS", "3")))
    return parser


def utc_now() -> datetime:
    return datetime.now(timezone.utc)


def utc_text(value: datetime) -> str:
    return value.isoformat(timespec="seconds").replace("+00:00", "Z")


def http_json(url: str, timeout: float) -> tuple[dict[str, Any] | None, str | None]:
    request = urllib.request.Request(url, headers={"Accept": "application/json", "User-Agent": "PortKillUpdateMonitor/1"})
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            if response.status < 200 or response.status >= 300:
                return None, f"http_{response.status}"
            body = response.read(65537)
            if len(body) > 65536:
                return None, "response_too_large"
            value = json.loads(body.decode("utf-8"))
            if not isinstance(value, dict):
                return None, "response_not_object"
            return value, None
    except (OSError, urllib.error.URLError, urllib.error.HTTPError, TimeoutError, UnicodeError, json.JSONDecodeError) as error:
        return None, type(error).__name__.lower()


def probe_http(url: str, timeout: float, component: str) -> dict[str, Any]:
    value, error = http_json(url, timeout)
    if error:
        return {"status": "down", "component": component, "error": error}
    reported = {
        key: value[key] for key in ("status", "component", "running", "manifestValid", "version", "sequence", "daysRemaining", "connections", "failure", "database")
        if key in value and isinstance(value[key], (str, int, float, bool, type(None)))
    }
    for key in ("queue", "storage", "manifest"):
        nested = value.get(key)
        if isinstance(nested, dict):
            reported[key] = {field: nested[field] for field in nested
                             if field in {"validation", "publish", "active", "attention", "totalBytes", "usedBytes", "freeBytes", "freePercent", "status", "version", "sequence", "daysRemaining"}
                             and isinstance(nested[field], (str, int, float, bool, type(None)))}
    return {"status": "up", "component": component, "reported": reported}


def copy_health_object(value: dict[str, Any], name: str) -> dict[str, Any] | None:
    nested = value.get(name)
    if not isinstance(nested, dict):
        return None
    allowed = {"validation", "publish", "active", "attention", "totalBytes", "usedBytes", "freeBytes", "freePercent",
               "status", "version", "sequence", "daysRemaining"}
    return {key: nested[key] for key in nested if key in allowed
            and isinstance(nested[key], (str, int, float, bool, type(None)))}


def systemd_status(unit: str, skip: bool, timeout: float) -> dict[str, Any]:
    if skip:
        return {"status": "skipped", "unit": unit}
    try:
        result = subprocess.run(["systemctl", "is-active", "--quiet", unit], check=False, timeout=timeout)
    except (OSError, subprocess.TimeoutExpired) as error:
        return {"status": "unknown", "unit": unit, "error": type(error).__name__.lower()}
    return {"status": "up" if result.returncode == 0 else "down", "unit": unit}


def disk_status(path: str) -> tuple[dict[str, Any] | None, str | None]:
    try:
        usage = shutil.disk_usage(path)
        free_percent = usage.free * 100.0 / usage.total if usage.total else 0.0
        return {"totalBytes": usage.total, "usedBytes": usage.used, "freeBytes": usage.free,
                "freePercent": round(free_percent, 2)}, None
    except OSError as error:
        return None, type(error).__name__.lower()


def severity_rank(value: str) -> int:
    return {"healthy": 0, "warning": 1, "critical": 2}.get(value, 2)


def add_alert(alerts: list[dict[str, str]], code: str, severity: str, component: str, message: str) -> None:
    alerts.append({"code": code, "severity": severity, "component": component, "message": message})


def manifest_from_notifier(notifier: dict[str, Any]) -> dict[str, Any]:
    reported = notifier.get("reported", {})
    if notifier.get("status") != "up":
        return {"status": "unavailable"}
    valid = reported.get("manifestValid") is True
    days = reported.get("daysRemaining")
    result: dict[str, Any] = {"status": "healthy" if valid else "invalid"}
    for key in ("version", "sequence", "daysRemaining"):
        if key in reported:
            result[key] = reported[key]
    if days is not None and isinstance(days, (int, float)):
        result["status"] = "healthy" if valid else "invalid"
    return result


def write_status(path: str, status: dict[str, Any]) -> None:
    target = Path(path)
    target.parent.mkdir(parents=True, exist_ok=True)
    temporary = target.with_name(f".{target.name}.{os.getpid()}.tmp")
    temporary.write_text(json.dumps(status, ensure_ascii=True, separators=(",", ":")) + "\n", encoding="utf-8")
    os.replace(temporary, target)


def main() -> int:
    args = build_parser().parse_args()
    if args.minimum_free_bytes < 0 or args.minimum_free_percent < 0 or args.queue_warning < 0 or args.queue_critical < args.queue_warning:
        raise SystemExit("monitor thresholds are invalid")
    now = utc_now()
    alerts: list[dict[str, str]] = []

    api = probe_http(args.api_url, args.timeout_seconds, "admin-api")
    notifier = probe_http(args.notifier_url, args.timeout_seconds, "notifier")
    static_manifest, static_error = http_json(args.download_url, args.timeout_seconds)
    static = {"status": "up" if static_error is None else "down"}
    if static_error:
        static["error"] = static_error

    api_reported = api.get("reported", {}) if isinstance(api.get("reported"), dict) else {}
    database = {"status": "up" if api_reported.get("database") == "healthy" else "unavailable"}
    counts = copy_health_object(api_reported, "queue")

    storage, storage_error = disk_status(args.data_root)
    if storage_error:
        storage = {"status": "unavailable", "error": storage_error}
        add_alert(alerts, "storage_unavailable", "critical", "storage", "data storage capacity could not be read")
    else:
        storage["status"] = "healthy"
        if storage["freeBytes"] < args.minimum_free_bytes or storage["freePercent"] < args.minimum_free_percent:
            severity = "critical" if storage["freeBytes"] < args.minimum_free_bytes / 2 or storage["freePercent"] < args.minimum_free_percent / 2 else "warning"
            add_alert(alerts, "storage_capacity_low", severity, "storage", "free disk capacity is below the configured threshold")

    queue = counts or {"validation": 0, "publish": 0, "active": 0, "attention": 0}
    queue["status"] = "healthy" if counts is not None else "unavailable"
    if api["status"] == "up" and database["status"] != "up":
        add_alert(alerts, "database_unavailable", "critical", "database", "SQLite readiness is unavailable")
    if api["status"] == "up" and counts is None:
        add_alert(alerts, "queue_unavailable", "critical", "queue", "queue counters are unavailable")
    elif counts is not None and counts["active"] >= args.queue_critical:
        add_alert(alerts, "queue_capacity_critical", "critical", "queue", "pending task count is above the critical threshold")
    elif counts is not None and counts["active"] >= args.queue_warning:
        add_alert(alerts, "queue_capacity_high", "warning", "queue", "pending task count is above the warning threshold")
    if counts is not None and counts.get("attention", 0) > 0:
        add_alert(alerts, "queue_attention", "warning", "queue", "failed or unverified tasks require attention")

    manifest = manifest_from_notifier(notifier)
    days = manifest.get("daysRemaining")
    if manifest.get("status") == "unavailable":
        add_alert(alerts, "notifier_unavailable", "critical", "distribution", "read-only release notifier is unavailable")
    elif manifest.get("status") == "invalid":
        add_alert(alerts, "manifest_invalid", "critical", "distribution", "the notifier cannot verify the active manifest")
    elif isinstance(days, (int, float)):
        if days <= args.manifest_critical_days:
            add_alert(alerts, "manifest_expiry_critical", "critical", "distribution", "active manifest expires soon or is expired")
        elif days <= args.manifest_warning_days:
            add_alert(alerts, "manifest_expiry_warning", "warning", "distribution", "active manifest is approaching expiry")

    if api["status"] != "up":
        add_alert(alerts, "admin_api_unavailable", "critical", "management", "management API is unavailable")
    worker = systemd_status("portkiller-update-worker.service", args.skip_systemd, args.timeout_seconds)
    if worker["status"] == "down":
        add_alert(alerts, "publisher_worker_unavailable", "critical", "management", "publisher worker is not active")
    elif worker["status"] == "unknown":
        add_alert(alerts, "publisher_worker_unknown", "warning", "management", "publisher worker state could not be read")
    if static["status"] != "up":
        add_alert(alerts, "static_download_unavailable", "critical", "distribution", "static manifest download is unavailable")

    highest = max((severity_rank(alert["severity"]) for alert in alerts), default=0)
    overall = "critical" if highest == 2 else "warning" if highest == 1 else "healthy"
    distribution_available = static["status"] == "up" and notifier["status"] == "up"
    status: dict[str, Any] = {
        "schema": 1,
        "observedUtc": utc_text(now),
        "status": overall,
        "management": {"api": api, "worker": worker, "database": database, "queue": queue},
        "distribution": {"status": "available" if distribution_available else "degraded", "staticManifest": static, "notifier": notifier, "manifest": manifest},
        "storage": storage,
        "alerts": alerts,
        "failureIsolation": "distribution_only" if distribution_available and (api["status"] != "up" or worker["status"] != "up") else "normal",
        "secretsLogged": False,
    }
    write_status(args.status_path, status)
    print(json.dumps(status, ensure_ascii=True, separators=(",", ":")))
    if alerts:
        codes = ",".join(alert["code"] for alert in alerts)
        print(f"portkiller_update_monitor status={overall} alert_codes={codes} secrets_logged=false", file=sys.stderr)
    return 1 if overall == "critical" else 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except KeyboardInterrupt:
        raise SystemExit(130)
