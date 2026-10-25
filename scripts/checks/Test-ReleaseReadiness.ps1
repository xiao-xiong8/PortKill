#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('linux-x64','linux-arm64','win-x64')]
    [string]$Runtime = 'linux-x64',
    [string]$OutputDirectory,
    [switch]$SkipBundleBuild
)

$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$evidence = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else {
    Join-Path $repository '.local/artifacts/admin-execution/P8-05'
}
New-Item -ItemType Directory -Path $evidence -Force | Out-Null

function Assert-Condition([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Read-JsonFile([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    try { return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
    catch { throw "无法读取 JSON 证据：$Path；$($_.Exception.Message)" }
}

function Get-EvidenceStatus([string]$Task) {
    $path = Join-Path $repository ".local/artifacts/admin-execution/$Task/result.json"
    $result = Read-JsonFile $path
    if ($null -eq $result) {
        return [ordered]@{ task = $Task; status = 'missing'; path = ".local/artifacts/admin-execution/$Task/result.json" }
    }
    return [ordered]@{
        task = $Task
        status = [string]$result.status
        path = ".local/artifacts/admin-execution/$Task/result.json"
        recordedUtc = [string]$result.recordedUtc
        limitations = @($result.limitations | ForEach-Object { [string]$_ })
    }
}

function Invoke-Captured([string]$File, [string[]]$CliArgs, [string]$LogPath) {
    Push-Location $repository
    try {
        & pwsh -NoProfile -File $File @CliArgs 2>&1 | Tee-Object -FilePath $LogPath
        if ($LASTEXITCODE -ne 0) { throw "命令失败 ($LASTEXITCODE)：$File $($CliArgs -join ' ')" }
    } finally { Pop-Location }
}

function Get-TrackedFiles {
    $raw = & git -C $repository ls-files -z
    if ($LASTEXITCODE -ne 0) { throw '无法读取 Git 跟踪文件。' }
    return @($raw -split "`0" | Where-Object { $_ })
}

function Test-ProductionSource {
    $tracked = @(Get-TrackedFiles)
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
                $_.FullName -notmatch '[\\/](bin|obj|node_modules|dist|\.local)[\\/]'
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
        'Other-Publisher-Only!',
        '(?i)demo(?:user|account|password)'
    )
    $matches = @()
    foreach ($pattern in $forbidden) {
        $matches += @($files | Select-String -Pattern $pattern -AllMatches)
    }
    Assert-Condition ($matches.Count -eq 0) ("生产源码命中禁止安全旁路、凭据或演示账号模式：$($matches[0].Path):$($matches[0].LineNumber)")

    return [ordered]@{
        trackedFiles = $tracked.Count
        scannedFiles = $files.Count
        sensitivePaths = $sensitivePaths.Count
        forbiddenMatches = $matches.Count
    }
}

function Test-RequiredFiles {
    $required = @(
        'deploy/contracts/admin-api.v1.json',
        'docs/ARCHITECTURE.md',
        'docs/AUTO-UPDATE.md',
        'docs/RELEASE-NOTES.txt',
        'src/Web/PortKiller.UpdateAdmin.Web/package.json',
        'src/Web/PortKiller.UpdateAdmin.Web/pnpm-lock.yaml',
        'src/Web/PortKiller.UpdateAdmin.Web/THIRD-PARTY-NOTICES.txt',
        'src/Modules/PortKiller.UpdateManagement/Persistence/Migrations/UpdateAdminDbContextModelSnapshot.cs',
        'deploy/systemd/portkiller-update-api.service',
        'deploy/systemd/portkiller-update-worker.service',
        'deploy/systemd/portkiller-update-notifier.service',
        'deploy/systemd/portkiller-update-monitor.service',
        'deploy/systemd/portkiller-update-monitor.timer',
        'deploy/nginx/portkiller-admin.conf.example',
        'deploy/nginx/portkiller-download-tls.conf.example',
        'deploy/ubuntu/install-update-server.sh',
        'deploy/ubuntu/backup-update-server.sh',
        'deploy/ubuntu/restore-update-server.sh',
        'deploy/ubuntu/upgrade-update-server.sh',
        'deploy/ubuntu/monitor-update-server.py',
        'deploy/ubuntu/monitor-update-server.sh',
        'deploy/examples/update-admin-api.env.example',
        'deploy/examples/update-admin-worker.env.example',
        'deploy/examples/update-monitor.env.example'
    )
    $missing = @($required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $repository $_) -PathType Leaf) })
    Assert-Condition ($missing.Count -eq 0) ("缺少正式交付文件：$($missing -join ', ')")

    $lock = Get-Content -LiteralPath (Join-Path $repository 'src/Web/PortKiller.UpdateAdmin.Web/pnpm-lock.yaml') -Raw
    $notices = Get-Content -LiteralPath (Join-Path $repository 'src/Web/PortKiller.UpdateAdmin.Web/THIRD-PARTY-NOTICES.txt') -Raw
    $package = Get-Content -LiteralPath (Join-Path $repository 'src/Web/PortKiller.UpdateAdmin.Web/package.json') -Raw | ConvertFrom-Json
    Assert-Condition ($lock.Length -gt 1000) '前端 pnpm-lock.yaml 为空或过短。'
    Assert-Condition ($notices.Length -gt 100) '前端第三方许可声明缺失或过短。'
    Assert-Condition ([string]$package.packageManager -match '^pnpm@') '前端 package.json 未固定 pnpm 版本。'

    $migrations = @(Get-ChildItem -LiteralPath (Join-Path $repository 'src/Modules/PortKiller.UpdateManagement/Persistence/Migrations') -Filter '*.cs' -File)
    Assert-Condition ($migrations.Count -ge 2) 'EF Core 迁移数量不足，未发现可审阅的初始迁移和模型快照。'
    return [ordered]@{ requiredFiles = $required.Count; migrations = $migrations.Count; lockBytes = $lock.Length; noticesBytes = $notices.Length; packageManager = [string]$package.packageManager }
}

