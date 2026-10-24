#requires -Version 7.0
[CmdletBinding()]
param([switch]$InstallWebTools)
$ErrorActionPreference='Stop'
$repository=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$localSdk=Join-Path $repository '.local/cache/dotnet'
if (Test-Path -LiteralPath (Join-Path $localSdk 'dotnet.exe')) {
    $env:DOTNET_ROOT=$localSdk
    $env:PATH="$localSdk;$env:PATH"
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$sdkVersion=& dotnet --version
if ($LASTEXITCODE -ne 0 -or $sdkVersion -notmatch '^10\.0\.4\d\d$') { throw 'Install the .NET SDK feature band required by global.json.' }
$tools=Join-Path $repository '.local/cache/node-tools'
$pnpmName=if ($IsWindows) { 'pnpm.cmd' } else { 'pnpm' }
$localPnpm=Join-Path $tools "node_modules/.bin/$pnpmName"
if ($InstallWebTools -and -not (Test-Path -LiteralPath $localPnpm)) {
    $npmName=if ($IsWindows) { 'npm.cmd' } else { 'npm' }
    & $npmName install --prefix $tools pnpm@11.25.0 --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw 'Could not install the pinned pnpm locally.' }
}
if (Test-Path -LiteralPath $localPnpm) { $env:PATH="$(Split-Path -Parent $localPnpm);$env:PATH" }
$configuration=Join-Path $repository '.local/config/admin.local.json'
if (-not (Test-Path -LiteralPath $configuration)) {
    [IO.Directory]::CreateDirectory((Split-Path -Parent $configuration)) | Out-Null
    $dataRoot=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'PortKill.UpdateAdmin.Development'
    @{Administration=@{DataRoot=$dataRoot};urls='http://127.0.0.1:5081'} | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $configuration -Encoding utf8
}
$env:PORTKILL_ADMIN_CONFIG=$configuration
Write-Output "Development environment ready; .NET $sdkVersion"
