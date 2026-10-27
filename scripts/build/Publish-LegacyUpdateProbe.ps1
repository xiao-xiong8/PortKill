#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory, [Parameter(Mandatory)][string]$VcVarsAllPath,
    [string]$BaselineVersion = '0.1.1')
$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
# 固定已发布源码，不能替换为当前源码冒充旧版。
$baselines = Get-Content -LiteralPath (Join-Path $repository 'tests/PortKiller.UpdateRegression/published-update-baselines.json') -Raw | ConvertFrom-Json
$entry = @($baselines | Where-Object ReleaseVersion -EQ $BaselineVersion)
if ($entry.Count -ne 1 -or $entry[0].Commit -notmatch '^[a-f0-9]{40}$' -or $entry[0].ProbeVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw '历史更新基线不存在或记录不合法。'
}
$entry = $entry[0]; $baseline = $entry.Commit
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw '历史测试输出目录必须是新目录，避免误用旧二进制。' }
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$source = Join-Path $OutputDirectory 'source'
$archive = Join-Path $OutputDirectory 'baseline.zip'
& git -C $repository archive --format=zip --output $archive $baseline -- Directory.Build.props global.json src/Modules/PortKiller.Updates src/Modules/PortKiller.UpdateProtocol tests/PortKiller.UpdateProbe
if ($LASTEXITCODE -ne 0) { throw "无法读取历史更新基线 $baseline，请获取完整 Git 历史后重试，不能跳过旧版本兼容验收。" }
Expand-Archive -LiteralPath $archive -DestinationPath $source
$toolchain = (Resolve-Path -LiteralPath $VcVarsAllPath).Path
if ($toolchain.IndexOfAny([char[]]'"&|<>%') -ge 0) { throw '工具链路径格式错误。' }
$vcRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $toolchain))
# 多基线连续发布时复用同一套 x64 工具链，重复调用 vcvarsall 会不断扩张 PATH 并突破 cmd 长度限制。
if (-not $env:VCINSTALLDIR -or $env:VCINSTALLDIR.TrimEnd('\','/') -ine $vcRoot -or
    $env:VSCMD_ARG_TGT_ARCH -ne 'x64' -or $env:VSCMD_ARG_HOST_ARCH -ne 'x64' -or
    -not (Get-Command cl.exe -ErrorAction SilentlyContinue)) {
    $environmentLines = & $env:ComSpec /d /c "call `"$toolchain`" amd64 >nul && set"
    if ($LASTEXITCODE -ne 0) { throw 'MSVC 初始化失败。' }
    foreach ($line in $environmentLines) {
        $position = $line.IndexOf('=')
        if ($position -gt 0) { [Environment]::SetEnvironmentVariable($line.Substring(0, $position), $line.Substring($position + 1), 'Process') }
    }
}
$probe = Join-Path $OutputDirectory 'publish'
# 8.x 仅用于隔离测试；历史源码和历史安装协议保持原样。
& dotnet publish (Join-Path $source 'tests/PortKiller.UpdateProbe') -c Release -r win-x64 "-p:Version=$($entry.ProbeVersion)" -p:IlcUseEnvironmentalTools=true -o $probe --nologo
if ($LASTEXITCODE -ne 0) { throw '历史 AOT 测试宿主构建失败。' }
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'baseline.json'), ($entry | ConvertTo-Json))
Write-Output "历史安装器 AOT 测试宿主：$probe；源码：$baseline"
