#!/usr/bin/env bash
set -euo pipefail

usage() {
    echo "Usage: sudo bash upgrade-update-server.sh <existing-ssh-user> <server-bundle> <verification-config> <backup-directory>" >&2
}

deploy_user="${1:-}"
bundle="${2:-}"
verification="${3:-}"
backup="${4:-}"
if [[ -z "$deploy_user" || -z "$bundle" || -z "$verification" || -z "$backup" ]] || [[ "$(id -u)" -ne 0 ]]; then
    usage
    exit 2
fi
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
installer="$script_dir/install-update-server.sh"
backup_script="$script_dir/backup-update-server.sh"
if [[ ! -x "$installer" && ! -f "$installer" ]] || [[ ! -f "$backup_script" ]]; then
    echo "Missing update-server installation scripts." >&2
    exit 1
fi
if [[ ! -d "$bundle" || ! -f "$verification" ]]; then
    echo "The server bundle and verification config must exist before upgrade." >&2
    exit 1
fi
if ! command -v systemctl >/dev/null 2>&1 || ! command -v curl >/dev/null 2>&1 || ! command -v runuser >/dev/null 2>&1; then
    echo "systemd, curl and runuser are required." >&2
    exit 1
fi

api_user="portkiller-update-api"
api_executable="/opt/portkiller-updates/api/PortKiller.UpdateAdmin.Api"
api_env="/etc/portkiller-updates/api.env"

echo "Creating a complete backup before replacing the server bundle..."
bash "$backup_script" "$backup"

systemctl stop portkiller-update-api.service portkiller-update-worker.service portkiller-update-notifier.service >/dev/null 2>&1 || true
systemctl stop portkiller-update-monitor.timer portkiller-update-monitor.service >/dev/null 2>&1 || true
bash "$installer" "$deploy_user" "$bundle" "$verification"
systemctl stop portkiller-update-api.service portkiller-update-worker.service >/dev/null 2>&1 || true

if [[ ! -x "$api_executable" || ! -f "$api_env" ]]; then
    echo "The upgraded API or environment file is missing." >&2
    exit 1
fi
run_as_api() {
    runuser -u "$api_user" -- /bin/sh -c 'set -a; . /etc/portkiller-updates/api.env; set +a; exec "$@"' upgrade-command "$@"
}
run_as_api "$api_executable" db migrate
systemctl start portkiller-update-api.service
systemctl start portkiller-update-worker.service
if [[ -x /opt/portkiller-updates/notifier/PortKiller.UpdateNotifier && -f /etc/portkiller-updates/verification.json ]]; then
    systemctl start portkiller-update-notifier.service
fi
systemctl start portkiller-update-monitor.timer

for attempt in $(seq 1 30); do
    if curl --fail --silent --show-error --max-time 2 http://127.0.0.1:5081/health/live >/dev/null; then
        echo "Update-server upgrade completed; backup retained at $backup"
        exit 0
    fi
    sleep 1
done
echo "API did not become healthy. Keep services stopped or restore from: $backup" >&2
exit 1
