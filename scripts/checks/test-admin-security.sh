#!/usr/bin/env bash
set -euo pipefail

repository="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
cd -- "$repository"

python3 - "$repository" <<'PY'
import pathlib
import re
import subprocess
import sys

root = pathlib.Path(sys.argv[1])
tracked = subprocess.check_output(["git", "ls-files", "-z"], text=False).decode().split("\0")
tracked = [item for item in tracked if item]
sensitive = [item for item in tracked if re.search(
    r"(?i)(^|/)([^/]+\.)?(pem|pfx|key|secret)$|(^|/)appsettings\.(development|local)\.json$|(^|/)(\.env|[^/]+\.env)$",
    item,
)]
if sensitive:
    raise SystemExit(f"tracked sensitive files: {sensitive}")

roots = [
    root / "src/Services",
    root / "src/Modules/PortKiller.UpdateManagement",
    root / "src/Web/PortKiller.UpdateAdmin.Web/src",
    root / "deploy",
    root / "scripts/build",
]
files = [
    path for base in roots if base.exists() for path in base.rglob("*")
    if path.is_file() and not set(path.parts) & {"bin", "obj", "node_modules", "dist"}
]
forbidden = [
    re.compile(r"BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY"),
    re.compile(r"UseDeveloperExceptionPage"),
    re.compile(r"AddDeveloperExceptionPage"),
    re.compile(r"(?i)(?:Skip|Bypass|Disable)(?:Authentication|Authorization)"),
    re.compile(r"(?i)TestOnlyAuthentication"),
    re.compile(r"Test-Only-Password!|Viewer-Only-Password!|Other-Publisher-Only!"),
]
for path in files:
    text = path.read_text(encoding="utf-8", errors="replace")
    for pattern in forbidden:
        if pattern.search(text):
            raise SystemExit(f"forbidden production security pattern {pattern.pattern}: {path}")
    if re.search(r"(?i)(localStorage|sessionStorage).*\b(token|cookie|secret|privateKey)\b", text):
        raise SystemExit(f"frontend or deployment source may persist authentication material: {path}")

api_root = root / "src/Services/PortKiller.UpdateAdmin.Api"
api_text = "\n".join(path.read_text(encoding="utf-8") for path in api_root.rglob("*.cs"))
expected = [
    '/health/live',
    '/health/ready',
    '/api/v1/auth/csrf',
    '/api/v1/auth/login',
]
if api_text.count(".AllowAnonymous()") != len(expected):
    raise SystemExit("unexpected number of anonymous API endpoints")
for route in expected:
    if route not in api_text:
        raise SystemExit(f"anonymous endpoint allowlist is missing {route}")

print(f"PASS admin security static checks: {len(tracked)} tracked files, {len(files)} production files")
PY
