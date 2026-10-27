#requires -Version 7.0
[CmdletBinding()]
param([string]$VcVarsAllPath,[switch]$ServerOnly,[switch]$SkipPerformance,[switch]$Clean,[string]$OutputDirectory)
$ErrorActionPreference='Stop'
$repository=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
if (-not $OutputDirectory) { $OutputDirectory=Join-Path $repository ('.local/artifacts/update-validation/run-'+[DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmss')) }
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($OutputDirectory)|Out-Null
Start-Transcript -Path (Join-Path $OutputDirectory 'pipeline.log')|Out-Null
Push-Location -LiteralPath $repository
try {
    if ($ServerOnly) {
        & dotnet build tests/PortKiller.UpdateRegression -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw '跨平台更新构建失败。' }
    } else {
        if (-not $IsWindows) { throw '完整 AOT/UI 验证需要 Windows；服务器平台使用 -ServerOnly。' }
        & (Join-Path $PSScriptRoot 'Build.ps1') -Configuration Release -IncludeUi
    }
    $testArgs=@()
    if (-not $ServerOnly) {
        if (-not $VcVarsAllPath) { throw '请提供 Windows AOT 的 VcVarsAllPath。' }
        $toolchain=(Resolve-Path -LiteralPath $VcVarsAllPath).Path
        if ($toolchain.IndexOfAny([char[]]'"&|<>%') -ge 0) { throw '工具链路径格式错误。' }
        $environmentLines=& $env:ComSpec /d /c "call `"$toolchain`" amd64 >nul && set"
        if ($LASTEXITCODE -ne 0) { throw 'MSVC 初始化失败。' }
        foreach ($line in $environmentLines) { $position=$line.IndexOf('='); if ($position -gt 0) { [Environment]::SetEnvironmentVariable($line.Substring(0,$position),$line.Substring($position+1),'Process') } }
        $desktop=Join-Path $OutputDirectory 'desktop-aot'
        & (Join-Path $PSScriptRoot 'Publish-Aot.ps1') -OutputDirectory $desktop -Version 0.1.1 -VcVarsAllPath $toolchain
        foreach ($entry in @(@{Version='8.1.0';Name='probe-v1'},@{Version='8.2.0';Name='probe-v2'})) {
            $probe=Join-Path $OutputDirectory $entry.Name
            & dotnet publish tests/PortKiller.UpdateProbe -c Release -r win-x64 "-p:Version=$($entry.Version)" -p:IlcUseEnvironmentalTools=true -o $probe --nologo
            if ($LASTEXITCODE -ne 0) { throw 'AOT 测试宿主构建失败。' }
            foreach ($name in @('av_libglesv2.dll','libHarfBuzzSharp.dll','libSkiaSharp.dll')) { Copy-Item -LiteralPath (Join-Path $desktop $name) -Destination $probe }
        }
        $baselines=Get-Content -LiteralPath (Join-Path $repository 'tests/PortKiller.UpdateRegression/published-update-baselines.json') -Raw | ConvertFrom-Json
        if (@($baselines).Count -eq 0) { throw '缺少已发布的历史更新基线。' }
        foreach ($baseline in $baselines) {
            $legacyRoot=Join-Path $OutputDirectory ('probe-historical-'+$baseline.ReleaseVersion)
            & (Join-Path $PSScriptRoot 'Publish-LegacyUpdateProbe.ps1') -OutputDirectory $legacyRoot -VcVarsAllPath $toolchain -BaselineVersion $baseline.ReleaseVersion
            $legacyProbe=Join-Path $legacyRoot 'publish'
            foreach ($name in @('av_libglesv2.dll','libHarfBuzzSharp.dll','libSkiaSharp.dll')) { Copy-Item -LiteralPath (Join-Path $desktop $name) -Destination $legacyProbe }
            $testArgs+=@('--historical-aot',$legacyProbe,(Join-Path $OutputDirectory 'probe-v1'),(Join-Path $OutputDirectory 'probe-v2'),$baseline.Receipt)
        }
        $prepared=Join-Path $OutputDirectory 'publisher-probe'; [IO.Directory]::CreateDirectory($prepared)|Out-Null
        Copy-Item -LiteralPath (Join-Path $OutputDirectory 'probe-v2/PortKiller.UpdateProbe.exe') -Destination (Join-Path $prepared 'PortKiller.exe')
        foreach ($name in @('av_libglesv2.dll','libHarfBuzzSharp.dll','libSkiaSharp.dll')) { Copy-Item -LiteralPath (Join-Path $desktop $name) -Destination $prepared }
        $testArgs+=@('--aot',(Join-Path $OutputDirectory 'probe-v1'),(Join-Path $OutputDirectory 'probe-v2'),'--publisher',$prepared)
    }
    $runner=Join-Path $OutputDirectory 'runner'
    & dotnet build tests/PortKiller.UpdateRegression -c Release -o $runner --nologo
    if ($LASTEXITCODE -ne 0) { throw '隔离验证入口构建失败。' }
    $testArgs=@((Join-Path $runner 'PortKiller.UpdateRegression.dll'))+$testArgs
    if (-not $SkipPerformance) { $testArgs+='--perf' }
    & dotnet @testArgs
    if ($LASTEXITCODE -ne 0) { throw '更新自动化验证失败。' }
    $serverOutput = Join-Path $OutputDirectory 'server-linux-x64'
    if ($Clean) {
        & (Join-Path $PSScriptRoot 'Publish-UpdateServer.ps1') -Runtime 'linux-x64' -OutputDirectory $serverOutput -Clean
    } else {
        & (Join-Path $PSScriptRoot 'Publish-UpdateServer.ps1') -Runtime 'linux-x64' -OutputDirectory $serverOutput
    }
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'result.json'),(@{Passed=$true;Utc=[DateTimeOffset]::UtcNow.ToString('O');OS=[Environment]::OSVersion.VersionString;SDK=(& dotnet --version);ServerOnly=[bool]$ServerOnly;LegacyAot=(-not $ServerOnly);Performance=(-not $SkipPerformance);Commit=(& git rev-parse HEAD);Dirty=(@(& git status --porcelain).Count -gt 0)}|ConvertTo-Json))
    Write-Output "验证通过，日志：$OutputDirectory"
} finally { Pop-Location; Stop-Transcript|Out-Null }
