#!/usr/bin/env bash
set -euo pipefail

usage() {
    echo "Usage: sudo bash restore-update-server.sh <backup-directory>" >&2
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
backup="$(readlink -f -- "$backup")"

api_user="portkiller-update-api"
worker_user="portkiller-update-worker"
admin_group="portkiller-admin"
publish_group="portkiller-publish"
data_parent="/var/lib/portkiller-updates"
data_root="$data_parent/admin"
archive_root="$data_root/archive"
staging_root="$data_root/staging"
auth_root="$data_root/auth-keys"
public_parent="/srv/portkiller-updates"
public_root="$public_parent/stable"
state_root="$public_parent/.stable-update-state"
config_root="/etc/portkiller-updates"
install_root="/opt/portkiller-updates"
api_executable="$install_root/api/PortKiller.UpdateAdmin.Api"
tool_executable="$install_root/tool/PortKiller.UpdateTool"

if [[ ! -d "$backup" || ! -f "$backup/files.sha256" || ! -d "$backup/database" || ! -f "$backup/database/backup.json" || ! -d "$backup/config" ]]; then
    echo "Backup directory is incomplete: $backup" >&2
    exit 1
fi
case "$backup" in
    "$data_parent"|"$data_parent"/*|"$public_parent"|"$public_parent"/*|"$config_root"|"$config_root"/*|"$install_root"|"$install_root"/*)
        echo "Backup must be outside the live update directories: $backup" >&2
        exit 1
        ;;
esac
(cd -- "$backup" && sha256sum -c files.sha256)
for required in archive staging stable .stable-update-state; do
    if [[ ! -d "$backup/files/$required" ]]; then
        echo "Backup is missing files/$required" >&2
        exit 1
    fi
done

api_was_active=0
worker_was_active=0
notifier_was_active=0
if systemctl is-active --quiet portkiller-update-api.service; then api_was_active=1; fi
if systemctl is-active --quiet portkiller-update-worker.service; then worker_was_active=1; fi
if systemctl is-active --quiet portkiller-update-notifier.service; then notifier_was_active=1; fi
staged=""
restore_id="$(date -u +%Y%m%dT%H%M%SZ)-$$"

cleanup() {
    local status=$?
    if [[ -n "$staged" && -d "$staged" ]]; then rm -rf -- "$staged"; fi
    if [[ "$api_was_active" -eq 1 ]]; then systemctl start portkiller-update-api.service >/dev/null 2>&1 || true; fi
    if [[ "$worker_was_active" -eq 1 ]]; then systemctl start portkiller-update-worker.service >/dev/null 2>&1 || true; fi
    if [[ "$notifier_was_active" -eq 1 ]]; then systemctl start portkiller-update-notifier.service >/dev/null 2>&1 || true; fi
    exit "$status"
}
trap cleanup EXIT

systemctl stop portkiller-update-api.service portkiller-update-worker.service portkiller-update-notifier.service >/dev/null 2>&1 || true
install -d -o root -g root -m 0755 "$data_parent" "$public_parent"
staged="$(mktemp -d "$data_parent/.portkiller-restore.XXXXXX")"
chmod 0700 "$staged"
cp -a -- "$backup/files/archive" "$staged/archive"
cp -a -- "$backup/files/staging" "$staged/staging"
cp -a -- "$backup/files/stable" "$staged/stable"
cp -a -- "$backup/files/.stable-update-state" "$staged/.stable-update-state"

# The backup is root-owned and private, so perform the offline restore as root and
# repair the service-account ownership before starting either service.
set -a
. "$config_root/api.env"
set +a
"$api_executable" db restore --input "$backup/database"

replace_tree() {
    local staged_path="$1" live_path="$2" parent="$3"
    local previous="$parent/.pre-restore-$restore_id-$(basename -- "$live_path")"
    if [[ -e "$live_path" || -L "$live_path" ]]; then mv -- "$live_path" "$previous"; fi
    mv -- "$staged_path" "$live_path"
    echo "$live_path -> $previous" >> "$data_parent/restore-previous-$restore_id.txt"
}
replace_tree "$staged/archive" "$archive_root" "$data_root"
replace_tree "$staged/staging" "$staging_root" "$data_root"
replace_tree "$staged/stable" "$public_root" "$public_parent"
replace_tree "$staged/.stable-update-state" "$state_root" "$public_parent"

if [[ -d "$backup/config" ]]; then
    previous_config="/etc/portkiller-updates.pre-restore-$restore_id"
    if [[ -d "$config_root" ]]; then mv -- "$config_root" "$previous_config"; fi
    install -d -o root -g root -m 0755 "$config_root"
    cp -a -- "$backup/config/." "$config_root/"
    chown -R root:root "$config_root"
    chmod 0755 "$config_root"
    [[ ! -f "$config_root/api.env" ]] || { chown root:"$admin_group" "$config_root/api.env"; chmod 0640 "$config_root/api.env"; }
    [[ ! -f "$config_root/worker.env" ]] || { chown root:"$admin_group" "$config_root/worker.env"; chmod 0640 "$config_root/worker.env"; }
    [[ ! -f "$config_root/verification.json" ]] || chmod 0644 "$config_root/verification.json"
fi

# Restore the same DAC boundary created by install-update-server.sh.
chown -R "$worker_user:$worker_user" "$archive_root"
find "$archive_root" -type d -exec chmod 0700 {} +
find "$archive_root" -type f -exec chmod 0600 {} +
chown -R "$api_user:$admin_group" "$staging_root"
find "$staging_root" -type d -exec chmod 2770 {} +
find "$staging_root" -type f -exec chmod 0660 {} +
chown -R "$worker_user:$publish_group" "$public_root" "$state_root"
find "$public_root" -type d -exec chmod 0755 {} +
find "$public_root" -type f -exec chmod 0644 {} +
find "$state_root" -type d -exec chmod 2770 {} +
find "$state_root" -type f -exec chmod 0660 {} +
chmod 2775 "$public_root"
chmod 2770 "$state_root"
chown "$api_user:$admin_group" "$data_root"
chmod 2770 "$data_root"
for database_file in admin.sqlite admin.sqlite-wal admin.sqlite-shm runtime.lock; do
    if [[ -e "$data_root/$database_file" ]]; then
        chown "$api_user:$admin_group" "$data_root/$database_file"
        chmod 0660 "$data_root/$database_file"
    fi
done
chown -R "$api_user:$api_user" "$auth_root"
find "$auth_root" -type d -exec chmod 0700 {} +
find "$auth_root" -type f -exec chmod 0600 {} +

if [[ -x "$tool_executable" && -f "$config_root/verification.json" ]]; then
    runuser -u "$worker_user" -- "$tool_executable" recover --root "$public_root" --config "$config_root/verification.json" --json-errors > "$data_parent/restore-recover-$restore_id.json"
    runuser -u "$worker_user" -- "$tool_executable" inspect --root "$public_root" --config "$config_root/verification.json" --json-errors > "$data_parent/restore-inspect-$restore_id.json"
fi
rm -rf -- "$staged"
staged=""
echo "Full PortKiller update-server restore completed: $backup"
echo "Previous live trees were kept under .pre-restore-$restore_id where applicable."
