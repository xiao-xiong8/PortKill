#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$KitDirectory,
    [string]$ArchivePath,
    [switch]$ApplyToKit
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$kit = (Resolve-Path -LiteralPath $KitDirectory).Path
if (-not $ArchivePath) { $ArchivePath = Join-Path $repository '.local/artifacts/PortKill-Docker-Update.zip' }
$ArchivePath = [IO.Path]::GetFullPath($ArchivePath)
if ([IO.Path]::GetExtension($ArchivePath) -ne '.zip' -or (Test-Path -LiteralPath $ArchivePath -PathType Container)) { throw 'ArchivePath 必须是 ZIP 文件路径。' }
$protectedPaths = @('docker-compose.yml','docker/Dockerfile','docker/Caddyfile','docker/Caddy.Dockerfile','.env','verification.json')
$protectedHashes = @{}
foreach ($relative in $protectedPaths) {
    $path = Join-Path $kit $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "原部署目录缺少：$relative" }
    $protectedHashes[$relative] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
}
if ((Get-Content -LiteralPath (Join-Path $kit 'docker/Dockerfile') -Raw) -notmatch 'COPY\s+server-bundle/notifier/\s+/opt/portkill/notifier/') {
    throw '原 Dockerfile 未使用已确认的 notifier 构建路径。'
}
$bundlePath = Join-Path $kit 'server-bundle/bundle.json'
$bundle = Get-Content -LiteralPath $bundlePath -Raw | ConvertFrom-Json
if ($bundle.runtime -ne 'linux-x64' -or $bundle.schema -ne 1) { throw '仅支持当前 linux-x64 服务端部署包。' }
$temporaryParent = [IO.Path]::GetFullPath((Join-Path $repository '.local/tmp'))
New-Item -ItemType Directory -Path $temporaryParent -Force | Out-Null
$temporary = Join-Path $temporaryParent ('docker-notifier-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
try {
    $notifier = Join-Path $temporary 'server-bundle/notifier'
    & dotnet publish (Join-Path $repository 'src/Services/PortKiller.UpdateNotifier/PortKiller.UpdateNotifier.csproj') `
        -c Release -r linux-x64 --self-contained true -p:PublishAot=false -p:DebugType=None -p:DebugSymbols=false -o $notifier --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Linux 通知服务编译失败。' }
    $executable = Join-Path $notifier 'PortKiller.UpdateNotifier'
    $stream = [IO.File]::OpenRead($executable)
    try {
        $header = [byte[]]::new(64)
        if ($stream.Read($header,0,64) -ne 64 -or [Convert]::ToHexString($header[0..3]) -ne '7F454C46' `
            -or $header[4] -ne 2 -or [BitConverter]::ToUInt16($header,18) -ne 62) { throw '通知程序不是 Linux x64 ELF。' }
    } finally { $stream.Dispose() }
    $newFiles = @(Get-ChildItem -LiteralPath $notifier -Recurse -File)
    $newNames = @($newFiles | ForEach-Object { [IO.Path]::GetRelativePath($notifier,$_.FullName) })
    $obsolete = @(Get-ChildItem -LiteralPath (Join-Path $kit 'server-bundle/notifier') -Recurse -File | Where-Object {
        [IO.Path]::GetRelativePath((Join-Path $kit 'server-bundle/notifier'),$_.FullName) -notin $newNames
    })
    if ($obsolete.Count -ne 0) { throw '原通知目录存在新包不再使用的文件，需要先审查清理方案，不能直接覆盖。' }

    foreach ($name in @('update.sh','check-notifier.py','resolve-notifier-image.py','fix-publish-state.sh','UPDATE.txt')) {
        $destination = Join-Path $temporary "docker/$name"
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        $text = [IO.File]::ReadAllText((Join-Path $repository "deploy/docker/$name")).Replace("`r`n","`n")
        [IO.File]::WriteAllText($destination,$text,[Text.UTF8Encoding]::new($false))
    }
    $sourceRelative = 'src/Services/PortKiller.UpdateNotifier/NotifierHost.cs'
    $sourceDestination = Join-Path $temporary "source/$sourceRelative"
    New-Item -ItemType Directory -Path (Split-Path -Parent $sourceDestination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repository $sourceRelative) -Destination $sourceDestination

    $commit = (& git -C $repository rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw '无法读取源码版本。' }
    $dirty = @(& git -C $repository status --porcelain).Count -gt 0
    $generated = [DateTimeOffset]::UtcNow.ToString('O')
    $componentUpdate = [ordered]@{
        component='notifier'; fix='sse-immediate-first-frame'; runtime='linux-x64'; generatedUtc=$generated
        sourceCommit=$commit; sourceDirty=$dirty
        sourceSha256=(Get-FileHash -LiteralPath $sourceDestination -Algorithm SHA256).Hash.ToLowerInvariant()
        assemblySha256=(Get-FileHash -LiteralPath (Join-Path $notifier 'PortKiller.UpdateNotifier.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $bundleRoot = Join-Path $kit 'server-bundle'
    $otherFiles = @(Get-ChildItem -LiteralPath $bundleRoot -Recurse -File | Where-Object {
        $_.FullName -ne $bundlePath -and -not $_.FullName.StartsWith(([IO.Path]::GetFullPath((Join-Path $bundleRoot 'notifier')) + [IO.Path]::DirectorySeparatorChar),[StringComparison]::OrdinalIgnoreCase)
    } | ForEach-Object {
        [ordered]@{path=[IO.Path]::GetRelativePath($bundleRoot,$_.FullName).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
    $notifierFiles = @($newFiles | ForEach-Object {
        [ordered]@{path='notifier/'+[IO.Path]::GetRelativePath($notifier,$_.FullName).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
    $bundle.files = @($otherFiles + $notifierFiles | Sort-Object { $_.path })
    $bundle | Add-Member -NotePropertyName componentUpdates -NotePropertyValue @($componentUpdate) -Force
    $bundle | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $temporary 'server-bundle/bundle.json') -Encoding utf8NoBOM
    [ordered]@{schema=1;scope='notifier-only';deploymentScriptRevision=3;update=$componentUpdate;preserved=$protectedPaths;rollbackImage='portkill-notifier:previous'} `
        | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $temporary 'docker/notifier-update.json') -Encoding utf8NoBOM

    $payload = @(Get-ChildItem -LiteralPath $temporary -Recurse -File | Sort-Object FullName)
    foreach ($file in $payload) {
        $relative = [IO.Path]::GetRelativePath($temporary,$file.FullName).Replace('\','/')
        if ($relative -match '(^|/)(\.env[^/]*|verification\.json|data)(/|$)' -or $file.Extension -in @('.pem','.key','.pfx','.p12')) { throw "不允许进入更新包：$relative" }
    }
    $hashLines = @($payload | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(),[IO.Path]::GetRelativePath($temporary,$_.FullName).Replace('\','/')
    })
    [IO.File]::WriteAllText((Join-Path $temporary 'docker/notifier-update.sha256'),($hashLines -join "`n")+"`n",[Text.Encoding]::ASCII)
    New-Item -ItemType Directory -Path (Split-Path -Parent $ArchivePath) -Force | Out-Null
    if (Test-Path -LiteralPath $ArchivePath) { Remove-Item -LiteralPath $ArchivePath }
    [IO.Compression.ZipFile]::CreateFromDirectory($temporary,$ArchivePath,[IO.Compression.CompressionLevel]::Optimal,$false)
    $zip = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        if ($zip.Entries.Count -ne $payload.Count + 1) { throw 'ZIP 文件数量与载荷不一致。' }
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName -match '(^/|(^|/)\.\.(/|$))') { throw 'ZIP 路径越界。' }
        }
    } finally { $zip.Dispose() }
    if ($ApplyToKit) {
        foreach ($file in @(Get-ChildItem -LiteralPath $temporary -Recurse -File)) {
            $destination = Join-Path $kit ([IO.Path]::GetRelativePath($temporary,$file.FullName))
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
        }
    }
    foreach ($relative in $protectedPaths) {
        if ((Get-FileHash -LiteralPath (Join-Path $kit $relative) -Algorithm SHA256).Hash -ne $protectedHashes[$relative]) { throw "原部署配置发生变化：$relative" }
    }
    $archiveHash = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(($ArchivePath+'.sha256'),$archiveHash+'  '+[IO.Path]::GetFileName($ArchivePath)+"`n",[Text.Encoding]::ASCII)
    [pscustomobject]@{Archive=$ArchivePath;Bytes=(Get-Item -LiteralPath $ArchivePath).Length;Sha256=$archiveHash;AppliedToKit=[bool]$ApplyToKit;ProtectedConfigurationUnchanged=$true}
} finally {
    $resolvedTemporary = [IO.Path]::GetFullPath($temporary)
    $resolvedParent = [IO.Path]::GetFullPath($temporaryParent) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedTemporary.StartsWith($resolvedParent,[StringComparison]::OrdinalIgnoreCase)) { throw '临时目录清理路径越界。' }
    if (Test-Path -LiteralPath $resolvedTemporary) { Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force }
}
