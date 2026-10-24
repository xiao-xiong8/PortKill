[CmdletBinding()]
param([switch]$Update)
$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$contract = Join-Path $repository 'deploy/contracts/admin-api.v1.json'
$temporary = Join-Path $repository ('.local/tmp/admin-contract-' + [guid]::NewGuid().ToString('N') + '.json')
Push-Location $repository
try {
    & dotnet run --project src/Services/PortKiller.UpdateAdmin.Api -c Release -- export-contract $temporary
    if ($LASTEXITCODE -ne 0) { throw 'OpenAPI export failed' }
    if ($Update) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $contract) -Force | Out-Null
        Copy-Item -LiteralPath $temporary -Destination $contract
    } elseif (-not (Test-Path -LiteralPath $contract) -or [IO.File]::ReadAllText($contract).Replace("`r`n","`n") -ne [IO.File]::ReadAllText($temporary).Replace("`r`n","`n")) {
        throw 'OpenAPI differs from checked-in contract; run Test-AdminContract.ps1 -Update and regenerate frontend types'
    }
    Write-Output 'PASS: generated API contract matches source'
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
    Pop-Location
}
