#!/usr/bin/env bash
set -Eeuo pipefail
umask 077
fail() { echo "$*" >&2; exit 2; }
[[ "$#" == 3 ]] || fail '用法：sudo bash docker-server-update-recover.sh /部署目录 Compose项目名 任务ID'
[[ "$(id -u)" == 0 && "$(uname -s)" == Linux && "$(uname -m)" == x86_64 ]] || fail '仅支持 root / Ubuntu Linux x86-64。'
root="$(realpath -e -- "$1")"
project="$2"
task="$3"
[[ "$root" != / && "$root" != *','* && "$project" =~ ^[a-z0-9][a-z0-9_-]*$ && "$task" =~ ^[a-f0-9]{32}$ ]] || fail '部署目标或任务ID无效。'
request="$root/data/admin/server-updates/tasks/$task/request.json"
image="$(python3 - "$root" "$project" "$task" "$request" <<'PY'
import json, re, sys
from pathlib import Path
root, project, task, request = sys.argv[1:]
path = Path(request)
for item in (path, *path.parents):
    if item.is_symlink():
        raise SystemExit('恢复记录不能经过符号链接')
data = json.loads(path.read_text())
if data['options']['deploymentRoot'] != root or data['options']['project'] != project or data['taskId'] != task or data['dataRoot'] != '/var/lib/portkill/admin':
    raise SystemExit('恢复记录与目标部署不符')
package = json.loads((Path(root) / 'data/admin/server-updates/packages' / data['packageId'] / 'package.json').read_text())
image = package['runnerImage']
if not re.fullmatch(r'portkill-server-update-runner:[A-Za-z0-9][A-Za-z0-9._-]{0,127}', image):
    raise SystemExit('恢复镜像标签无效')
print(image)
PY
)"
docker image inspect "$image" >/dev/null || fail '本机缺少原任务 runner 镜像；请从原签名更新包恢复该镜像后重试，勿使用任意镜像。'
name="portkill-recovery-$task-$(date +%s)"
docker run --detach --name "$name" --label "portkill.server-update.recovery=$task" --network host --user 0:0 \
    --mount "type=bind,src=$root,dst=$root" \
    --mount "type=bind,src=$root/data/admin,dst=/var/lib/portkill/admin" \
    --mount type=bind,src=/var/run/docker.sock,dst=/var/run/docker.sock \
    "$image" recover "/var/lib/portkill/admin/server-updates/tasks/$task/request.json"
echo "恢复已启动（不覆盖数据库）；查看日志：sudo docker logs -f $name"
echo "检查退出码：sudo docker inspect --format '{{.State.ExitCode}}' $name；仅退出码 0 表示恢复检查通过。"
echo "验收后可删除这个已退出的恢复容器：sudo docker rm $name"
