#!/usr/bin/env bash
set -Eeuo pipefail
umask 077

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
if [[ -f "$script_dir/../docker-compose.yml" ]]; then
    kit_dir="$(cd -- "$script_dir/.." && pwd -P)"
else
    kit_dir="$(cd -- "$script_dir/../.." && pwd -P)"
fi
cd "$kit_dir"

if [[ "$(id -u)" -ne 0 ]]; then
    echo '请使用 sudo 运行：sudo bash docker/fix-publish-state.sh' >&2
    exit 2
fi
for dependency in docker python3; do
    command -v "$dependency" >/dev/null || { echo "缺少命令：$dependency" >&2; exit 2; }
done
[[ -f docker-compose.yml ]] || { echo '缺少 docker-compose.yml。' >&2; exit 2; }
[[ -d data && ! -L data && ! -L data/state ]] || { echo 'data 必须是现有本地目录，data/state 不能是符号链接。' >&2; exit 2; }
docker compose config --quiet

python3 - <<'PY'
from pathlib import Path

path = Path("docker-compose.yml")
if path.is_symlink(): raise SystemExit("Compose 文件不能是符号链接。")
text = path.read_text(encoding="utf-8")
required = "    - ./data/public:/srv/portkill/stable\n"
mount = "    - ./data/state:/srv/portkill/.stable-update-state\n"
worker_start = text.index("  worker:\n")
worker_end = text.index("  notifier:\n", worker_start)
worker = text[worker_start:worker_end]
if required not in worker:
    raise SystemExit("worker 未使用预期的 data/public 挂载，停止修改。")
if mount not in worker:
    if "/srv/portkill/.stable-update-state" in worker:
        raise SystemExit("worker 已存在其他发布状态挂载，请核对原配置。")
    worker = worker.replace(required, required + mount, 1)
    text = text[:worker_start] + worker + text[worker_end:]
    backup = path.with_name(path.name + ".before-state-fix")
    if not backup.exists(): backup.write_text(path.read_text(encoding="utf-8"), encoding="utf-8")
    path.write_text(text, encoding="utf-8", newline="\n")
PY

mkdir -p data/state
chown 10001:10001 data/state
chmod 0700 data/state
docker compose config --quiet
docker compose up -d --no-deps --no-build --pull never worker

echo '已挂载私有发布状态目录并重启 worker。'
echo '验证发布目录和状态（不激活版本）：'
docker compose exec -T worker /opt/portkill/tool/PortKiller.UpdateTool \
    inspect --root /srv/portkill/stable --config /etc/portkill/verification.json
echo '目录检查通过。请刷新原任务页面；若仍出现 operation_conflict，请保留输出继续诊断，不要重复上传或删除任务数据。'
