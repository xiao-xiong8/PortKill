#!/usr/bin/env bash
set -Eeuo pipefail
umask 077
[[ "$#" == 2 || ( "$#" == 3 && "$3" == --recover ) ]] || { echo '用法：sudo bash ./docker-server-install.sh . 原Compose项目名 [--recover]' >&2; exit 2; }
[[ "$(id -u)" == 0 && "$(uname -s)" == Linux && "$(uname -m)" == x86_64 ]] || { echo '需要 sudo、Linux x86-64 和现有 Docker Compose 部署。' >&2; exit 2; }
for executable in python3 docker openssl; do command -v "$executable" >/dev/null || { echo "缺少命令：$executable" >&2; exit 2; }; done
root="$(cd -- "$1" && pwd -P)"
script_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
[[ "$root" == "$script_root" ]] || { echo '请在原部署目录解压本包，再执行当前目录的脚本。' >&2; exit 2; }
exec python3 -u "$root/.portkill-server-install/install.py" "$root" "$2" "${3:-}"
