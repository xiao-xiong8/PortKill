#!/usr/bin/env bash
set -euo pipefail

package_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
if [[ "$#" -ne 2 ]]; then
    echo "用法：sudo bash docker-upgrade.sh /现有部署目录 现有Compose项目名" >&2
    exit 2
fi
[[ "$(id -u)" -eq 0 ]] || { echo "请使用 sudo 运行。" >&2; exit 2; }
[[ "$(uname -s)" == Linux && "$(uname -m)" == x86_64 ]] || { echo "本包仅支持 Linux x86-64 服务器。" >&2; exit 2; }
target_dir="$(cd -- "$1" && pwd)"
project="$2"
[[ "$project" =~ ^[a-z0-9][a-z0-9_-]*$ ]] || { echo "Compose 项目名无效。" >&2; exit 2; }
if [[ "$package_dir" == "$target_dir" || "$package_dir/" == "$target_dir/"* || "$target_dir/" == "$package_dir/"* ]]; then
    echo "请把新包解压到与现有部署分开的临时目录。" >&2
    exit 2
fi
docker compose version >/dev/null
cd "$package_dir"
sha256sum -c SHA256SUMS.txt >/dev/null

for required in .env verification.json docker-compose.yml docker/Dockerfile docker/Caddyfile docker/Caddy.Dockerfile; do
    [[ -f "$target_dir/$required" ]] || { echo "现有部署缺少 $required。" >&2; exit 2; }
done
for required in data/admin data/public data/state server-bundle/web; do
    [[ -d "$target_dir/$required" ]] || { echo "现有部署缺少目录 $required。" >&2; exit 2; }
done
reference_hash="15ae87cb0fb67b1131f6ecf53a6ea43f65dbf58d069b1a3d17919b36610943f1"
actual_hash="$(sha256sum "$target_dir/docker-compose.yml" | cut -d ' ' -f 1)"
if [[ "$actual_hash" != "$reference_hash" ]]; then
    echo "现有 Compose 与已核对的 20261026 版本不同，已停止；请人工合并配置，不能直接覆盖。" >&2
    exit 2
fi
for config in docker/Dockerfile docker/Caddyfile docker/Caddy.Dockerfile; do
    cmp -s "$package_dir/$config" "$target_dir/$config" || { echo "$config 已有自定义修改，已停止，请人工核对。" >&2; exit 2; }
done

export MONITOR_COMPOSE_PROJECT="$project"
old_compose=(docker compose --project-directory "$target_dir" --env-file "$target_dir/.env" -f "$target_dir/docker-compose.yml" -p "$project")
new_compose=(docker compose --project-directory "$package_dir" --env-file "$target_dir/.env" -f "$package_dir/docker-compose.yml" -p "$project")
"${old_compose[@]}" config --quiet
"${new_compose[@]}" config --quiet
for service in api worker notifier caddy; do
    containers="$("${old_compose[@]}" ps -aq "$service")"
    [[ -n "$containers" && "$containers" != *$'\n'* ]] || { echo "项目 $project 中未找到唯一的 $service 容器，请核对项目名。" >&2; exit 2; }
done

echo "已核对项目 $project。更新期间请勿提交发布任务。"
read -r -p "请确认管理页面没有运行中或排队的发布任务，输入 yes 继续：" confirmed
[[ "$confirmed" == yes ]] || { echo "已取消，原部署未修改。" >&2; exit 2; }
echo "现在先构建镜像，原服务继续运行。"
"${new_compose[@]}" build api monitor

backup_dir="$(dirname -- "$target_dir")/PortKill-backup-$(date +%Y%m%d-%H%M%S)-$$"
mkdir -m 0700 "$backup_dir"
printf '%s\n' "$project" > "$backup_dir/compose-project.txt"
cp -p "$target_dir/.env" "$backup_dir/.env"
"${old_compose[@]}" images -q > "$backup_dir/image-ids.txt"
echo "停止 API/Worker，创建本地备份：$backup_dir"
trap 'echo "更新未完成。备份目录：$backup_dir；请按使用说明检查或回退，API/Worker 可能仍停止。" >&2' ERR
"${old_compose[@]}" stop api worker
tar -C "$target_dir" -czf "$backup_dir/deployment.tar.gz" docker-compose.yml server-bundle data/admin
chmod 0600 "$backup_dir/deployment.tar.gz" "$backup_dir/.env"

cp "$package_dir/docker-compose.yml" "$target_dir/docker-compose.yml"
cp -a "$package_dir/server-bundle/." "$target_dir/server-bundle/"
mkdir -p "$target_dir/docker/monitor"
cp -a "$package_dir/docker/monitor/." "$target_dir/docker/monitor/"
printf '\nMONITOR_COMPOSE_PROJECT=%s\n' "$project" >> "$target_dir/.env"

"${old_compose[@]}" config --quiet
"${old_compose[@]}" run --rm --no-deps --entrypoint /opt/portkill/api/PortKiller.UpdateAdmin.Api api db migrate
"${old_compose[@]}" up -d --no-deps api worker notifier monitor
"${old_compose[@]}" ps
trap - ERR
echo "升级命令执行完成，备份保存在 $backup_dir。请登录运行总览并按使用说明完成验收。"
