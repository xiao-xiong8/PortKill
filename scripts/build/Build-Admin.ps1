#requires -Version 7.0
[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration='Release',[switch]$RunTests)
$ErrorActionPreference='Stop'
$repository=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& (Join-Path $repository 'scripts/checks/Test-Architecture.ps1')
& dotnet build (Join-Path $repository 'PortKill.Server.slnx') -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw 'Server build failed' }
if ($RunTests) {
    $testProject=Join-Path $repository 'tests/PortKiller.UpdateAdmin.Tests'
    if (-not (Test-Path -LiteralPath $testProject)) { throw 'Administration tests are not present' }
    & dotnet test $testProject -c $Configuration --no-build --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Administration tests failed' }
}
