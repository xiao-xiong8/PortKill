#requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path

function Read-EvaluatedVersion([string]$ProjectPath) {
    $output = & dotnet msbuild $ProjectPath '-getProperty:Version' '-nologo' 2>&1
    if ($LASTEXITCODE -ne 0) { throw "无法读取项目版本：$ProjectPath" }
    $value = [string](@($output | ForEach-Object { $_.ToString().Trim() } | Where-Object { $_ -match '^\d+\.\d+(\.\d+){0,3}$' } | Select-Object -Last 1))
    if ([string]::IsNullOrWhiteSpace([string]$value)) { throw "项目版本为空：$ProjectPath" }
    return [version]$value
}

function Assert-Condition([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

[xml]$props = Get-Content -LiteralPath (Join-Path $repository 'Directory.Build.props') -Raw
$productVersion = [string]$props.SelectSingleNode('/Project/PropertyGroup/ProductVersion').InnerText
Assert-Condition ($productVersion -match '^\d+\.\d+\.\d+$') "ProductVersion 必须是三段正式版本：$productVersion"

$desktopProject = Join-Path $repository 'src/PortKiller.Desktop/PortKiller.Desktop.csproj'
$adminProject = Join-Path $repository 'src/Services/PortKiller.UpdateAdmin.Api/PortKiller.UpdateAdmin.Api.csproj'
$desktopVersion = Read-EvaluatedVersion $desktopProject
$adminVersion = Read-EvaluatedVersion $adminProject
Assert-Condition ($desktopVersion.ToString(3) -eq $productVersion) "桌面项目版本 $desktopVersion 与 ProductVersion $productVersion 不一致。"
Assert-Condition ($adminVersion.ToString(3) -eq $productVersion) "后台项目版本 $adminVersion 与 ProductVersion $productVersion 不一致。"

$desktopText = Get-Content -LiteralPath $desktopProject -Raw
Assert-Condition ($desktopText -notmatch '<Version>') '桌面项目不能单独声明 Version。'
$adminText = Get-Content -LiteralPath (Join-Path $repository 'src/Modules/PortKiller.UpdateManagement/AdminApplication.cs') -Raw
Assert-Condition ($adminText -notmatch 'Version\s*=\s*"\d+\.\d+') 'AdminApplication 不能硬编码产品版本。'

Write-Output "PASS version consistency: ProductVersion=$productVersion; desktop=$($desktopVersion.ToString(3)); admin=$($adminVersion.ToString(3))"
