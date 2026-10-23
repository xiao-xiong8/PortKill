[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [string]$VcVarsAllPath,
    [string]$Version
)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'artifacts/publish/win-x64' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ($Version -and $Version -notmatch '^\d+\.\d+(\.\d+){0,2}$') { throw '仅支持两至四段数字正式版本。' }
$publishArgs = @('publish', (Join-Path $projectRoot 'src/PortKiller.Desktop/PortKiller.Desktop.csproj'), '-c', 'Release', '-r', 'win-x64', '-o', $OutputDirectory, '-v', 'minimal')
if ($Version) { $publishArgs += "-p:Version=$Version" }
if ($VcVarsAllPath) {
    $toolchainPath = (Resolve-Path -LiteralPath $VcVarsAllPath).Path
    if ($toolchainPath.IndexOfAny([char[]]'"&|<>%') -ge 0) { throw '工具链路径包含不支持的命令字符。' }
    $environmentLines = & $env:ComSpec /d /c "call `"$toolchainPath`" amd64 >nul && set"
    if ($LASTEXITCODE -ne 0) { throw "MSVC 环境初始化失败：$LASTEXITCODE" }
    foreach ($line in $environmentLines) {
        $separator = $line.IndexOf('=')
        if ($separator -gt 0) { [Environment]::SetEnvironmentVariable($line.Substring(0, $separator), $line.Substring($separator + 1), 'Process') }
    }
    $publishArgs += '-p:IlcUseEnvironmentalTools=true'
}
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "AOT 发布失败：$LASTEXITCODE" }
foreach ($name in @('PortKiller.exe', 'av_libglesv2.dll', 'libHarfBuzzSharp.dll', 'libSkiaSharp.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $OutputDirectory $name))) { throw "发布缺少文件：$name" }
}
Write-Output "AOT 发布完成：$OutputDirectory"
