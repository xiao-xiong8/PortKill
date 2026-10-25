#!/usr/bin/env bash
set -euo pipefail

usage() {
    echo "Usage: sudo bash install-update-server.sh <existing-ssh-user> [server-bundle] [verification-config]" >&2
}

deploy_user="${1:-}"
bundle="${2:-}"
verification="${3:-}"
if [[ -z "$deploy_user" ]] || ! id "$deploy_user" >/dev/null 2>&1; then
    usage
    exit 2
fi
if [[ "$(id -u)" -ne 0 ]]; then
    echo "Run with sudo: sudo bash install-update-server.sh <existing-ssh-user> [server-bundle] [verification-config]" >&2
    exit 2
fi
if ! command -v systemctl >/dev/null 2>&1; then
    echo "systemd is required for this installation." >&2
    exit 1
fi

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repository_deploy_dir="$(cd -- "$script_dir/.." && pwd)"
nginx_config="$repository_deploy_dir/nginx/portkiller-updates.conf"
api_unit="$repository_deploy_dir/systemd/portkiller-update-api.service"
worker_unit="$repository_deploy_dir/systemd/portkiller-update-worker.service"
notifier_unit="$repository_deploy_dir/systemd/portkiller-update-notifier.service"
for required in "$nginx_config" "$api_unit" "$worker_unit" "$notifier_unit"; do
    if [[ ! -f "$required" ]]; then
        echo "Missing deployment file: $required" >&2
        exit 1
    fi
done

admin_group="portkiller-admin"
publish_group="portkiller-publish"
api_user="portkiller-update-api"
worker_user="portkiller-update-worker"
notifier_user="portkiller-update-notifier"
data_parent="/var/lib/portkiller-updates"
data_root="$data_parent/admin"
staging_root="$data_root/staging"
archive_root="$data_root/archive"
auth_root="$data_root/auth-keys"
public_parent="/srv/portkiller-updates"
public_root="$public_parent/stable"
state_root="$public_parent/.stable-update-state"
legacy_staging_root="$public_parent/.staging"
install_root="/opt/portkiller-updates"
config_root="/etc/portkiller-updates"

ensure_group() {
    local group="$1"
    if ! getent group "$group" >/dev/null 2>&1; then
        groupadd --system "$group"
    fi
}

ensure_service_user() {
    local user="$1" home="$2"
    if ! id "$user" >/dev/null 2>&1; then
        useradd --system --user-group --no-create-home --home-dir "$home" --shell /usr/sbin/nologin "$user"
    else
        usermod --shell /usr/sbin/nologin "$user"
    fi
}

ensure_group "$admin_group"
ensure_group "$publish_group"
ensure_service_user "$api_user" "$data_root"
ensure_service_user "$worker_user" "$data_root"
ensure_service_user "$notifier_user" "$install_root/notifier"
usermod -a -G "$admin_group" "$api_user"
usermod -a -G "$admin_group" "$worker_user"
usermod -a -G "$publish_group" "$worker_user"
# Keep the existing SSH publishing entry point working without granting it database access.
usermod -a -G "$publish_group" "$deploy_user"

export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get install -y nginx curl

for unit in portkiller-update-api.service portkiller-update-worker.service portkiller-update-notifier.service; do
    systemctl stop "$unit" >/dev/null 2>&1 || true
done

install -d -o root -g root -m 0755 "$install_root" "$config_root" "$data_parent" "$public_parent"
# API and Worker share only the database and staging group. The archive is Worker-owned.
install -d -o "$api_user" -g "$admin_group" -m 2770 "$data_root" "$staging_root"
install -d -o "$worker_user" -g "$worker_user" -m 0700 "$archive_root"
install -d -o "$api_user" -g "$api_user" -m 0700 "$auth_root"
# The public package directory is intentionally readable by Nginx and old clients.
install -d -o "$worker_user" -g "$publish_group" -m 2775 "$public_root"
install -d -o "$worker_user" -g "$publish_group" -m 2770 "$state_root"
# The existing SSH publisher uploads into this private sibling before invoking UpdateTool.
install -d -o "$deploy_user" -g "$publish_group" -m 2770 "$legacy_staging_root"

