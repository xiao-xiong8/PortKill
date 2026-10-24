#requires -Version 7.0
[CmdletBinding()]
param([switch]$Check,[string]$Pnpm)
$ErrorActionPreference='Stop'
$repository=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (-not $Pnpm) {
    $name=if ($IsWindows) { 'pnpm.cmd' } else { 'pnpm' }
    $local=Join-Path $repository ".local/cache/node-tools/node_modules/.bin/$name"
    $Pnpm=if (Test-Path -LiteralPath $local) { $local } else { $name }
}
Push-Location (Join-Path $repository 'src/Web/PortKiller.UpdateAdmin.Web')
try {
    & $Pnpm install --frozen-lockfile
    if ($LASTEXITCODE -ne 0) { throw 'Frozen frontend install failed' }
    & $Pnpm run check:api
    if ($LASTEXITCODE -ne 0) { throw 'Frontend contract check failed' }
    if ($Check) {
        foreach ($checkName in @('lint','test:unit')) {
            & $Pnpm run $checkName
            if ($LASTEXITCODE -ne 0) { throw "Frontend $checkName failed" }
        }
    }
    & $Pnpm run build
    if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed' }
} finally { Pop-Location }
