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
    'deploy/systemd/portkiller-update-monitor.service',
    'deploy/systemd/portkiller-update-monitor.timer',
    'deploy/ubuntu/install-update-server.sh',
    'deploy/ubuntu/monitor-update-server.sh',
    'deploy/ubuntu/monitor-update-server.py',
    'deploy/nginx/portkiller-admin.conf.example',
    'deploy/nginx/portkiller-download-tls.conf.example',
    'deploy/ubuntu/backup-update-server.sh',
    'deploy/ubuntu/restore-update-server.sh',
    'deploy/ubuntu/upgrade-update-server.sh',
    'deploy/examples/update-admin-api.env.example',
    'deploy/examples/update-admin-worker.env.example',
    'deploy/examples/update-monitor.env.example'
)
foreach ($relative in $deploymentFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $repository $relative) -PathType Leaf)) { throw "缺少部署文件：$relative" }
}
$apiUnit=Get-Content -Raw -LiteralPath (Join-Path $repository 'deploy/systemd/portkiller-update-api.service')
$workerUnit=Get-Content -Raw -LiteralPath (Join-Path $repository 'deploy/systemd/portkiller-update-worker.service')
$notifierUnit=Get-Content -Raw -LiteralPath (Join-Path $repository 'deploy/systemd/portkiller-update-notifier.service')
$monitorUnit=Get-Content -Raw -LiteralPath (Join-Path $repository 'deploy/systemd/portkiller-update-monitor.service')
$monitorTimer=Get-Content -Raw -LiteralPath (Join-Path $repository 'deploy/systemd/portkiller-update-monitor.timer')
foreach ($required in @('User=portkiller-update-api','Group=portkiller-admin','ProtectSystem=strict','NoNewPrivileges=true','ReadWritePaths=/var/lib/portkiller-updates/admin','InaccessiblePaths=/var/lib/portkiller-updates/admin/archive')) {
    if ($apiUnit -notmatch [regex]::Escape($required)) { throw "API systemd 单元缺少约束：$required" }
}
foreach ($required in @('User=portkiller-update-worker','SupplementaryGroups=portkiller-publish','ProtectSystem=strict','NoNewPrivileges=true','ReadWritePaths=/var/lib/portkiller-updates/admin','/srv/portkiller-updates/stable')) {
    if ($workerUnit -notmatch [regex]::Escape($required)) { throw "Worker systemd 单元缺少约束：$required" }
}
foreach ($required in @('User=portkiller-update-notifier','ProtectSystem=strict','NoNewPrivileges=true','ReadOnlyPaths=/srv/portkiller-updates /etc/portkiller-updates')) {
    if ($notifierUnit -notmatch [regex]::Escape($required)) { throw "Notifier systemd 单元缺少约束：$required" }
}
foreach ($required in @('User=portkiller-update-monitor','Group=portkiller-update-monitor','SupplementaryGroups=portkiller-admin','ProtectSystem=strict','NoNewPrivileges=true','ReadOnlyPaths=/var/lib/portkiller-updates /srv/portkiller-updates /etc/portkiller-updates /opt/portkiller-updates','StandardOutput=journal','SyslogIdentifier=portkiller-update-monitor')) {
    if ($monitorUnit -notmatch [regex]::Escape($required)) { throw "监测 systemd 单元缺少约束：$required" }
}
if ($monitorTimer -notmatch [regex]::Escape('OnUnitActiveSec=5min')) { throw '监测 timer 缺少周期' }
$installer=Get-Content -Raw -LiteralPath (Join-Path $repository 'deploy/ubuntu/install-update-server.sh')
foreach ($required in @('portkiller-update-api','portkiller-update-worker','portkiller-update-notifier','portkiller-update-monitor','portkiller-admin','portkiller-publish','2770 "$data_root"','monitor-update-server.py','python3')) {
    if ($installer -notmatch [regex]::Escape($required)) { throw "安装脚本缺少权限边界：$required" }
}
$nginxAdmin=Get-Content -Raw -LiteralPath (Join-Path $repository 'deploy/nginx/portkiller-admin.conf.example')
$nginxDownload=Get-Content -Raw -LiteralPath (Join-Path $repository 'deploy/nginx/portkiller-download-tls.conf.example')
foreach ($required in @('location /api/v1/','events/stream','proxy_buffering off','proxy_request_buffering off','client_max_body_size 257m','X-Forwarded-Proto https','try_files $uri $uri/ /index.html')) {
    if ($nginxAdmin -notmatch [regex]::Escape($required)) { throw "管理 Nginx 模板缺少约束：$required" }
}
foreach ($required in @('location = /stable/manifest.json','immutable','max_ranges 1','location = /stable/events','location / { return 404; }')) {
    if ($nginxDownload -notmatch [regex]::Escape($required)) { throw "下载 Nginx 模板缺少约束：$required" }
}
$backupScript=Get-Content -Raw -LiteralPath (Join-Path $repository 'deploy/ubuntu/backup-update-server.sh')
$restoreScript=Get-Content -Raw -LiteralPath (Join-Path $repository 'deploy/ubuntu/restore-update-server.sh')
$upgradeScript=Get-Content -Raw -LiteralPath (Join-Path $repository 'deploy/ubuntu/upgrade-update-server.sh')
foreach ($required in @('db backup --output','files.sha256','archive','stable')) {
    if ($backupScript -notmatch [regex]::Escape($required)) { throw "备份脚本缺少内容：$required" }
}
foreach ($required in @('db restore --input','sha256sum -c files.sha256','recover --root','inspect --root')) {
    if ($restoreScript -notmatch [regex]::Escape($required)) { throw "恢复脚本缺少内容：$required" }
}
foreach ($required in @('backup-update-server.sh','db migrate','/health/live','portkiller-update-monitor.timer')) {
    if ($upgradeScript -notmatch [regex]::Escape($required)) { throw "升级脚本缺少内容：$required" }
}
foreach ($file in @($bundle.files)) {
    $path=Join-Path $OutputDirectory ([string]$file.path)
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "元数据缺少文件：$($file.path)" }
    $actual=Get-FileHash -LiteralPath $path -Algorithm SHA256
    if ((Get-Item -LiteralPath $path).Length -ne [long]$file.bytes -or $actual.Hash.ToLowerInvariant() -ne [string]$file.sha256) { throw "文件摘要不匹配：$($file.path)" }
}
Write-Output "PASS admin bundle: $OutputDirectory"
