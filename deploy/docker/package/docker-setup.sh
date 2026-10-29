#!/usr/bin/env bash
set -euo pipefail

kit_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
cd "$kit_dir"

if [[ "$(id -u)" -ne 0 ]]; then
    echo "请使用 root 运行：sudo bash docker-setup.sh" >&2
    exit 2
fi
[[ "$(uname -s)" == Linux && "$(uname -m)" == x86_64 ]] || { echo "本包仅支持 Linux x86-64 服务器。" >&2; exit 2; }
if ! docker compose version >/dev/null 2>&1; then
    echo "需要已启动的 Docker Engine 和 Docker Compose v2。" >&2
    exit 2
fi
for required in docker-compose.yml docker/Dockerfile docker/Caddyfile docker/Caddy.Dockerfile \
    docker/monitor/Dockerfile docker/monitor/collector.py verification.json .env \
    server-bundle/api/PortKiller.UpdateAdmin.Api server-bundle/worker/PortKiller.UpdatePublisher.Worker \
    server-bundle/notifier/PortKiller.UpdateNotifier server-bundle/tool/PortKiller.UpdateTool server-bundle/web/index.html; do
    [[ -e "$kit_dir/$required" ]] || { echo "缺少文件：$required" >&2; exit 2; }
done
if grep -Eq 'REPLACE_WITH_|updates\.example\.com' .env verification.json; then
    echo "请先填写 .env 和 verification.json 中的域名、DuckDNS Token、现有 RSA 公钥。" >&2
    exit 2
fi
for path in data/admin data/public data/state; do
    if [[ -d "$path" ]] && find "$path" -mindepth 1 -print -quit | grep -q .; then
        echo "新装脚本拒绝复用非空目录：$path。已有部署请使用使用说明中的更新步骤。" >&2
        exit 2
    fi
done
project="$(sed -n 's/^COMPOSE_PROJECT_NAME=//p' .env | tail -n 1)"
monitor_project="$(sed -n 's/^MONITOR_COMPOSE_PROJECT=//p' .env | tail -n 1)"
if [[ ! "$project" =~ ^[a-z0-9][a-z0-9_-]*$ || "$project" != "$monitor_project" ]]; then
    echo ".env 必须配置 COMPOSE_PROJECT_NAME 和 MONITOR_COMPOSE_PROJECT，且两者均为同一 Compose 项目名。" >&2
    exit 2
fi
if [[ -n "$(docker ps -aq --filter "label=com.docker.compose.project=$project")" ]]; then
    echo "Docker 中已存在项目 $project，请使用已有部署更新步骤。" >&2
    exit 2
fi
if [[ "${COMPOSE_PROJECT_NAME:-$project}" != "$project" || "${MONITOR_COMPOSE_PROJECT:-$project}" != "$project" ]]; then
    echo "当前 Shell 的项目名与 .env 不一致，请先清理项目名环境变量。" >&2
    exit 2
fi
sha256sum -c SHA256SUMS.txt >/dev/null
docker compose config --quiet
read -r -p "首位管理员账号：" admin_user
[[ -n "$admin_user" ]] || { echo "管理员账号不能为空。" >&2; exit 2; }

mkdir -p data/admin data/public data/state
chown 10001:10001 data/admin data/public data/state
chmod 2770 data/admin
chmod 0755 data/public
chmod 0700 data/state
chown 10001:10001 verification.json
chmod 0600 verification.json .env

docker compose build --pull api worker notifier caddy monitor
docker compose run --rm --no-deps --entrypoint /opt/portkill/api/PortKiller.UpdateAdmin.Api api db migrate
docker compose run --rm --no-deps --entrypoint /opt/portkill/api/PortKiller.UpdateAdmin.Api api admin init --user "$admin_user"
docker compose up -d
docker compose ps

echo "安装完成。管理地址：https://$(sed -n 's/^ADMIN_DOMAIN=//p' .env | tail -n 1)"
