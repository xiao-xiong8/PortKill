#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('linux-x64','linux-arm64','win-x64')][string]$Runtime='linux-x64',
    [string]$OutputDirectory,
    [switch]$Clean
)
$ErrorActionPreference='Stop'
$repository=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
if (-not $OutputDirectory) { $OutputDirectory=Join-Path $repository ".local/artifacts/update-server/$Runtime" }
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) {
    $existing=@(Get-ChildItem -LiteralPath $OutputDirectory -Force)
    if ($existing.Count -gt 0) {
        if (-not $Clean) { throw "输出目录不为空；请指定新的目录或显式使用 -Clean：$OutputDirectory" }
        Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
    }
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

function Publish-ServerComponent([hashtable]$Component) {
    $destination=Join-Path $OutputDirectory $Component.Name
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    # Server components stay self-contained CoreCLR applications; Native AOT is a desktop-only concern.
    & dotnet publish (Join-Path $repository "src/$($Component.Project)") -c Release -r $Runtime --self-contained true -p:PublishAot=false -p:DebugType=None -p:DebugSymbols=false -o $destination --nologo
    if ($LASTEXITCODE -ne 0) { throw "服务端发布失败：$($Component.Name)" }
}

foreach ($component in @(
    @{Name='api';Project='Services/PortKiller.UpdateAdmin.Api'},
    @{Name='worker';Project='Services/PortKiller.UpdatePublisher.Worker'},
    @{Name='notifier';Project='Services/PortKiller.UpdateNotifier'},
    @{Name='tool';Project='Tools/PortKiller.UpdateTool'}
)) { Publish-ServerComponent $component }

$webProject=Join-Path $repository 'src/Web/PortKiller.UpdateAdmin.Web'
& (Join-Path $repository 'scripts/build/Build-Web.ps1') -Check
if ($LASTEXITCODE -ne 0) { throw '管理前端构建失败' }
$webOutput=Join-Path $OutputDirectory 'web'; New-Item -ItemType Directory -Path $webOutput -Force | Out-Null
foreach ($entry in @(Get-ChildItem -LiteralPath (Join-Path $webProject 'dist') -Force)) {
    Copy-Item -LiteralPath $entry.FullName -Destination $webOutput -Recurse -Force
}
if (-not (Test-Path -LiteralPath (Join-Path $webOutput 'index.html'))) { throw '管理前端缺少 index.html' }

$privateFiles=@(Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File | Where-Object {
    $_.Name -match '\.(pem|pfx|key|secret|env)$' -or $_.Name -match '^appsettings\.(Development|Local)\.json$'
})
if ($privateFiles.Count -gt 0) { throw "交付包包含本地或私钥文件：$($privateFiles[0].FullName)" }

$versionSource=Get-Content -Raw -LiteralPath (Join-Path $repository 'src/Modules/PortKiller.UpdateManagement/AdminApplication.cs')
$version=[regex]::Match($versionSource,'Version\s*=\s*"(?<version>[^"]+)"').Groups['version'].Value
if ([string]::IsNullOrWhiteSpace($version)) { throw '无法读取管理后台版本' }
$commit=(& git -C $repository rev-parse HEAD).Trim()
$dirty=@(& git -C $repository status --porcelain)
$files=@(Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File | Where-Object { $_.Name -ne 'bundle.json' } | ForEach-Object {
    [ordered]@{ path=[IO.Path]::GetRelativePath($OutputDirectory,$_.FullName).Replace('\','/'); bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$bundle=[ordered]@{
    schema=1; application='PortKill Update Administration'; version=$version; runtime=$Runtime; selfContained=$true; nativeAot=$false
    commit=$commit; dirty=($dirty.Count -gt 0); contractVersion=1
    components=@(
        [ordered]@{ name='api'; path='api'; entrypoint='PortKiller.UpdateAdmin.Api'; nativeAot=$false },
        [ordered]@{ name='worker'; path='worker'; entrypoint='PortKiller.UpdatePublisher.Worker'; nativeAot=$false },
        [ordered]@{ name='notifier'; path='notifier'; entrypoint='PortKiller.UpdateNotifier'; nativeAot=$false },
        [ordered]@{ name='tool'; path='tool'; entrypoint='PortKiller.UpdateTool'; nativeAot=$false },
        [ordered]@{ name='web'; path='web'; staticOnly=$true; requiresNodeRuntime=$false }
    )
    configuration=[ordered]@{ dataRoot='external absolute path'; dataProtectionPath='external protected path'; updateTool='external config or environment'; signingPrivateKey='never included' }
    files=$files
}
$bundle | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'bundle.json') -Encoding utf8
Write-Output "服务端自包含产物：$OutputDirectory"
