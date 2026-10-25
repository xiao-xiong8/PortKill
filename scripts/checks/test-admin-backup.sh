#!/usr/bin/env bash
set -euo pipefail

repository="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
deploy="$repository/deploy/ubuntu"
for script in backup-update-server.sh restore-update-server.sh upgrade-update-server.sh; do
    test -f "$deploy/$script"
    bash -n "$deploy/$script"
done
grep -Fq 'db backup --output' "$deploy/backup-update-server.sh"
grep -Fq 'files.sha256' "$deploy/backup-update-server.sh"
grep -Fq 'archive' "$deploy/backup-update-server.sh"
grep -Fq 'stable' "$deploy/backup-update-server.sh"
grep -Fq 'db restore --input' "$deploy/restore-update-server.sh"
grep -Fq 'sha256sum -c files.sha256' "$deploy/restore-update-server.sh"
grep -Fq 'recover --root' "$deploy/restore-update-server.sh"
grep -Fq 'inspect --root' "$deploy/restore-update-server.sh"
grep -Fq 'backup-update-server.sh' "$deploy/upgrade-update-server.sh"
grep -Fq 'db migrate' "$deploy/upgrade-update-server.sh"
grep -Fq '/health/live' "$deploy/upgrade-update-server.sh"
grep -Fq 'portkiller-update-monitor.timer' "$deploy/upgrade-update-server.sh"
grep -Fq 'portkiller-update-monitor.timer' "$deploy/backup-update-server.sh"
grep -Fq 'portkiller-update-monitor.timer' "$deploy/restore-update-server.sh"

echo "PASS admin backup and upgrade scripts"
