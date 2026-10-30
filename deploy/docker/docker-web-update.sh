#!/usr/bin/env bash
# Update Caddy's bind-mounted web files without replacing the mount directory.
set -Eeuo pipefail
umask 022

fail() { echo "$*" >&2; exit 2; }
mode=apply
if [[ "${1:-}" == --rollback ]]; then mode=rollback; shift; fi
if [[ "$mode" == apply && "$#" -ne 2 || "$mode" == rollback && "$#" -ne 3 ]]; then
    fail "用法：sudo bash docker-web-update.sh [--rollback] /部署目录 Compose项目名 [备份目录]"
fi
[[ "$(id -u)" -eq 0 ]] || fail "请使用 sudo 运行。"
[[ "$(uname -s)" == Linux ]] || fail "请在 Linux Docker 服务器上运行。"
for command in docker sha256sum tar install mktemp flock realpath; do
    command -v "$command" >/dev/null || fail "缺少命令：$command"
done
package_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
target_dir="$(cd -- "$1" && pwd -P)"
project="$2"
[[ "$project" =~ ^[a-z0-9][a-z0-9_-]*$ ]] || fail "Compose 项目名无效。"
web_dir="$target_dir/server-bundle/web"
[[ -f "$target_dir/.env" && -f "$target_dir/docker-compose.yml" && -f "$web_dir/index.html" ]] || fail "部署目录不完整。"
[[ "$(realpath -e -- "$web_dir")" == "$web_dir" ]] || fail "Web 目录包含符号链接，请先核对挂载。"
[[ -z "$(find "$web_dir" -type l -print -quit)" ]] || fail "Web 目录内包含符号链接，已停止。"

in_place=false
if [[ "$mode" == apply ]]; then
    if [[ "$package_dir" == "$target_dir" ]]; then
        in_place=true
    elif [[ "$package_dir/" == "$target_dir/"* || "$target_dir/" == "$package_dir/"* ]]; then
        fail "增量包必须位于部署根目录，或与部署目录完全分开，不能嵌套放置。"
    fi
    [[ -f "$package_dir/SHA256SUMS.txt" && -f "$package_dir/server-bundle/web/index.html" ]] || fail "增量包不完整。"
    (cd "$package_dir" && sha256sum -c SHA256SUMS.txt >/dev/null)
fi

exec 9>"$target_dir/.portkill-web-update.lock"
flock -n 9 || fail "已有 Web 更新正在执行。"
compose=(docker compose --project-directory "$target_dir" --env-file "$target_dir/.env" -f "$target_dir/docker-compose.yml" -p "$project")
"${compose[@]}" config --quiet
container="$("${compose[@]}" ps -q caddy)"
[[ -n "$container" && "$container" != *$'\n'* ]] || fail "项目 $project 中未找到唯一运行中的 Caddy 容器。"
[[ "$(docker inspect --format '{{.State.Running}}' "$container")" == true ]] || fail "Caddy 当前未运行。"
[[ "$(docker inspect --format '{{ index .Config.Labels "com.docker.compose.project" }}' "$container")" == "$project" ]] || fail "Caddy 项目标签不匹配。"
mount_source="$(docker inspect --format '{{range .Mounts}}{{if and (eq .Type "bind") (eq .Destination "/srv/web")}}{{.Source}}{{end}}{{end}}' "$container")"
[[ "$mount_source" == "$web_dir" ]] || fail "Caddy 的 /srv/web 挂载与部署目录不一致。"
current_hash="$(sha256sum "$web_dir/index.html" | cut -d ' ' -f 1)"
mounted_hash="$(docker exec "$container" sha256sum /srv/web/index.html | cut -d ' ' -f 1)"
[[ "$current_hash" == "$mounted_hash" ]] || fail "Caddy 中的首页与宿主机文件不一致。"

