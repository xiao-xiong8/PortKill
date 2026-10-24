#requires -Version 7.0
[CmdletBinding()]
param([ValidateSet('Api','Worker','Web')][string]$Service='Api',[switch]$NoBuild)
$ErrorActionPreference='Stop'
$repository=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $PSScriptRoot 'Initialize-Dev.ps1')
Push-Location $repository
try {
    if ($Service -eq 'Web') {
        Push-Location 'src/Web/PortKiller.UpdateAdmin.Web'
        try { & pnpm run dev } finally { Pop-Location }
    } else {
        $project=if ($Service -eq 'Api') { 'src/Services/PortKiller.UpdateAdmin.Api' } else { 'src/Services/PortKiller.UpdatePublisher.Worker' }
        $arguments=@('run','--project',$project,'-c','Release','--no-launch-profile')
        if ($NoBuild) { $arguments+='--no-build' }
        & dotnet @arguments
    }
    if ($LASTEXITCODE -ne 0) { throw "$Service exited with code $LASTEXITCODE" }
} finally { Pop-Location }
