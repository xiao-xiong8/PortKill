#requires -Version 7.0
[CmdletBinding()]
param([string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$evidence = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else {
    Join-Path $repository '.local/artifacts/admin-execution/P8-04'
}
New-Item -ItemType Directory -Path $evidence -Force | Out-Null

Push-Location $repository
try {
    $logPath = Join-Path $evidence 'key-lifecycle.log'
    & dotnet run --project (Join-Path $repository 'tests/PortKiller.UpdateRegression') -c Release --no-launch-profile -- --key-lifecycle 2>&1 |
        Tee-Object -FilePath $logPath
    if ($LASTEXITCODE -ne 0) { throw '隔离信任链演练失败。' }

    $matrix = Get-Content -LiteralPath (Join-Path $repository 'tests/PortKiller.UpdateRegression/P8KeyLifecycleMatrix.json') -Raw | ConvertFrom-Json
    $log = Get-Content -LiteralPath $logPath -Raw
    $markers = foreach ($marker in @($matrix.requiredMarkers)) {
        if (-not $log.Contains([string]$marker, [StringComparison]::Ordinal)) { throw "P8-04 演练缺少证据：$marker" }
        [ordered]@{ marker = [string]$marker; passed = $true }
    }

    $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
    $certificatePath = $env:PORTKILL_AUTHENTICODE_CERTIFICATE
    $certificateConfigured = -not [string]::IsNullOrWhiteSpace($certificatePath) -and (Test-Path -LiteralPath $certificatePath)
    $firstInstallReady = $null -ne $signtool -and $certificateConfigured
    $firstInstallReason = if ($firstInstallReady) { 'certificate material was supplied to this process; external trust-chain verification is still required' } else { 'no production Authenticode certificate and trust-chain material was supplied; update RSA signing does not establish first-install publisher trust' }
    $result = [ordered]@{
        task = 'P8-04'
        phase = 'P8'
        status = if ($firstInstallReady) { 'partial' } else { 'blocked' }
        summaryType = 'trust-chain-and-key-lifecycle-rehearsal'
        recordedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        platform = [Runtime.InteropServices.RuntimeInformation]::OSDescription
        runtime = [Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
        machine = [Environment]::MachineName
        command = 'pwsh -NoProfile -File scripts/checks/Test-KeyLifecycle.ps1'
        rehearsal = [ordered]@{ markers = @($markers); oldClientTrust = 'single fixed public key'; rotation = 'old key rejects new key; new key accepts after coordinated update'; rollback = 'lower version rejected'; remediation = 'higher version and sequence accepted'; firstInstallArchive = 'five-file setup rejected by four-file updater' }
        gates = [ordered]@{ firstInstallAuthenticode = [ordered]@{ ready = $firstInstallReady; signtool = ($null -ne $signtool); certificatePathConfigured = $certificateConfigured; reason = $firstInstallReason }; oldClientKeyMigration = [ordered]@{ ready = $false; reason = 'current client accepts one configured PublicKeyPem; rotation requires a coordinated client configuration migration and recovery path' }; privateKeyLocation = 'not present in website/server bundle; test keys were generated in an isolated temporary directory' }
        evidence = @('key-lifecycle.log', 'P8KeyLifecycleMatrix.json')
        limitations = @('本次演练使用临时 RSA 密钥、回环外观的 HTTPS URL、隔离缓存和临时安装数据。', '未使用真实代码签名证书、生产私钥、旧版本真实安装或公网分发源。', 'P8-04 不应勾选完成，直到首装 Authenticode/可信发布者条件和旧客户端公钥迁移步骤具备。')
        nextTask = if ($firstInstallReady) { 'P8-04' } else { 'P8-04' }
    }
    $result | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $evidence 'result.json') -Encoding utf8
    if ($firstInstallReady) { Write-Output 'PARTIAL P8-04: cryptographic lifecycle rehearsal passed; external first-install and old-client migration gates remain open.' }
    else { Write-Output 'BLOCKED P8-04: cryptographic lifecycle rehearsal passed; first-install Authenticode and old-client key migration gates are not available.' }
} finally {
    Pop-Location
}
