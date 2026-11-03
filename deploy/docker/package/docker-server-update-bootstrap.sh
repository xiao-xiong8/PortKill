#!/usr/bin/env bash
set -Eeuo pipefail
umask 077
fail() { echo "$*" >&2; exit 2; }
[[ "$#" == 2 || "$#" == 5 ]] || fail '用法：sudo bash docker-server-update-bootstrap.sh /部署目录 Compose项目名 [--apply /绝对路径/更新.zip 已核对的ComposeSHA256]'
[[ "$(id -u)" == 0 && "$(uname -s)" == Linux && "$(uname -m)" == x86_64 ]] || fail '仅支持 root / Ubuntu Linux x86-64。'
for executable in docker python3 openssl sha256sum stat realpath; do command -v "$executable" >/dev/null || fail "缺少命令：$executable"; done
root="$(realpath -e -- "$1")"
project="$2"
[[ "$project" =~ ^[a-z0-9][a-z0-9_-]*$ && "$root" != / && "$root" != *','* ]] || fail '部署目标无效。'
for file in docker-compose.yml .env verification.json; do [[ -f "$root/$file" && ! -L "$root/$file" ]] || fail "缺少真实部署文件：$file"; done
compose=(docker compose --project-directory "$root" --env-file "$root/.env" -f "$root/docker-compose.yml" -p "$project")
"${compose[@]}" config --quiet
"${compose[@]}" ps
sha="$(sha256sum "$root/docker-compose.yml" | cut -d ' ' -f 1)"
echo "Compose SHA256: $sha"
if [[ "$#" == 2 ]]; then
    echo '以上为只读检查。核对项目/路径后，用 --apply 更新ZIP 上述SHA256 执行首次接入；不会安装常驻服务。'
    exit 0
fi
[[ "$3" == --apply && "$5" == "$sha" ]] || fail 'Compose 摘要不符；停止写入，请重新核对。'
package="$(realpath -e -- "$4")"
api="$("${compose[@]}" ps -q api)"
[[ -n "$api" && "$api" != *$'\n'* ]] || fail 'API 容器不唯一。'
temporary="$(mktemp -d)"
trap 'rm -rf -- "$temporary"' EXIT
helper="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/prepare-server-update.py"
docker_root="$(docker info --format '{{.DockerRootDir}}')"
[[ "$docker_root" == /* && "$docker_root" != / && "$docker_root" != *','* ]] || fail 'Docker data-root 无效。'
python3 "$helper" "$package" "$root/verification.json" "$temporary" "$root" "$docker_root"
docker load --input "$temporary/runner.tar.gz"
image="$(cat "$temporary/runner-image")"
group="$(stat -c '%g' /var/run/docker.sock)"
# The detached one-shot container survives this SSH session and API recreation.
name="portkill-bootstrap-$(date +%s)-$$"
docker run --detach --name "$name" --label portkill.server-update.bootstrap=true --network host --user 0:0 \
    --volumes-from "$api" --mount "type=bind,src=$root,dst=$root" \
    --mount type=bind,src=/var/run/docker.sock,dst=/var/run/docker.sock \
    --mount "type=bind,src=$docker_root,dst=/docker-storage,readonly" \
    --mount "type=bind,src=$package,dst=/update-input/package.zip,readonly" \
    "$image" bootstrap "$root" "$project" "$sha" "$group"
echo "首次接入已启动；查看日志：sudo docker logs -f $name"
echo "容器结果：sudo docker inspect --format '{{.State.ExitCode}}' $name；验收后可 docker rm $name。"
