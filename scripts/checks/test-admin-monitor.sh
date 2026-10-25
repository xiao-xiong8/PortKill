#!/usr/bin/env bash
set -euo pipefail

repository="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
monitor="$repository/deploy/ubuntu/monitor-update-server.sh"
python_monitor="$repository/deploy/ubuntu/monitor-update-server.py"
test -f "$monitor"
test -f "$python_monitor"
bash -n "$monitor"
python3 -m py_compile "$python_monitor"

root="$(mktemp -d)"
server_pid=""
cleanup() {
    if [[ -n "$server_pid" ]]; then kill "$server_pid" >/dev/null 2>&1 || true; wait "$server_pid" >/dev/null 2>&1 || true; fi
    rm -rf -- "$root"
}
trap cleanup EXIT
mkdir -p "$root/data" "$root/public" "$root/http/health" "$root/http/stable" "$root/status"
cat > "$root/http/health/live" <<'JSON'
{"status":"ok","component":"admin-api","database":"healthy","queue":{"validation":1,"publish":0,"active":1,"attention":0},"manifest":{"status":"healthy","version":"1.0.0","sequence":1,"daysRemaining":30}}
JSON
cat > "$root/http/health/notifier" <<'JSON'
{"running":true,"manifestValid":true,"version":"1.0.0","sequence":1,"connections":0,"daysRemaining":30}
JSON
cat > "$root/http/stable/manifest.json" <<'JSON'
{"schema":2,"version":"1.0.0","sequence":1}
JSON
port="$(python3 - <<'PY'
import socket
with socket.socket() as sock:
    sock.bind(('127.0.0.1', 0))
    print(sock.getsockname()[1])
PY
)"
python3 -m http.server "$port" --bind 127.0.0.1 --directory "$root/http" >/dev/null 2>&1 &
server_pid=$!
for _ in $(seq 1 20); do curl --silent --fail "http://127.0.0.1:$port/health/live" >/dev/null && break; sleep 0.1; done

python3 "$python_monitor" --data-root "$root/data" --public-root "$root/public" \
    --api-url "http://127.0.0.1:$port/health/live" \
    --notifier-url "http://127.0.0.1:$port/health/notifier" --download-url "http://127.0.0.1:$port/stable/manifest.json" \
    --status-path "$root/status/status.json" --skip-systemd >/dev/null
python3 - "$root/status/status.json" <<'PY'
import json
import sys
data = json.load(open(sys.argv[1], encoding='utf-8'))
assert data['status'] == 'healthy', data
assert data['distribution']['status'] == 'available', data
assert data['management']['queue']['active'] == 1, data
assert data['secretsLogged'] is False, data
assert not any(word in open(sys.argv[1], encoding='utf-8').read().lower() for word in ('password', 'cookie', 'private key', 'totp'))
PY

set +e
python3 "$python_monitor" --data-root "$root/data" --public-root "$root/public" \
    --api-url "http://127.0.0.1:1/health/live" \
    --notifier-url "http://127.0.0.1:$port/health/notifier" --download-url "http://127.0.0.1:$port/stable/manifest.json" \
    --status-path "$root/status/degraded.json" --skip-systemd >/dev/null
exit_code=$?
set -e
[[ "$exit_code" -eq 1 ]]
python3 - "$root/status/degraded.json" <<'PY'
import json
import sys
data = json.load(open(sys.argv[1], encoding='utf-8'))
assert data['distribution']['status'] == 'available', data
assert data['failureIsolation'] == 'distribution_only', data
assert any(item['code'] == 'admin_api_unavailable' for item in data['alerts']), data
PY

echo "PASS admin monitor aggregation and failure isolation"
