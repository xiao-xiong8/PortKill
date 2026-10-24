#requires -Version 7.0
[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration='Release',[switch]$RunRegression,[switch]$IncludeUi)
$ErrorActionPreference='Stop'
$repository=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& (Join-Path $repository 'scripts/checks/Test-Architecture.ps1')
& dotnet build (Join-Path $repository 'PortKill.Desktop.slnx') -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed' }
if ($RunRegression -or $IncludeUi) {
    $arguments=@('run','--no-build','--project',(Join-Path $repository 'tests/PortKiller.Regression'),'-c',$Configuration)
    if ($IncludeUi) { $arguments+=@('--','--ui') }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Desktop regression failed' }
}
