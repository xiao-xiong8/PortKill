#!/usr/bin/env bash
set -euo pipefail

deploy_user="${1:-}"
if [[ -z "$deploy_user" ]] || ! id "$deploy_user" >/dev/null 2>&1; then
    echo "Usage: sudo bash install-update-server.sh <existing-ssh-user>" >&2
    exit 2
fi
if [[ "$(id -u)" -ne 0 ]]; then
    echo "Run with sudo: sudo bash install-update-server.sh <existing-ssh-user>" >&2
    exit 2
fi

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
nginx_config="$script_dir/../nginx/portkiller-updates.conf"
if [[ ! -f "$nginx_config" ]]; then
    echo "Missing Nginx config: $nginx_config" >&2
    exit 1
fi

export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get install -y nginx curl
install -d -o "$deploy_user" -g www-data -m 2755 /srv/portkiller-updates/stable
install -o root -g root -m 0644 "$nginx_config" /etc/nginx/conf.d/portkiller-updates.conf
nginx -t
systemctl enable --now nginx
systemctl reload nginx

if command -v ufw >/dev/null 2>&1 && ufw status | grep -q 'Status: active'; then
    ufw allow 8080/tcp
fi

echo "Ready: http://$(hostname -I | awk '{print $1}'):8080/stable/manifest.json"
echo "Allow inbound TCP 8080 in the cloud security group if one is configured."
