#!/usr/bin/env bash
set -euo pipefail

repository="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
systemd_dir="$repository/deploy/systemd"
installer="$repository/deploy/ubuntu/install-update-server.sh"

for required in \
    "$systemd_dir/portkiller-update-api.service" \
    "$systemd_dir/portkiller-update-worker.service" \
    "$systemd_dir/portkiller-update-notifier.service" \
    "$systemd_dir/portkiller-update-monitor.service" \
    "$systemd_dir/portkiller-update-monitor.timer" \
    "$installer" \
    "$repository/deploy/examples/update-admin-api.env.example" \
    "$repository/deploy/examples/update-admin-worker.env.example" \
    "$repository/deploy/examples/update-monitor.env.example"; do
    test -f "$required"
done

bash -n "$installer"
grep -Fq 'User=portkiller-update-api' "$systemd_dir/portkiller-update-api.service"
grep -Fq 'InaccessiblePaths=/var/lib/portkiller-updates/admin/archive' "$systemd_dir/portkiller-update-api.service"
grep -Fq 'ReadWritePaths=/var/lib/portkiller-updates/admin' "$systemd_dir/portkiller-update-worker.service"
grep -Fq '/srv/portkiller-updates/stable' "$systemd_dir/portkiller-update-worker.service"
grep -Fq 'ReadOnlyPaths=/srv/portkiller-updates /etc/portkiller-updates' "$systemd_dir/portkiller-update-notifier.service"
grep -Fq 'NoNewPrivileges=true' "$systemd_dir/portkiller-update-api.service"
grep -Fq 'NoNewPrivileges=true' "$systemd_dir/portkiller-update-worker.service"
grep -Fq 'NoNewPrivileges=true' "$systemd_dir/portkiller-update-notifier.service"
grep -Fq 'User=portkiller-update-monitor' "$systemd_dir/portkiller-update-monitor.service"
grep -Fq 'SupplementaryGroups=portkiller-admin' "$systemd_dir/portkiller-update-monitor.service"
grep -Fq 'ReadOnlyPaths=/var/lib/portkiller-updates /srv/portkiller-updates /etc/portkiller-updates /opt/portkiller-updates' "$systemd_dir/portkiller-update-monitor.service"
grep -Fq 'StandardOutput=journal' "$systemd_dir/portkiller-update-monitor.service"
grep -Fq 'OnUnitActiveSec=5min' "$systemd_dir/portkiller-update-monitor.timer"
grep -Fq '/health/ready' "$repository/deploy/examples/update-monitor.env.example"
grep -Fq 'portkiller-update-monitor' "$installer"
grep -Fq 'python3' "$installer"
bash "$repository/scripts/checks/test-admin-nginx.sh"
bash "$repository/scripts/checks/test-admin-backup.sh"
bash "$repository/scripts/checks/test-admin-monitor.sh"

if command -v systemd-analyze >/dev/null 2>&1; then
    systemd-analyze verify \
        "$systemd_dir/portkiller-update-api.service" \
        "$systemd_dir/portkiller-update-worker.service" \
        "$systemd_dir/portkiller-update-notifier.service" \
        "$systemd_dir/portkiller-update-monitor.service" \
        "$systemd_dir/portkiller-update-monitor.timer"
fi

echo "PASS admin deployment templates"
