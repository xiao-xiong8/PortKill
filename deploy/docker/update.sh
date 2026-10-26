#!/usr/bin/env bash
set -Eeuo pipefail
umask 077
echo 'PortKill notifier 更新脚本：修订 3'

kit_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
cd "$kit_dir"
mode="${1:-update}"
if (( $# > 1 )) || [[ "$mode" != update && "$mode" != --rollback ]]; then
    echo '用法：sudo bash docker/update.sh [--rollback]' >&2
    exit 2
fi
for dependency in docker python3 sha256sum flock; do
    command -v "$dependency" >/dev/null || { echo "缺少命令：$dependency" >&2; exit 2; }
done
for required in docker-compose.yml docker/Dockerfile .env verification.json; do
    [[ -f "$required" ]] || { echo "原部署目录缺少：$required" >&2; exit 2; }
done
exec 9>docker/.update.lock
flock -n 9 || { echo '已有更新正在执行。' >&2; exit 2; }
docker compose version >/dev/null
docker compose config --quiet
image_ref="$(docker compose config --format json | python3 -c '
import json, pathlib, sys
service = json.load(sys.stdin)["services"]["notifier"]
build = service.get("build", {})
if not isinstance(build, dict) or pathlib.Path(build.get("context", "")).resolve() != pathlib.Path.cwd():
    raise SystemExit("notifier 的构建目录与原部署目录不一致，请核对 Compose。")
if build.get("dockerfile") != "docker/Dockerfile":
    raise SystemExit("notifier 未使用原 docker/Dockerfile，停止更新。")
image = service.get("image", "")
if not image or "@" in image or any(c.isspace() for c in image):
    raise SystemExit("notifier 需要使用原有可重建的镜像标签。")
print(image)
')"

rollback_tag=portkill-notifier:previous
pending_tag=portkill-notifier:update-base
previous_ref_file=docker/.notifier-previous-image
activated=0
base_pinned=0
keep_base=0
cleanup() {
    if (( base_pinned && ! keep_base )); then
        docker image rm "$pending_tag" >/dev/null 2>&1 || true
    fi
}
trap cleanup EXIT
handle_error() {
    local status="$1"
    trap - ERR
    # ERR is inherited by command substitutions; only the outer shell reports it.
    if (( BASH_SUBSHELL > 0 )); then
        exit "$status"
    fi
    set +e
    if (( activated && base_pinned )); then
        echo '更新检查失败，正在恢复更新前的镜像。' >&2
        if docker image tag "$pending_tag" "$image_ref" && docker compose up -d --no-deps --force-recreate notifier; then
            echo '已重新启动更新前的通知服务；本次更新未成功。' >&2
        else
            keep_base=1
            echo "自动恢复失败，已保留 $rollback_tag 和 $pending_tag 供恢复。" >&2
        fi
    elif [[ "$mode" == --rollback ]]; then
        echo '上一镜像恢复未完成，请检查 Docker 输出。' >&2
    else
        echo '更新未完成，尚未切换通知容器。' >&2
    fi
    exit "$status"
}
trap 'handle_error $?' ERR

if [[ "$mode" == --rollback ]]; then
    [[ -f "$previous_ref_file" ]] || { echo '没有上一镜像记录。' >&2; exit 2; }
    [[ "$(cat "$previous_ref_file")" == "$image_ref" ]] || { echo 'Compose 镜像名与上一记录不一致。' >&2; exit 2; }
    docker image inspect "$rollback_tag" >/dev/null
    docker image tag "$rollback_tag" "$image_ref"
    docker compose up -d --no-deps --force-recreate notifier
    docker compose ps notifier
    echo '已恢复上一通知镜像；磁盘中的更新文件保留。'
    exit 0
fi

[[ -f docker/notifier-update.sha256 ]] || { echo '缺少更新文件校验清单。' >&2; exit 2; }
sha256sum --quiet -c docker/notifier-update.sha256
old_container="$(docker compose ps -a -q notifier)"
[[ -n "$old_container" && "$old_container" != *$'\n'* ]] || { echo '需要原部署中的单个 notifier 容器。' >&2; exit 2; }
old_image_ref="$(python3 docker/resolve-notifier-image.py "$old_container")"
# Pin the actual running image before the mutable Compose tag is rebuilt.
# Use this stable tag for recovery, never a container's possibly untaggable Image value.
docker image tag "$old_image_ref" "$pending_tag"
base_pinned=1
old_image_id="$(docker image inspect --format '{{.Id}}' "$pending_tag")"

# The current container keeps running while the existing image tag is rebuilt.
# Only this build target is selected; Caddy and its DNS module are not rebuilt.
docker compose --progress plain build notifier
new_image_id="$(docker image inspect --format '{{.Id}}' "$image_ref")"
if [[ "$new_image_id" == "$old_image_id" && "$(docker inspect --format '{{.State.Running}}' "$old_container")" == true ]]; then
    docker inspect "$old_container" | python3 docker/check-notifier.py
    echo '通知容器已经使用这次构建的镜像，无需重复重建。'
    exit 0
fi
if [[ "$new_image_id" != "$old_image_id" ]]; then
    docker image tag "$pending_tag" "$rollback_tag"
    printf '%s\n' "$image_ref" > "$previous_ref_file"
fi
activated=1
docker compose up -d --no-deps --force-recreate notifier
new_container="$(docker compose ps -q notifier)"
[[ -n "$new_container" && "$new_container" != *$'\n'* ]]
docker inspect "$new_container" | python3 docker/check-notifier.py
activated=0
docker compose ps notifier
echo '通知服务更新完成。原 Compose、配置、证书和数据卷保持原样。'
