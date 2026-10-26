#!/usr/bin/env bash
set -Eeuo pipefail
umask 077
kit_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
cd "$kit_dir"
[[ "$(id -u)" -eq 0 ]] || { echo '请使用 sudo bash docker/update-publisher.sh' >&2; exit 2; }
for dependency in docker python3 sha256sum flock; do
    command -v "$dependency" >/dev/null || { echo "缺少命令：$dependency" >&2; exit 2; }
done
exec 9>docker/.update.lock
flock -n 9 || { echo '已有部署更新正在执行。' >&2; exit 2; }
sha256sum --quiet -c docker/publisher-update.sha256
echo '发布组件修复 r2：校验文件通过。'
docker compose config --quiet
image_ref="$(docker compose config --format json | python3 -c '
import json, pathlib, sys
service = json.load(sys.stdin)["services"]["worker"]
build = service.get("build", {})
if not isinstance(build, dict) or pathlib.Path(build.get("context", "")).resolve() != pathlib.Path.cwd() or build.get("dockerfile") != "docker/Dockerfile":
    raise SystemExit("worker 的构建路径与原部署不一致。")
image = service.get("image", "")
if not image or "@" in image or any(c.isspace() for c in image): raise SystemExit("worker 镜像标签无效。")
print(image)
')"
old_container="$(docker compose ps -a -q worker)"
[[ -n "$old_container" && "$old_container" != *$'\n'* ]] || { echo '需要单个现有 worker 容器。' >&2; exit 2; }
old_reference="$(python3 docker/resolve-notifier-image.py "$old_container" worker)"
rollback_tag=portkill-publisher:previous
pending_tag=portkill-publisher:update-base
docker image tag "$old_reference" "$pending_tag"
switched=0
cleanup() {
    result="$?"
    # Keep the fixed backup reference after any failure, including failed rollback.
    if (( result == 0 )); then docker image rm "$pending_tag" >/dev/null 2>&1 || true; fi
}
trap cleanup EXIT
failed() {
    status="$?"
    trap - ERR
    if (( BASH_SUBSHELL > 0 )); then exit "$status"; fi
    set +e
    # Restore the mutable Compose tag even if a build or offline self-check failed.
    if docker image tag "$pending_tag" "$rollback_tag" && docker image tag "$pending_tag" "$image_ref"; then
        if (( switched )); then
            echo 'Worker 启动验证失败，正在恢复原运行镜像。' >&2
            docker compose up -d --no-deps --no-build --pull never --force-recreate worker
        fi
    else
        echo '镜像标签恢复失败，已保留 portkill-publisher:update-base，请保留现场继续诊断。' >&2
    fi
    echo '发布组件更新未完成，请保留完整输出。' >&2
    exit "$status"
}
trap failed ERR

# The state-directory patch is idempotent; avoid a container switch until the new image passes its self-check.
[[ -d data/state && ! -L data/state ]] || { echo '请先执行 docker/fix-publish-state.sh 修复状态挂载。' >&2; exit 2; }
docker compose config --format json | python3 -c '
import json, pathlib, sys
mounts = json.load(sys.stdin)["services"]["worker"].get("volumes", [])
if not any(v.get("target") == "/srv/portkill/.stable-update-state" and v.get("type") == "bind"
    and pathlib.Path(v.get("source", "")).resolve() == pathlib.Path("data/state").resolve()
    and not v.get("read_only", False) for v in mounts):
    raise SystemExit("请先执行 docker/fix-publish-state.sh 修复状态挂载。")
'
docker compose --progress plain build worker
docker run --rm --network none \
    --mount "type=bind,source=$kit_dir/verification.json,target=/etc/portkill/verification.json,readonly" \
    -e UpdateTool__ExecutablePath=/opt/portkill/tool/PortKiller.UpdateTool \
    -e UpdateTool__VerificationConfigPath=/etc/portkill/verification.json \
    --entrypoint /opt/portkill/worker/PortKiller.UpdatePublisher.Worker \
    "$image_ref" --self-check-tool-owner
if [[ "$(docker image inspect --format '{{.Id}}' "$image_ref")" == "$(docker image inspect --format '{{.Id}}' "$pending_tag")" ]]; then
    echo '当前 Worker 已包含本修复，身份自检通过，无需重建容器。'
    exit 0
fi
docker image tag "$pending_tag" "$rollback_tag"
switched=1
docker compose up -d --no-deps --no-build --pull never --force-recreate worker
docker compose exec -T worker /bin/true
switched=0
docker compose ps worker
echo '发布组件已更新，父子进程身份自检通过。原任务会继续恢复，无需重新上传 0.1.1。'
