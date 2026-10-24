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
install -d -o "$deploy_user" -g www-data -m 2750 /srv/portkiller-updates/.stable-update-state
install -d -o "$deploy_user" -g www-data -m 2700 /srv/portkiller-updates/.staging
if ! id portkiller-notifier >/dev/null 2>&1; then
    useradd --system --no-create-home --shell /usr/sbin/nologin portkiller-notifier
fi
install -d -o root -g www-data -m 0755 /opt/portkiller-updates /etc/portkiller-updates
install -o root -g root -m 0644 "$script_dir/../systemd/portkiller-update-notifier.service" /etc/systemd/system/portkiller-update-notifier.service
systemctl daemon-reload
bundle="${2:-}"
verification="${3:-}"
if [[ -n "$bundle" ]]; then
    test -f "$bundle/tool/PortKiller.UpdateTool"
    test -f "$bundle/notifier/PortKiller.UpdateNotifier"
    systemctl stop portkiller-update-notifier
    install -d -o root -g root -m 0755 /opt/portkiller-updates/tool /opt/portkiller-updates/notifier
    cp -a -- "$bundle/tool/." /opt/portkiller-updates/tool/
    cp -a -- "$bundle/notifier/." /opt/portkiller-updates/notifier/
    chown -R root:root /opt/portkiller-updates/tool /opt/portkiller-updates/notifier
    chmod 0755 /opt/portkiller-updates/tool/PortKiller.UpdateTool /opt/portkiller-updates/notifier/PortKiller.UpdateNotifier
fi
if [[ -n "$verification" ]]; then
    install -o root -g www-data -m 0644 "$verification" /etc/portkiller-updates/verification.json
fi
if [[ -x /opt/portkiller-updates/notifier/PortKiller.UpdateNotifier && -f /etc/portkiller-updates/verification.json ]]; then
    systemctl enable --now portkiller-update-notifier
else
    echo "Install self-contained notifier/tool in /opt/portkiller-updates and verification.json in /etc/portkiller-updates, then enable portkiller-update-notifier."
fi
install -o root -g root -m 0644 "$nginx_config" /etc/nginx/conf.d/portkiller-updates.conf
nginx -t
systemctl enable --now nginx
systemctl reload nginx

if command -v ufw >/dev/null 2>&1 && ufw status | grep -q 'Status: active'; then
    ufw allow 8080/tcp
fi

echo "Ready: http://$(hostname -I | awk '{print $1}'):8080/stable/manifest.json"
echo "Allow inbound TCP 8080 in the cloud security group if one is configured."
