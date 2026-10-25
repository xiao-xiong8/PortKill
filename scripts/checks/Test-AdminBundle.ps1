#requires -Version 7.0
[CmdletBinding()]
param([ValidateSet('linux-x64','linux-arm64','win-x64')][string]$Runtime='linux-x64',[string]$OutputDirectory)
$ErrorActionPreference='Stop'
$repository=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
if (-not $OutputDirectory) { $OutputDirectory=Join-Path $repository ('.local/artifacts/admin-bundle-validation/'+$Runtime+'-'+[DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmss')) }
& (Join-Path $repository 'scripts/build/Publish-UpdateServer.ps1') -Runtime $Runtime -OutputDirectory $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw '管理后台交付包构建失败' }
$bundle=Get-Content -Raw -LiteralPath (Join-Path $OutputDirectory 'bundle.json') | ConvertFrom-Json
if ($bundle.schema -ne 1 -or $bundle.runtime -ne $Runtime -or $bundle.nativeAot -ne $false) { throw '交付包元数据不符合服务端约束' }
foreach ($component in @('api','worker','notifier','tool','web')) {
    if (-not (Test-Path -LiteralPath (Join-Path $OutputDirectory $component))) { throw "缺少交付组件：$component" }
}
if (-not (Test-Path -LiteralPath (Join-Path $OutputDirectory 'web/index.html'))) { throw '静态前端缺少 index.html' }
$forbidden=@(Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File | Where-Object {
    $_.Name -match '\.(pem|pfx|key|secret|env)$' -or $_.Name -match '^appsettings\.(Development|Local)\.json$' -or $_.FullName -match '[\\/]node_modules[\\/]' -or $_.Name -in @('PortKiller.exe','av_libglesv2.dll','libHarfBuzzSharp.dll','libSkiaSharp.dll')
})
if ($forbidden.Count -gt 0) { throw "交付包包含禁止文件：$($forbidden[0].FullName)" }
$deploymentFiles=@(
    'deploy/systemd/portkiller-update-api.service',
    'deploy/systemd/portkiller-update-worker.service',
    'deploy/systemd/portkiller-update-notifier.service',
    'deploy/ubuntu/install-update-server.sh',
    'deploy/examples/update-admin-api.env.example',
    'deploy/examples/update-admin-worker.env.example'
)
foreach ($relative in $deploymentFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $repository $relative) -PathType Leaf)) { throw "缺少部署文件：$relative" }
}
$apiUnit=Get-Content -Raw -LiteralPath (Join-Path $repository 'deploy/systemd/portkiller-update-api.service')
$workerUnit=Get-Content -Raw -LiteralPath (Join-Path $repository 'deploy/systemd/portkiller-update-worker.service')
$notifierUnit=Get-Content -Raw -LiteralPath (Join-Path $repository 'deploy/systemd/portkiller-update-notifier.service')
foreach ($required in @('User=portkiller-update-api','Group=portkiller-admin','ProtectSystem=strict','NoNewPrivileges=true','ReadWritePaths=/var/lib/portkiller-updates/admin','InaccessiblePaths=/var/lib/portkiller-updates/admin/archive')) {
    if ($apiUnit -notmatch [regex]::Escape($required)) { throw "API systemd 单元缺少约束：$required" }
}
foreach ($required in @('User=portkiller-update-worker','SupplementaryGroups=portkiller-publish','ProtectSystem=strict','NoNewPrivileges=true','ReadWritePaths=/var/lib/portkiller-updates/admin','/srv/portkiller-updates/stable')) {
    if ($workerUnit -notmatch [regex]::Escape($required)) { throw "Worker systemd 单元缺少约束：$required" }
}
foreach ($required in @('User=portkiller-update-notifier','ProtectSystem=strict','NoNewPrivileges=true','ReadOnlyPaths=/srv/portkiller-updates /etc/portkiller-updates')) {
    if ($notifierUnit -notmatch [regex]::Escape($required)) { throw "Notifier systemd 单元缺少约束：$required" }
}
$installer=Get-Content -Raw -LiteralPath (Join-Path $repository 'deploy/ubuntu/install-update-server.sh')
foreach ($required in @('portkiller-update-api','portkiller-update-worker','portkiller-update-notifier','portkiller-admin','portkiller-publish','2770 "$data_root"')) {
    if ($installer -notmatch [regex]::Escape($required)) { throw "安装脚本缺少权限边界：$required" }
}
foreach ($file in @($bundle.files)) {
    $path=Join-Path $OutputDirectory ([string]$file.path)
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "元数据缺少文件：$($file.path)" }
    $actual=Get-FileHash -LiteralPath $path -Algorithm SHA256
    if ((Get-Item -LiteralPath $path).Length -ne [long]$file.bytes -or $actual.Hash.ToLowerInvariant() -ne [string]$file.sha256) { throw "文件摘要不匹配：$($file.path)" }
}
Write-Output "PASS admin bundle: $OutputDirectory"
