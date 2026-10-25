#!/usr/bin/env bash
set -euo pipefail

repository="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
systemd_dir="$repository/deploy/systemd"
installer="$repository/deploy/ubuntu/install-update-server.sh"

for required in \
    "$systemd_dir/portkiller-update-api.service" \
    "$systemd_dir/portkiller-update-worker.service" \
    "$systemd_dir/portkiller-update-notifier.service" \
    "$installer" \
    "$repository/deploy/examples/update-admin-api.env.example" \
    "$repository/deploy/examples/update-admin-worker.env.example"; do
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

if command -v systemd-analyze >/dev/null 2>&1; then
    systemd-analyze verify \
        "$systemd_dir/portkiller-update-api.service" \
        "$systemd_dir/portkiller-update-worker.service" \
        "$systemd_dir/portkiller-update-notifier.service"
fi

echo "PASS admin deployment templates"
