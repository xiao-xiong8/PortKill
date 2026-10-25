#requires -Version 7.0
[CmdletBinding()]
param([string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$evidence = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else {
    Join-Path $repository '.local/artifacts/admin-execution/P8-01'
}
New-Item -ItemType Directory -Path $evidence -Force | Out-Null

function Invoke-Captured([string]$File, [string[]]$CliArgs, [string]$LogPath) {
    Push-Location $repository
    try {
        & pwsh -NoProfile -File $File @CliArgs 2>&1 | Tee-Object -FilePath $LogPath
        if ($LASTEXITCODE -ne 0) { throw "命令失败 ($LASTEXITCODE): $File $($CliArgs -join ' ')" }
    } finally { Pop-Location }
}

function Assert-Condition([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Test-ProductionSource {
    $tracked = @((& git -C $repository ls-files -z) -split "`0" | Where-Object { $_ })
    $sensitivePaths = @($tracked | Where-Object {
        $_ -match '(?i)(^|/)([^/]+\.)?(pem|pfx|key|secret)$' -or
        $_ -match '(?i)(^|/)appsettings\.(development|local)\.json$' -or
        $_ -match '(?i)(^|/)(\.env|[^/]+\.env)$'
    })
    Assert-Condition ($sensitivePaths.Count -eq 0) ("源码跟踪了敏感文件：$($sensitivePaths -join ', ')")

    $roots = @(
        (Join-Path $repository 'src/Services'),
        (Join-Path $repository 'src/Modules/PortKiller.UpdateManagement'),
        (Join-Path $repository 'src/Web/PortKiller.UpdateAdmin.Web/src'),
        (Join-Path $repository 'deploy'),
        (Join-Path $repository 'scripts/build')
    )
    $files = @($roots | ForEach-Object {
        if (Test-Path -LiteralPath $_) {
            Get-ChildItem -LiteralPath $_ -Recurse -File | Where-Object {
                $_.FullName -notmatch '[\\/](bin|obj|node_modules|dist)[\\/]'
            }
        }
    })
    $forbidden = @(
        'BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY',
        'UseDeveloperExceptionPage',
        'AddDeveloperExceptionPage',
        '(?i)(?:Skip|Bypass|Disable)(?:Authentication|Authorization)',
        '(?i)TestOnlyAuthentication',
        'Test-Only-Password!',
        'Viewer-Only-Password!',
        'Other-Publisher-Only!'
    )
    foreach ($pattern in $forbidden) {
        $matches = @($files | Select-String -Pattern $pattern -AllMatches)
        Assert-Condition ($matches.Count -eq 0) ("生产源码命中禁止安全旁路或凭据模式 '$pattern'：$($matches[0].Path):$($matches[0].LineNumber)")
    }

    $storedSecrets = @($files | Select-String -Pattern '(?i)(localStorage|sessionStorage).*\b(token|cookie|secret|privateKey)\b' -AllMatches)
    Assert-Condition ($storedSecrets.Count -eq 0) ("前端或部署源码疑似持久化认证材料：$($storedSecrets[0].Path):$($storedSecrets[0].LineNumber)")
    return [ordered]@{
        trackedFiles = $tracked.Count
        scannedFiles = $files.Count
        sensitivePaths = 0
        forbiddenPatterns = 0
        storedSecrets = 0
    }
}

Push-Location $repository
try {
    $adminLog = Join-Path $evidence 'admin-browser-check.log'
    $previousBrowserChannel = $env:PORTKILL_TEST_BROWSER_CHANNEL
    if ($IsWindows -and -not $previousBrowserChannel -and (Test-Path -LiteralPath (Join-Path ${env:ProgramFiles} 'Google/Chrome/Application/chrome.exe'))) {
        # The bundled browser can fail to spawn in the desktop sandbox; installed Chrome is equivalent for this local check.
        $env:PORTKILL_TEST_BROWSER_CHANNEL = 'chrome'
    }
    try {
        Invoke-Captured -File (Join-Path $repository 'scripts/checks/Test-Admin.ps1') -CliArgs @('-IncludeBrowser') -LogPath $adminLog
    } finally {
        if ($null -eq $previousBrowserChannel) { Remove-Item Env:PORTKILL_TEST_BROWSER_CHANNEL -ErrorAction SilentlyContinue }
        else { $env:PORTKILL_TEST_BROWSER_CHANNEL = $previousBrowserChannel }
    }

    $trxPath = Join-Path $repository '.local/artifacts/admin-tests/latest/admin-tests.trx'
    Assert-Condition (Test-Path -LiteralPath $trxPath) '后台测试没有生成 TRX 证据。'
    [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
    $counters = $trx.TestRun.ResultSummary.Counters
    $matrixPath = Join-Path $repository 'tests/PortKiller.UpdateAdmin.Tests/P8SecurityMatrix.json'
    $matrix = Get-Content -LiteralPath $matrixPath -Raw | ConvertFrom-Json
    Assert-Condition ([int]$counters.total -ge [int]$matrix.minimumBackendTests) 'P8 安全矩阵的后台测试发现数量不足。'
    Assert-Condition ([int]$counters.failed -eq 0 -and [int]$counters.notExecuted -eq 0 -and [int]$counters.total -eq [int]$counters.passed) 'P8 安全矩阵存在失败或跳过的后台测试。'

    $coverage = foreach ($group in @($matrix.groups)) {
        $groupResults = foreach ($prefix in @($group.tests)) {
            $matched = @($trx.TestRun.Results.UnitTestResult | Where-Object {
                ([string]$_.testName).Contains([string]$prefix, [StringComparison]::Ordinal)
            })
            Assert-Condition ($matched.Count -gt 0) ("P8 安全矩阵缺少测试发现：$prefix")
            Assert-Condition (@($matched | Where-Object outcome -ne 'Passed').Count -eq 0) ("P8 安全矩阵测试失败：$prefix")
            foreach ($test in $matched) {
                [ordered]@{ name = [string]$test.testName; outcome = [string]$test.outcome; duration = [string]$test.duration }
            }
        }
        [ordered]@{
            id = [string]$group.id
            requirement = [string]$group.requirement
            passed = $true
            tests = @($groupResults)
            browserChecks = @($group.browserChecks)
        }
    }

    $bundleDirectory = Join-Path $evidence 'admin-bundle-linux-x64'
    $bundleLog = Join-Path $evidence 'admin-bundle.log'
    Invoke-Captured -File (Join-Path $repository 'scripts/checks/Test-AdminBundle.ps1') -CliArgs @('-Runtime', 'linux-x64', '-OutputDirectory', $bundleDirectory) -LogPath $bundleLog
    $sourceChecks = Test-ProductionSource

    $diffLog = Join-Path $evidence 'git-diff-check.log'
    & git -C $repository diff --check 2>&1 | Tee-Object -FilePath $diffLog
    if ($LASTEXITCODE -ne 0) { throw 'git diff --check failed.' }

    $result = [ordered]@{
        task = 'P8-01'
        phase = 'P8'
        status = 'passed'
        summaryType = 'security-verification-matrix'
        recordedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        platform = [Runtime.InteropServices.RuntimeInformation]::OSDescription
        baseCommit = (& git -C $repository rev-parse HEAD).Trim()
        command = 'pwsh -NoProfile -File scripts/checks/Test-AdminSecurity.ps1'
        backendTests = [ordered]@{
            total = [int]$counters.total
            passed = [int]$counters.passed
            failed = [int]$counters.failed
            skipped = [int]$counters.notExecuted
            trx = 'admin-tests.trx'
        }
        browserChecks = 'Test-Admin.ps1 -IncludeBrowser completed successfully; see admin-browser-check.log.'
        package = [ordered]@{
            runtime = 'linux-x64'
            validation = 'Test-AdminBundle.ps1 passed'
            directory = 'admin-bundle-linux-x64'
            sensitiveFiles = 'none detected'
        }
        sourceChecks = $sourceChecks
        matrix = @($coverage)
        evidence = @('admin-browser-check.log', 'admin-tests.trx', 'admin-bundle.log', 'git-diff-check.log')
        limitations = @(
            '本次 P8-01 证据在 Windows 隔离数据库、回环 HTTPS、测试密钥和自包含 linux-x64 交付包上执行。',
            '真实 Linux systemd/Nginx/生产 TLS/公网路径仍需专用服务器按 test-admin-linux.sh 执行；本报告不把交叉构建当作运行验收。',
            '签名私钥和生产账号未用于本次测试。'
        )
        nextTask = 'P8-02'
    }
    $result | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath (Join-Path $evidence 'result.json') -Encoding utf8
    Write-Output ('PASS P8-01 security matrix: ' + $counters.passed + ' backend tests, ' + $coverage.Count + ' groups, browser flow and bundle checks passed.')
} finally { Pop-Location }
