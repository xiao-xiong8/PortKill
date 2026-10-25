#!/usr/bin/env bash
set -euo pipefail

usage() {
    echo "Usage: sudo bash backup-update-server.sh <backup-directory>" >&2
}

backup="${1:-}"
if [[ -z "$backup" ]] || [[ "$(id -u)" -ne 0 ]]; then
    usage
    exit 2
fi
if ! command -v systemctl >/dev/null 2>&1 || ! command -v sha256sum >/dev/null 2>&1 || ! command -v runuser >/dev/null 2>&1; then
    echo "systemd, sha256sum and runuser are required." >&2
    exit 1
fi
backup="$(readlink -m -- "$backup")"

api_user="portkiller-update-api"
worker_user="portkiller-update-worker"
admin_group="portkiller-admin"
data_parent="/var/lib/portkiller-updates"
data_root="$data_parent/admin"
archive_root="$data_root/archive"
staging_root="$data_root/staging"
public_parent="/srv/portkiller-updates"
public_root="$public_parent/stable"
state_root="$public_parent/.stable-update-state"
config_root="/etc/portkiller-updates"
install_root="/opt/portkiller-updates"
api_executable="$install_root/api/PortKiller.UpdateAdmin.Api"

case "$backup" in
    "$data_parent"|"$data_parent"/*|"$public_parent"|"$public_parent"/*|"$config_root"|"$config_root"/*|"$install_root"|"$install_root"/*)
        echo "Backup must be outside the live update directories: $backup" >&2
        exit 1
        ;;
esac
if [[ -e "$backup" ]]; then
    echo "Backup destination already exists: $backup" >&2
    exit 1
fi
if [[ ! -x "$api_executable" ]]; then
    echo "Missing self-contained API executable: $api_executable" >&2
    exit 1
fi
for required in "$data_root" "$public_parent" "$config_root"; do
    if [[ ! -d "$required" ]]; then
        echo "Missing live directory: $required" >&2
        exit 1
    fi
done

parent="$(dirname -- "$backup")"
install -d -o root -g root -m 0700 "$parent"
temporary="$(mktemp -d "$parent/.portkiller-backup.XXXXXX")"
chown "$api_user:$admin_group" "$temporary"
chmod 2770 "$temporary"
api_was_active=0
worker_was_active=0
notifier_was_active=0
monitor_was_active=0
if systemctl is-active --quiet portkiller-update-api.service; then api_was_active=1; fi
if systemctl is-active --quiet portkiller-update-worker.service; then worker_was_active=1; fi
if systemctl is-active --quiet portkiller-update-notifier.service; then notifier_was_active=1; fi
if systemctl is-active --quiet portkiller-update-monitor.timer; then monitor_was_active=1; fi

cleanup() {
    local status=$?
    if [[ -n "${temporary:-}" && -d "$temporary" ]]; then rm -rf -- "$temporary"; fi
    if [[ "$api_was_active" -eq 1 ]]; then systemctl start portkiller-update-api.service >/dev/null 2>&1 || true; fi
    if [[ "$worker_was_active" -eq 1 ]]; then systemctl start portkiller-update-worker.service >/dev/null 2>&1 || true; fi
    if [[ "$notifier_was_active" -eq 1 ]]; then systemctl start portkiller-update-notifier.service >/dev/null 2>&1 || true; fi
    if [[ "$monitor_was_active" -eq 1 ]]; then systemctl start portkiller-update-monitor.timer >/dev/null 2>&1 || true; fi
    exit "$status"
}
trap cleanup EXIT

systemctl stop portkiller-update-monitor.timer portkiller-update-monitor.service portkiller-update-api.service portkiller-update-worker.service portkiller-update-notifier.service >/dev/null 2>&1 || true
install -d -o "$api_user" -g "$admin_group" -m 2770 "$temporary/database"
install -d -o root -g root -m 0700 "$temporary/files" "$temporary/config"

run_as_api() {
    runuser -u "$api_user" -- /bin/sh -c 'set -a; . /etc/portkiller-updates/api.env; set +a; exec "$@"' backup-command "$@"
}
run_as_api "$api_executable" db backup --output "$temporary/database"

copy_tree() {
    local source="$1" destination="$2"
    if [[ -d "$source" ]]; then
        cp -a -- "$source" "$destination/"
    else
        install -d -o root -g root -m 0700 "$destination/$(basename -- "$source")"
    fi
}
copy_tree "$archive_root" "$temporary/files"
copy_tree "$staging_root" "$temporary/files"
copy_tree "$public_root" "$temporary/files"
copy_tree "$state_root" "$temporary/files"
cp -a -- "$config_root/." "$temporary/config/"

printf 'schema=1\ncreatedUtc=%s\napiActive=%s\nworkerActive=%s\nnotifierActive=%s\n' \
    "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$api_was_active" "$worker_was_active" "$notifier_was_active" > "$temporary/metadata.txt"
(
    cd -- "$temporary"
    find database files config -type f -print0 | sort -z | xargs -0 -r sha256sum
) > "$temporary/files.sha256"
chown -R root:root "$temporary"
chmod -R go-rwx "$temporary"
mv -- "$temporary" "$backup"
temporary=""
echo "Full PortKiller update-server backup created: $backup"
