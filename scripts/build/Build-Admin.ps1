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
    $results=Join-Path $repository '.local/artifacts/admin-tests/latest'
    & dotnet test $testProject -c $Configuration --no-build --nologo --logger 'trx;LogFileName=admin-tests.trx' --results-directory $results
    if ($LASTEXITCODE -ne 0) { throw 'Administration tests failed' }
    [xml]$trx=[IO.File]::ReadAllText((Join-Path $results 'admin-tests.trx'))
    if ([int]$trx.TestRun.ResultSummary.Counters.total -lt 1 -or [int]$trx.TestRun.ResultSummary.Counters.notExecuted -gt 0) {
        throw 'Administration test discovery was empty or tests were skipped'
    }
}