function Test-Bundle([string]$BundleDirectory) {
    Assert-Condition (Test-Path -LiteralPath (Join-Path $BundleDirectory 'bundle.json') -PathType Leaf) '服务端交付包缺少 bundle.json。'
    $bundle = Read-JsonFile (Join-Path $BundleDirectory 'bundle.json')
    Assert-Condition ([string]$bundle.runtime -eq $Runtime) "服务端交付包运行时不匹配：$($bundle.runtime)"
    Assert-Condition ($bundle.nativeAot -eq $false) '服务端交付包错误启用了 Native AOT；Native AOT 仅属于桌面端。'
    foreach ($component in @('api','worker','notifier','tool','web')) {
        Assert-Condition (Test-Path -LiteralPath (Join-Path $BundleDirectory $component) -PathType Container) "交付包缺少组件目录：$component"
    }
    Assert-Condition (Test-Path -LiteralPath (Join-Path $BundleDirectory 'web/index.html') -PathType Leaf) '交付包缺少 web/index.html。'
    $files = @(Get-ChildItem -LiteralPath $BundleDirectory -Recurse -File)
    $forbidden = @($files | Where-Object {
        $_.FullName -match '[\\/]node_modules[\\/]' -or
        $_.Name -match '(?i)\.(pem|pfx|key|secret|env)$' -or
        $_.Name -match '^appsettings\.(Development|Local)\.json$' -or
        $_.Name -in @('PortKiller.exe','av_libglesv2.dll','libHarfBuzzSharp.dll','libSkiaSharp.dll')
    })
    Assert-Condition ($forbidden.Count -eq 0) ("交付包包含桌面产物、node_modules 或敏感文件：$($forbidden[0].FullName)")
    return [ordered]@{ directory = [IO.Path]::GetRelativePath($repository, $BundleDirectory).Replace('\','/'); runtime = [string]$bundle.runtime; nativeAot = [bool]$bundle.nativeAot; files = $files.Count; forbiddenFiles = 0; commit = [string]$bundle.commit; dirty = [bool]$bundle.dirty }
}

function Test-GitScope {
    $diffLog = Join-Path $evidence 'git-diff-check.log'
    & git -C $repository diff --check 2>&1 | Tee-Object -FilePath $diffLog
    if ($LASTEXITCODE -ne 0) { throw 'git diff --check failed.' }
    $status = @(& git -C $repository status --porcelain=v1)
    if ($LASTEXITCODE -ne 0) { throw '无法读取 Git 状态。' }
    $expected = @(
        'scripts/checks/Test-ReleaseReadiness.ps1',
        'docs/AUTO-UPDATE.md'
    )
    $unexpected = @($status | ForEach-Object {
        $line = [string]$_
        $path = if ($line.Length -gt 3) { $line.Substring(3).Trim().Trim('"') } else { '' }
        if ($path -and $expected -notcontains $path) { $line }
    })
    Assert-Condition ($unexpected.Count -eq 0) ("工作区存在未授权的交付范围变更：$($unexpected -join ' | ')")
    return [ordered]@{ diffCheck = $true; statusLines = @($status); expectedUncommitted = @($status); unexpectedChanges = @($unexpected); cleanAfterExpectedChanges = ($unexpected.Count -eq 0) }
}

Push-Location $repository
try {
    $evidenceStatuses = @(
        Get-EvidenceStatus 'P8-01'
        Get-EvidenceStatus 'P8-02'
        Get-EvidenceStatus 'P8-03'
        Get-EvidenceStatus 'P8-04'
    )
    foreach ($required in @('P8-01','P8-02','P8-03')) {
        $status = ($evidenceStatuses | Where-Object task -eq $required).status
        Assert-Condition ($status -eq 'passed') "$required 证据必须为 passed，当前为 $status。"
    }

    $p8_04 = $evidenceStatuses | Where-Object task -eq 'P8-04'
    $p8_04_result = Read-JsonFile (Join-Path $repository '.local/artifacts/admin-execution/P8-04/result.json')
    $firstInstallReady = $false
    $oldKeyMigrationReady = $false
    if ($null -ne $p8_04_result -and $null -ne $p8_04_result.gates) {
        $firstInstallReady = [bool]$p8_04_result.gates.firstInstallAuthenticode.ready
        $oldKeyMigrationReady = [bool]$p8_04_result.gates.oldClientKeyMigration.ready
    }

    $sourceChecks = Test-ProductionSource
    $fileChecks = Test-RequiredFiles
    Invoke-Captured -File (Join-Path $repository 'scripts/checks/Test-Architecture.ps1') -CliArgs @() -LogPath (Join-Path $evidence 'architecture.log')
    Invoke-Captured -File (Join-Path $repository 'scripts/checks/Test-AdminContract.ps1') -CliArgs @() -LogPath (Join-Path $evidence 'contract.log')

    $bundleDirectory = $null
    if (-not $SkipBundleBuild) {
        $bundleDirectory = Join-Path $evidence ("admin-bundle-$Runtime-" + [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ'))
        Invoke-Captured -File (Join-Path $repository 'scripts/checks/Test-AdminBundle.ps1') -CliArgs @('-Runtime', $Runtime, '-OutputDirectory', $bundleDirectory) -LogPath (Join-Path $evidence 'admin-bundle.log')
    } else {
        $bundleDirectory = Get-ChildItem -LiteralPath (Join-Path $repository '.local/artifacts/admin-execution') -Directory -Filter 'P8-05' -ErrorAction SilentlyContinue |
            ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Directory -Filter "admin-bundle-$Runtime-*" -ErrorAction SilentlyContinue } |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
        Assert-Condition $bundleDirectory '使用 -SkipBundleBuild 时没有找到既有 P8-05 服务端交付包。'
    }
    $bundleChecks = Test-Bundle $bundleDirectory

    $gitChecks = Test-GitScope
    $windowsOnly = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)
    $linuxRuntimeReady = -not $windowsOnly
    $linuxReason = if ($linuxRuntimeReady) { '本次运行环境为 Linux；仍需执行 test-admin-linux.sh 的实机证据。' } else { '当前运行环境为 Windows；没有把交叉构建或模板检查当作 Linux 运行验收。' }

    $blockers = [System.Collections.Generic.List[string]]::new()
    if (-not $firstInstallReady) { $blockers.Add('P8-04 首装 Authenticode/可信发布者条件未具备。') }
    if (-not $oldKeyMigrationReady) { $blockers.Add('P8-04 旧客户端 PublicKeyPem 轮换迁移机制未具备。') }
    if (-not $linuxRuntimeReady) { $blockers.Add('真实 Linux/systemd/SQLite 权限与服务运行验收尚未执行。') }
    $blockers.Add('真实 Nginx、生产 TLS、域名和公网下载/SSE/Range 验收尚未执行。')
    $overall = if ($blockers.Count -eq 0) { 'passed' } else { 'blocked' }

    $result = [ordered]@{
        task = 'P8-05'
        phase = 'P8'
        status = $overall
        summaryType = 'release-readiness-and-production-preflight'
        recordedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        platform = [Runtime.InteropServices.RuntimeInformation]::OSDescription
        runtime = [Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
        baseCommit = (& git rev-parse HEAD).Trim()
        command = 'pwsh -NoProfile -File scripts/checks/Test-ReleaseReadiness.ps1'
        evidence = [ordered]@{ prior = @($evidenceStatuses); source = $sourceChecks; requiredFiles = $fileChecks; bundle = $bundleChecks; git = $gitChecks; architecture = 'passed'; contract = 'passed' }
        gates = [ordered]@{
            p8_04 = [ordered]@{ status = [string]$p8_04.status; firstInstallAuthenticode = $firstInstallReady; oldClientKeyMigration = $oldKeyMigrationReady }
            linuxRuntime = [ordered]@{ ready = $linuxRuntimeReady; reason = $linuxReason; testEntryPoint = 'scripts/checks/test-admin-linux.sh' }
            nginxTlsPublicInternet = [ordered]@{ ready = $false; reason = '仅有模板和本机回环证据；尚未在目标服务器使用真实 Nginx、证书、域名和公网路径。' }
            signingPrivateKey = [ordered]@{ includedInBundle = $false; location = 'Windows signing environment only' }
            desktopAot = [ordered]@{ serverBundleNativeAot = $false; desktopEvidence = [string](($evidenceStatuses | Where-Object task -eq 'P8-03').status) }
        }
        blockers = @($blockers)
        limitations = @(
            '本检查器只审查源码、可复现交付包、模板和已有证据，不会推送 Git 或部署生产服务器。',
            'P8-04 的隔离 RSA 演练通过，但真实 Authenticode 首装信任和旧客户端公钥迁移仍未完成。',
            '当前 Windows 会话不能替代真实 Linux/systemd/Nginx/生产 TLS/公网验收。'
        )
        nextTask = if ($blockers.Count -eq 0) { '明确授权后执行专用 Linux/公网验收，再安排生产部署。' } else { '补齐 blockers 后重新运行 P8-05；不要把此报告标记为生产上线通过。' }
    }
    $result | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $evidence 'result.json') -Encoding utf8
    if ($overall -eq 'passed') { Write-Output 'PASS P8-05 release readiness: source, bundle, deployment inputs and prior evidence are complete.' }
    else { Write-Output ('BLOCKED P8-05 release readiness: ' + ($blockers -join ' | ')) }
} finally {
    Pop-Location
}
