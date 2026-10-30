#!/usr/bin/env bash
# Add public report routes without replacing the user's deployment or secrets.
set -Eeuo pipefail
umask 077
[[ "$#" -eq 2 ]] || { echo '用法：sudo bash docker-client-reports-update.sh /部署目录 Compose项目名' >&2; exit 2; }
target_dir="$(cd -- "$1" && pwd -P)"
project="$2"
[[ "$project" =~ ^[a-z0-9][a-z0-9_-]*$ ]] || exit 2
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
config="$target_dir/docker/Caddyfile"
[[ -f "$config" && -f "$target_dir/.env" ]] || { echo '缺少原 Caddyfile 或 .env。' >&2; exit 2; }
compose=(docker compose --project-directory "$target_dir" --env-file "$target_dir/.env" -f "$target_dir/docker-compose.yml" -p "$project")
container="$("${compose[@]}" ps -q caddy)"
[[ -n "$container" && "$container" != *$'\n'* ]] || { echo '未找到唯一运行中的 Caddy 容器。' >&2; exit 2; }
actual_mount="$(docker inspect --format '{{range .Mounts}}{{if eq .Destination "/etc/caddy/Caddyfile"}}{{.Source}}{{end}}{{end}}' "$container")"
[[ "$actual_mount" == "$config" ]] || { echo 'Caddyfile 挂载与部署目录不一致，已停止。' >&2; exit 2; }
candidate="$(mktemp)"
trap 'rm -f -- "$candidate"' EXIT
python3 "$script_dir/enable-client-reports.py" --input "$config" --output "$candidate"
if cmp -s "$config" "$candidate"; then
    "${compose[@]}" exec -T caddy caddy validate --config /dev/stdin --adapter caddyfile < "$candidate"
    "${compose[@]}" exec -T caddy caddy reload --config /dev/stdin --adapter caddyfile < "$candidate"
    echo '客户端上报路由已存在，已重新加载到运行中的 Caddy。'
    exit 0
fi
"${compose[@]}" exec -T caddy caddy validate --config /dev/stdin --adapter caddyfile < "$candidate"
backup="$config.before-client-reports-$(date -u +%Y%m%d%H%M%S)-$$"
cp -p -- "$config" "$backup"
rollback() {
    trap - ERR INT TERM
    cat "$backup" > "$config" || echo "恢复配置文件失败，请保留备份：$backup" >&2
    "${compose[@]}" exec -T caddy caddy reload --config /dev/stdin --adapter caddyfile < "$backup" || true
    echo "加载未完成，已尝试恢复原配置。备份：$backup" >&2
    exit 1
}
trap rollback ERR INT TERM
# Keep the inode of the bind-mounted file; rename would leave the container reading the old file.
cat "$candidate" > "$config"
"${compose[@]}" exec -T caddy caddy reload --config /dev/stdin --adapter caddyfile < "$candidate"
trap - ERR INT TERM
echo "客户端注册/上报路由已加载。备份：$backup"
