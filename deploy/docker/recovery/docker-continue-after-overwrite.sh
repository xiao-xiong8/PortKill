#!/usr/bin/env bash
# Continue the verified 20261029 deployment after its ZIP was extracted over the old kit.
set -Eeuo pipefail
umask 077

fail() { echo "$*" >&2; exit 2; }
[[ "$#" -eq 2 ]] || fail "用法：sudo bash docker-continue-after-overwrite.sh /现有部署目录 原Compose项目名"
[[ "$(id -u)" -eq 0 ]] || fail "请使用 sudo 运行。"
[[ "$(uname -s)" == Linux && "$(uname -m)" == x86_64 ]] || fail "本包仅支持 Linux x86-64。"
target_dir="$(cd -- "$1" && pwd -P)"
project="$2"
[[ "$project" =~ ^[a-z0-9][a-z0-9_-]*$ ]] || fail "Compose 项目名无效。"
cd "$target_dir"

for path in .env verification.json SHA256SUMS.txt docker-compose.yml; do
    [[ -f "$path" ]] || fail "缺少 $path，原配置必须保留，不能用示例替换。"
done
for path in data/admin data/public data/state server-bundle/web; do
    [[ -d "$path" ]] || fail "缺少已有部署目录 $path，已停止。"
done

# Bind this recovery path to the exact delivered package, then check every packaged file.
echo '2b408d503288cde520599d4b71b55b3c07883759887b9ada541e25e5e2afe678  SHA256SUMS.txt' | sha256sum -c - >/dev/null
echo "正在校验覆盖后的部署文件……"
sha256sum -c SHA256SUMS.txt >/dev/null
if grep -Eq 'REPLACE_WITH_' .env verification.json; then
    fail "检测到示例配置，请保留原 .env 与 verification.json 后再处理。"
fi

export MONITOR_COMPOSE_PROJECT="$project"
compose=(docker compose --project-directory "$target_dir" --env-file "$target_dir/.env" -f "$target_dir/docker-compose.yml" -p "$project")
docker compose version >/dev/null
"${compose[@]}" config --quiet
declare -A containers old_images
for service in api worker notifier caddy; do
    container="$("${compose[@]}" ps -aq "$service")"
    [[ -n "$container" && "$container" != *$'\n'* ]] || fail "项目 $project 中未找到唯一的 $service 容器。"
    [[ "$(docker inspect --format '{{.State.Running}}' "$container")" == true ]] || fail "$service 当前未运行，请先提供容器状态，不能直接执行本流程。"
    actual_project="$(docker inspect --format '{{ index .Config.Labels "com.docker.compose.project" }}' "$container")"
    [[ "$actual_project" == "$project" ]] || fail "$service 容器项目标签不匹配。"
    image="$(docker inspect --format '{{.Image}}' "$container")"
    [[ "$image" =~ ^sha256:[0-9a-f]{64}$ ]] || fail "无法读取 $service 的实际旧镜像 ID。"
    containers[$service]="$container"
    old_images[$service]="$image"
done

check_mount() {
    local service="$1" destination="$2" expected="$3" actual
    actual="$(docker inspect --format "{{range .Mounts}}{{if eq .Destination \"$destination\"}}{{.Source}}{{end}}{{end}}" "${containers[$service]}")"
    [[ "$actual" == "$target_dir/$expected" ]] || fail "$service 的 $destination 挂载与当前目录不一致，已停止。"
}
check_mount api /var/lib/portkill/admin data/admin
check_mount worker /var/lib/portkill/admin data/admin
check_mount worker /srv/portkill/stable data/public
check_mount notifier /srv/portkill/stable data/public
check_mount caddy /srv/web server-bundle/web

echo "已识别原项目：$project；原服务仍在运行。"
echo "此流程备份当前数据库和配置，记录实际旧镜像；被解压覆盖的旧 Web/程序文件不会凭空恢复。"
echo "请确认没有运行中或排队的发布任务，验收前不要提交新任务。"
read -r -p "输入 yes 继续：" confirmed
[[ "$confirmed" == yes ]] || fail "已取消，未停止服务或修改业务数据。"

# All three application services share the same new image; building api is sufficient.
echo "先构建镜像，此时原容器继续运行……"
"${compose[@]}" build api monitor
for service in api worker notifier caddy; do
    [[ "$("${compose[@]}" ps -aq "$service")" == "${containers[$service]}" ]] || fail "构建期间 $service 容器发生变化，已停止，请重新核对。"
done

backup_dir="$(mktemp -d "$(dirname -- "$target_dir")/PortKill-overwrite-backup-XXXXXXXX")"
echo "备份目录：$backup_dir"
on_error() {
    local code="$?"
    trap - ERR
    echo "升级未完成，退出码 $code。备份目录：$backup_dir" >&2
    echo "API/Worker 可能保持停止；请保留现场和输出，不要重装、清空数据或自动恢复旧库。" >&2
    exit "$code"
}
trap on_error ERR

printf '%s\n' "$project" > "$backup_dir/compose-project.txt"
backup_tag="$(date -u +%Y%m%d%H%M%S)-$$"
for service in api worker notifier caddy; do
    # Use running container image IDs, since the on-disk Compose already names the new image.
    rollback_image="portkill-overwrite-backup:$backup_tag-$service"
    docker image tag "${old_images[$service]}" "$rollback_image"
    printf '%s\t%s\t%s\n' "$service" "${old_images[$service]}" "$rollback_image" >> "$backup_dir/old-images.tsv"
    docker inspect "${containers[$service]}" > "$backup_dir/$service.container.json"
done
cp -p .env "$backup_dir/.env"

echo "停止 API/Worker 后备份数据库、WAL、认证密钥、任务状态和当前配置……"
"${compose[@]}" stop api worker
for service in api worker; do
    [[ "$(docker inspect --format '{{.State.Running}}' "${containers[$service]}")" == false ]] || fail "$service 仍在运行，不能备份或迁移。备份目录：$backup_dir"
done
tar -czf "$backup_dir/administration-and-config.tar.gz" \
    .env verification.json docker-compose.yml docker/Caddyfile data/admin data/state
tar -tzf "$backup_dir/administration-and-config.tar.gz" >/dev/null
chmod 0600 "$backup_dir/administration-and-config.tar.gz" "$backup_dir/.env"

printf '\nMONITOR_COMPOSE_PROJECT=%s\n' "$project" >> .env
"${compose[@]}" config --quiet
"${compose[@]}" run --rm --no-deps -T --entrypoint /opt/portkill/api/PortKiller.UpdateAdmin.Api api db migrate
"${compose[@]}" up -d --no-build --no-deps api worker notifier monitor
"${compose[@]}" ps
trap - ERR
echo "升级命令执行完成。请登录原管理网址，刷新运行总览，检查日志并验收；备份：$backup_dir"
