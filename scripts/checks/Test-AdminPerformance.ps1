#requires -Version 7.0
[CmdletBinding()]
param([string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$evidence = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else {
    Join-Path $repository '.local/artifacts/admin-execution/P8-02'
}
New-Item -ItemType Directory -Path $evidence -Force | Out-Null

function Invoke-Captured([string]$File, [string[]]$CliArgs, [string]$LogPath) {
    Push-Location $repository
    try {
        & pwsh -NoProfile -File $File @CliArgs 2>&1 | Tee-Object -FilePath $LogPath
        if ($LASTEXITCODE -ne 0) { throw "命令失败 ($LASTEXITCODE): $File $($CliArgs -join ' ')" }
    } finally { Pop-Location }
}

Push-Location $repository
try {
    $buildLog = Join-Path $evidence 'build.log'
    & pwsh -NoProfile -File (Join-Path $repository 'scripts/checks/Test-Architecture.ps1') 2>&1 | Tee-Object -FilePath $buildLog
    if ($LASTEXITCODE -ne 0) { throw 'Architecture check failed.' }
    & dotnet build (Join-Path $repository 'PortKill.Server.slnx') -c Release --nologo 2>&1 | Tee-Object -FilePath $buildLog -Append
    if ($LASTEXITCODE -ne 0) { throw 'Server build failed.' }

    $backendResult = Join-Path $evidence 'backend-performance.json'
    $backendLog = Join-Path $evidence 'backend-performance.log'
    $previousOutput = $env:PORTKILL_PERF_OUTPUT
    $env:PORTKILL_PERF_OUTPUT = $backendResult
    try {
        & dotnet test (Join-Path $repository 'tests/PortKiller.UpdateAdmin.Tests') -c Release --nologo --filter 'Category=Performance' `
            --logger 'trx;LogFileName=p8-performance.trx' --results-directory $evidence 2>&1 | Tee-Object -FilePath $backendLog
        if ($LASTEXITCODE -ne 0) { throw '后台性能测试失败。' }
    } finally {
        if ($null -eq $previousOutput) { Remove-Item Env:PORTKILL_PERF_OUTPUT -ErrorAction SilentlyContinue }
        else { $env:PORTKILL_PERF_OUTPUT = $previousOutput }
    }
    if (-not (Test-Path -LiteralPath $backendResult)) { throw '后台性能测试没有生成 JSON 报告。' }
    $backend = Get-Content -LiteralPath $backendResult -Raw | ConvertFrom-Json

    $regressionLog = Join-Path $evidence 'client-discovery-performance.log'
    & dotnet run --project (Join-Path $repository 'tests/PortKiller.UpdateRegression') -c Release --no-launch-profile -- --perf-only 2>&1 |
        Tee-Object -FilePath $regressionLog
    if ($LASTEXITCODE -ne 0) { throw '客户端发现性能回归失败。' }
    $regressionText = Get-Content -LiteralPath $regressionLog -Raw
    $discovery = [regex]::Match($regressionText, 'METRIC discovery n=(?<count>\d+) loopback P95=(?<p95>[0-9.]+)ms max=(?<max>[0-9.]+)ms')
    $manual = [regex]::Match($regressionText, 'METRIC manual check n=(?<count>\d+) cooldown-clock-advanced loopback P95=(?<p95>[0-9.]+)ms max=(?<max>[0-9.]+)ms')
    if (-not $discovery.Success -or -not $manual.Success) { throw '客户端性能回归没有输出完整的分位数指标。' }

    $result = [ordered]@{
        task = 'P8-02'
        phase = 'P8'
        status = 'passed'
        summaryType = 'controlled-performance-capacity'
        recordedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        platform = [Runtime.InteropServices.RuntimeInformation]::OSDescription
        runtime = [Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
        machine = [Environment]::MachineName
        command = 'pwsh -NoProfile -File scripts/checks/Test-AdminPerformance.ps1'
        backend = $backend
        clientDiscovery = [ordered]@{
            samples = [int]$discovery.Groups['count'].Value
            p95Milliseconds = [double]$discovery.Groups['p95'].Value
            maxMilliseconds = [double]$discovery.Groups['max'].Value
            targetMilliseconds = 5000
            source = 'PortKiller.UpdateRegression --perf-only'
        }
        clientManualCheck = [ordered]@{
            samples = [int]$manual.Groups['count'].Value
            p95Milliseconds = [double]$manual.Groups['p95'].Value
            maxMilliseconds = [double]$manual.Groups['max'].Value
            targetMilliseconds = 2000
            source = 'PortKiller.UpdateRegression --perf-only'
        }
        methodology = [ordered]@{
            api = '100 authenticated loopback HTTPS GET requests after 10 warmups'
            sse = '30 persisted job events measured until the corresponding authenticated SSE frame'
            worker = '5 fresh Worker apphost starts until structured publisher-started log'
            uploads = '2 concurrent streamed 256 MiB package bodies; process private-memory peak delta'
            discovery = '100 loopback client discovery changes with existing 2.1s polling cadence'
        }
        evidence = @('backend-performance.json', 'backend-performance.log', 'client-discovery-performance.log', 'p8-performance.trx', 'build.log')
        limitations = @(
            '指标来自 Windows 本机隔离数据库、回环 HTTPS、测试签名材料和本地磁盘，不代表公网、Nginx 或长期容量。',
            '上传内存使用进程私有字节峰值近似，未替代 Linux cgroup、容器或生产宿主监控。',
            '客户端发现目标是受控测试目标，不是公网 SLA。'
        )
        nextTask = 'P8-03'
    }
    $result | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath (Join-Path $evidence 'result.json') -Encoding utf8
    Write-Output ('PASS P8-02 performance: backend metrics, client discovery and manual-check percentiles recorded in ' + $evidence)
} finally { Pop-Location }