# Repair ownership and modes on an in-place upgrade without touching package contents.
chown "$api_user:$admin_group" "$data_root" "$staging_root"
find "$staging_root" -type d -exec chown "$api_user:$admin_group" {} + -exec chmod 2770 {} +
find "$staging_root" -type f -exec chown "$api_user:$admin_group" {} + -exec chmod 0660 {} +
chown -R "$worker_user:$worker_user" "$archive_root"
find "$archive_root" -type d -exec chmod 0700 {} +
find "$archive_root" -type f -exec chmod 0600 {} +
chown "$worker_user:$publish_group" "$public_root" "$state_root"
chmod 2775 "$public_root"
chmod 2770 "$state_root"
chown "$deploy_user:$publish_group" "$legacy_staging_root"
chmod 2770 "$legacy_staging_root"
for database_file in admin.sqlite admin.sqlite-wal admin.sqlite-shm runtime.lock; do
    if [[ -e "$data_root/$database_file" ]]; then
        chown "$api_user:$admin_group" "$data_root/$database_file"
        chmod 0660 "$data_root/$database_file"
    fi
done
chown -R "$api_user:$api_user" "$auth_root"
find "$auth_root" -type d -exec chmod 0700 {} +
find "$auth_root" -type f -exec chmod 0600 {} +

install -o root -g root -m 0644 "$api_unit" /etc/systemd/system/portkiller-update-api.service
install -o root -g root -m 0644 "$worker_unit" /etc/systemd/system/portkiller-update-worker.service
install -o root -g root -m 0644 "$notifier_unit" /etc/systemd/system/portkiller-update-notifier.service

if [[ -n "$bundle" ]]; then
    if [[ ! -d "$bundle" ]]; then
        echo "Server bundle is not a directory: $bundle" >&2
        exit 1
    fi
    bundle="$(readlink -f -- "$bundle")"
    for component in api worker notifier tool; do
        case "$component" in
            api) entry="PortKiller.UpdateAdmin.Api" ;;
            worker) entry="PortKiller.UpdatePublisher.Worker" ;;
            notifier) entry="PortKiller.UpdateNotifier" ;;
            tool) entry="PortKiller.UpdateTool" ;;
        esac
        if [[ ! -x "$bundle/$component/$entry" ]]; then
            echo "Bundle is missing executable: $component/$entry" >&2
            exit 1
        fi
    done
    if [[ ! -f "$bundle/web/index.html" ]]; then
        echo "Bundle is missing web/index.html" >&2
        exit 1
    fi

    copy_component() {
        local component="$1"
        local destination="$install_root/$component"
        case "$destination" in
            "$install_root/api"|"$install_root/worker"|"$install_root/notifier"|"$install_root/tool") ;;
            *) echo "Refusing unexpected install path: $destination" >&2; exit 1 ;;
        esac
        install -d -o root -g root -m 0755 "$destination"
        find "$destination" -mindepth 1 -maxdepth 1 -exec rm -rf -- {} +
        cp -a -- "$bundle/$component/." "$destination/"
        chown -R root:root "$destination"
        find "$destination" -type d -exec chmod 0755 {} +
        find "$destination" -type f -exec chmod 0644 {} +
        case "$component" in
            api) chmod 0755 "$destination/PortKiller.UpdateAdmin.Api" ;;
            worker) chmod 0755 "$destination/PortKiller.UpdatePublisher.Worker" ;;
            notifier) chmod 0755 "$destination/PortKiller.UpdateNotifier" ;;
            tool) chmod 0755 "$destination/PortKiller.UpdateTool" ;;
        esac
    }
    copy_component api
    copy_component worker
    copy_component notifier
    copy_component tool
    install -d -o root -g root -m 0755 "$install_root/web"
    find "$install_root/web" -mindepth 1 -maxdepth 1 -exec rm -rf -- {} +
    cp -a -- "$bundle/web/." "$install_root/web/"
    chown -R root:root "$install_root/web"
    find "$install_root/web" -type d -exec chmod 0755 {} +
    find "$install_root/web" -type f -exec chmod 0644 {} +