verify_mounted_web() {
    local source_dir="$1" file relative expected actual
    while IFS= read -r -d '' file; do
        relative="${file#"$source_dir"/}"
        expected="$(sha256sum "$file" | cut -d ' ' -f 1)"
        actual="$(docker exec "$container" sha256sum "/srv/web/$relative" | cut -d ' ' -f 1)"
        [[ "$actual" == "$expected" ]] || fail "Caddy 中的 Web 文件校验失败：$relative"
    done < <(find "$source_dir" -type f -print0)
}

if [[ "$in_place" == true ]]; then
    # unzip -o already wrote the payload into the live bind mount. Do not copy
    # files onto themselves or misrepresent an after-the-fact snapshot as a backup.
    verify_mounted_web "$web_dir"
    echo "当前目录的 Web 文件及 Caddy 挂载校验通过，直接覆盖已生效。"
    echo "无需创建临时目录、重建镜像或重启容器；请用 Ctrl+F5 刷新原管理网址。"
    echo "本次直接覆盖未生成覆盖前备份；回退请使用已有的旧版本备份。"
    exit 0
fi

backup_dir=''
stage_dir=''
index_temp=''
cleanup() {
    [[ -z "$index_temp" ]] || rm -f -- "$index_temp"
    [[ -z "$stage_dir" ]] || rm -rf -- "$stage_dir"
    return 0
}
trap cleanup EXIT
on_error() {
    local code="$?"
    echo "Web 更新未完成，退出码：$code。" >&2
    [[ -z "$backup_dir" ]] || printf '备份目录：%s\n' "$backup_dir" >&2
    exit "$code"
}
trap on_error ERR

if [[ "$mode" == apply ]]; then
    source_dir="$package_dir/server-bundle/web"
    [[ -z "$(find "$source_dir" -type l -print -quit)" ]] || fail "增量包 Web 目录包含符号链接。"
else
    backup_dir="$(cd -- "$3" && pwd -P)"
    [[ -f "$backup_dir/deployment.txt" && -f "$backup_dir/web-backup.sha256" ]] || fail "备份目录不完整。"
    [[ "$(cat "$backup_dir/deployment.txt")" == "$(printf '%s\n%s' "$target_dir" "$project")" ]] || fail "备份所属目录或项目与目标不一致。"
    (cd "$backup_dir" && sha256sum -c web-backup.sha256 >/dev/null)
    stage_dir="$(mktemp -d)"
    tar -xzf "$backup_dir/web.tar.gz" -C "$stage_dir" --no-same-owner
    source_dir="$stage_dir"
    [[ -f "$source_dir/index.html" && -z "$(find "$source_dir" -type l -print -quit)" ]] || fail "备份 Web 内容无效。"
fi

# Back up the current view for both update and rollback, before writing anything.
backup_dir="$(mktemp -d "$(dirname -- "$target_dir")/PortKill-web-backup-XXXXXXXX")"
chmod 0700 "$backup_dir"
printf '%s\n%s\n' "$target_dir" "$project" > "$backup_dir/deployment.txt"
tar -czf "$backup_dir/web.tar.gz" -C "$web_dir" .
(cd "$backup_dir" && sha256sum web.tar.gz deployment.txt > web-backup.sha256)
echo "已备份当前 Web：$backup_dir"

# Retain previous hashed assets for already-open tabs and publish index.html last.
while IFS= read -r -d '' file; do
    relative="${file#"$source_dir"/}"
    [[ "$relative" == index.html ]] && continue
    install -D -m 0644 -- "$file" "$web_dir/$relative"
done < <(find "$source_dir" -type f -print0)
index_temp="$(mktemp "$web_dir/.index.XXXXXXXX")"
install -m 0644 -- "$source_dir/index.html" "$index_temp"
mv -f -- "$index_temp" "$web_dir/index.html"
index_temp=''

verify_mounted_web "$source_dir"
trap - ERR
echo "Web 替换完成，容器无需重启。请用 Ctrl+F5 刷新原管理网址。"
printf '回退命令：sudo bash %q --rollback %q %q %q\n' "$package_dir/docker-web-update.sh" "$target_dir" "$project" "$backup_dir"
