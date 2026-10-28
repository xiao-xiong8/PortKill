#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PreparedPublishDirectory,
    [Parameter(Mandatory)][string]$VcVarsAllPath,
    [string]$Pnpm = 'pnpm.cmd'
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw '真实安装联动验收需要 Windows Native AOT。' }
$repository = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$prepared = (Resolve-Path -LiteralPath $PreparedPublishDirectory).Path
$evidence = Join-Path $repository '.local/artifacts/update-closed-loop'
[IO.Directory]::CreateDirectory($evidence) | Out-Null
$native = & (Join-Path $repository 'scripts/release/Get-AotArtifactInfo.ps1') -ExecutablePath (Join-Path $prepared 'PortKiller.exe')
$toolchain = (Resolve-Path -LiteralPath $VcVarsAllPath).Path
if ($toolchain.IndexOfAny([char[]]'"&|<>%') -ge 0) { throw '工具链路径包含不支持的命令字符。' }
$environmentLines = & $env:ComSpec /d /c "call `"$toolchain`" amd64 >nul && set"
if ($LASTEXITCODE -ne 0) { throw 'MSVC 初始化失败。' }
foreach ($line in $environmentLines) {
    $position = $line.IndexOf('=')
    if ($position -gt 0) { [Environment]::SetEnvironmentVariable($line.Substring(0, $position), $line.Substring($position + 1), 'Process') }
}
$names = @('PORTKILL_BROWSER_AOT_DIRECTORY', 'PORTKILL_BROWSER_OLD_PROBE_DIRECTORY', 'PORTKILL_BROWSER_NEW_PROBE_DIRECTORY')
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
Push-Location $repository
try {
    $probeDirectories = @()
    foreach ($probeVersion in @('8.4.0.0', '8.5.0.0')) {
        $directory = Join-Path $evidence "probe-$probeVersion"
        & dotnet publish tests/PortKiller.UpdateProbe -c Release -r win-x64 "-p:Version=$probeVersion" -p:IlcUseEnvironmentalTools=true -o $directory --nologo 2>&1 |
            Tee-Object -FilePath (Join-Path $evidence "probe-$probeVersion-build.log")
        if ($LASTEXITCODE -ne 0) { throw "AOT 测试客户端构建失败：$probeVersion" }
        foreach ($name in @('av_libglesv2.dll', 'libHarfBuzzSharp.dll', 'libSkiaSharp.dll')) {
            Copy-Item -LiteralPath (Join-Path $prepared $name) -Destination (Join-Path $directory $name)
        }
        $probeDirectories += $directory
    }
    $env:PORTKILL_BROWSER_AOT_DIRECTORY = $prepared
    $env:PORTKILL_BROWSER_OLD_PROBE_DIRECTORY = $probeDirectories[0]
    $env:PORTKILL_BROWSER_NEW_PROBE_DIRECTORY = $probeDirectories[1]
    & $Pnpm --dir src/Web/PortKiller.UpdateAdmin.Web run test:e2e 2>&1 | Tee-Object -FilePath (Join-Path $evidence 'browser.log')
    if ($LASTEXITCODE -ne 0) { throw 'Web、后端与 Native AOT 客户端联动失败。' }
    $runtime = Join-Path $repository '.local/artifacts/admin-browser/runtime'
    $outcomes = Get-Content -LiteralPath (Join-Path $runtime 'client-integration-result.json') -Raw | ConvertFrom-Json
    if (@($outcomes).Count -ne 2 -or (@($outcomes.result | Sort-Object) -join ',') -ne 'installed,rolled_back') {
        throw '缺少真实安装和回滚的客户端上报证据。'
    }
    [ordered]@{
        status = 'passed'; recordedUtc = [DateTimeOffset]::UtcNow.ToString('o'); baseCommit = (& git rev-parse HEAD).Trim()
        desktopArtifact = $native; browserChannel = $(if ($env:PORTKILL_TEST_BROWSER_CHANNEL) { $env:PORTKILL_TEST_BROWSER_CHANNEL } else { 'chromium' })
        browserFixture = (Get-Content -LiteralPath (Join-Path $runtime 'fixture-info.json') -Raw | ConvertFrom-Json)
        clients = @($outcomes)
        scope = '浏览器 MFA 登录、上传真实桌面 AOT、Worker 激活和公网检查，再通过网页发布测试版本，由同一更新模块的 Native AOT 测试客户端实际下载、替换、健康确认或回滚、HTTPS 上报，管理网页验证结果。'
        limitations = @('使用隔离数据库、测试签名密钥、localhost 分发和证书固定的 HTTPS 上报；不修改生产服务器。', '安装测试宿主复用桌面端更新模块，未模拟 Avalonia 窗口中的点击和渲染。')
    } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $evidence 'result.json') -Encoding utf8
    Copy-Item -LiteralPath (Join-Path $repository '.local/artifacts/admin-browser/update-closed-loop.png') -Destination (Join-Path $evidence 'update-closed-loop.png')
    Write-Output 'PASS 自动更新闭环：网页发布 → Worker → 实际分发目录 → AOT 安装/回滚 → HTTPS 版本登记 → 管理网页。'
} finally {
    Pop-Location
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
}