fi

if [[ -n "$verification" ]]; then
    if [[ ! -f "$verification" ]]; then
        echo "Verification config is not a file: $verification" >&2
        exit 1
    fi
    install -o root -g root -m 0644 "$verification" "$config_root/verification.json"
fi

write_env() {
    local path="$1" enabled="$2"
    local temporary
    temporary="$(mktemp "$config_root/.env.XXXXXX")"
    cat > "$temporary" <<EOF
DOTNET_ENVIRONMENT=Production
ASPNETCORE_ENVIRONMENT=Production
${enabled}
Administration__DataRoot=$data_root
UpdateTool__ExecutablePath=$install_root/tool/PortKiller.UpdateTool
UpdateTool__VerificationConfigPath=$config_root/verification.json
UpdateTool__ReleaseRoot=$public_root
UpdateTool__TimeoutSeconds=45
UpdateTool__PublicCheckTimeoutSeconds=300
UpdateTool__MaximumOutputBytes=65536
EOF
    if [[ "$path" == "$config_root/api.env" ]]; then
        echo 'ASPNETCORE_URLS=http://127.0.0.1:5081' >> "$temporary"
        echo 'urls=http://127.0.0.1:5081' >> "$temporary"
    fi
    install -o root -g "$admin_group" -m 0640 "$temporary" "$path"
    rm -f -- "$temporary"
}
write_env "$config_root/api.env" 'UpdateTool__Enabled=false'
write_env "$config_root/worker.env" 'UpdateTool__Enabled=true'

install -o root -g root -m 0644 "$nginx_config" /etc/nginx/conf.d/portkiller-updates.conf
systemctl daemon-reload
systemctl enable portkiller-update-api.service portkiller-update-worker.service >/dev/null
if [[ -x "$install_root/notifier/PortKiller.UpdateNotifier" && -f "$config_root/verification.json" ]]; then
    systemctl enable portkiller-update-notifier.service >/dev/null
fi

nginx -t
systemctl enable nginx >/dev/null
systemctl start nginx
systemctl reload nginx

if command -v ufw >/dev/null 2>&1 && ufw status | grep -q 'Status: active'; then
    ufw allow 8080/tcp
fi

if [[ -x "$install_root/notifier/PortKiller.UpdateNotifier" && -f "$config_root/verification.json" ]]; then
    systemctl start portkiller-update-notifier.service || echo "Notifier did not start; inspect journalctl -u portkiller-update-notifier.service."
else
    echo "Notifier remains stopped until the self-contained binary and verification.json are installed."
fi

if [[ -x "$install_root/api/PortKiller.UpdateAdmin.Api" && -x "$install_root/worker/PortKiller.UpdatePublisher.Worker" && -f "$data_root/admin.sqlite" ]]; then
    systemctl start portkiller-update-api.service || echo "API did not start; run database migration and inspect journalctl -u portkiller-update-api.service."
    systemctl start portkiller-update-worker.service || echo "Worker did not start; inspect journalctl -u portkiller-update-worker.service."
else
    echo "API and Worker are installed but stopped until the bundle is complete and the database is migrated."
fi

echo "Installed service accounts: $api_user, $worker_user, $notifier_user"
echo "Data root: $data_root (SQLite and Data Protection keys)"
echo "Publish root: $public_root (Worker and legacy SSH publisher write; API and Notifier read only)"
echo "Migration command (stop API and Worker first):"
echo "  sudo -u $api_user /bin/sh -c 'set -a; . $config_root/api.env; set +a; exec $install_root/api/PortKiller.UpdateAdmin.Api db migrate'"
echo "Initial administrator command (run after migration, with password entered interactively):"
echo "  sudo -u $api_user /bin/sh -c 'set -a; . $config_root/api.env; set +a; exec $install_root/api/PortKiller.UpdateAdmin.Api admin init --user <admin>'"
echo "Ready for the existing download path: http://$(hostname -I | awk '{print \$1}'):8080/stable/manifest.json"
echo "Allow inbound TCP 8080 in the cloud security group if one is configured."
