[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$RunRegression,
    [switch]$IncludeUi
)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
Push-Location -LiteralPath $projectRoot
try {
    dotnet build (Join-Path $projectRoot 'PortKill.slnx') -c $Configuration -v minimal
    if ($LASTEXITCODE -ne 0) { throw "构建失败：$LASTEXITCODE" }
    if ($RunRegression -or $IncludeUi) {
        $regressionArgs = @('run', '--no-build', '--project', (Join-Path $projectRoot 'tests/PortKiller.Regression/PortKiller.Regression.csproj'), '-c', $Configuration)
        if ($IncludeUi) { $regressionArgs += @('--', '--ui') }
        & dotnet @regressionArgs
        if ($LASTEXITCODE -ne 0) { throw "回归失败：$LASTEXITCODE" }
        & dotnet run --no-build --project (Join-Path $projectRoot 'tests/PortKiller.UpdateRegression/PortKiller.UpdateRegression.csproj') -c $Configuration
        if ($LASTEXITCODE -ne 0) { throw "更新回归失败：$LASTEXITCODE" }
    }
} finally { Pop-Location }
