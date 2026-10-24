#requires -Version 7.0
[CmdletBinding()]
param([switch]$IncludeBrowser)
$ErrorActionPreference='Stop'
$repository=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& (Join-Path $repository 'scripts/build/Build-Admin.ps1') -RunTests
& (Join-Path $repository 'scripts/checks/Test-AdminContract.ps1')
& (Join-Path $repository 'scripts/build/Build-Web.ps1') -Check
if ($IncludeBrowser) {
    $pnpmName=if ($IsWindows) { 'pnpm.cmd' } else { 'pnpm' }
    $pnpm=Join-Path $repository ".local/cache/node-tools/node_modules/.bin/$pnpmName"
    if (-not (Test-Path -LiteralPath $pnpm)) { $pnpm=$pnpmName }
    Push-Location (Join-Path $repository 'src/Web/PortKiller.UpdateAdmin.Web')
    try {
        & $pnpm run test:e2e
        if ($LASTEXITCODE -ne 0) { throw 'Browser tests failed' }
    } finally { Pop-Location }
}
