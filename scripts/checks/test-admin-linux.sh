#!/usr/bin/env bash
set -euo pipefail
repository="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
cd -- "$repository"
bash scripts/checks/test-admin-deployment.sh
results="$repository/.local/artifacts/admin-tests/linux"
mkdir -p -- "$results"
dotnet build PortKill.Server.slnx -c Release --nologo
dotnet test tests/PortKiller.UpdateAdmin.Tests -c Release --no-build --nologo \
    --logger 'trx;LogFileName=admin-linux.trx' --results-directory "$results"
report="$(< "$results/admin-linux.trx")"
[[ "$report" =~ total=\"[1-9][0-9]*\" ]] || { echo 'No tests discovered' >&2; exit 1; }
[[ "$report" =~ notExecuted=\"0\" ]] || { echo 'Tests were skipped' >&2; exit 1; }
echo "Linux administration tests passed: $results/admin-linux.trx"
