#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$Version, [string]$Resume, [string]$SshUser,
    [string]$ServerIp = $env:PORTKILLER_UPDATE_SERVER_IP,
    [ValidateRange(1,65535)][int]$SshPort = 22,
    [ValidateRange(1,65535)][int]$HttpPort = 8080,
    [uri]$PublicBaseUrl, [switch]$AllowHttp, [switch]$UseSystemProxy,
    [uri]$ClientReportUrl,
    [string]$SigningKeyPath, [string]$PublicKeyPath, [string]$ReleaseNotesPath,
    [string]$VcVarsAllPath, [string]$PreparedPublishDirectory,
    [string]$LocalRoot, [switch]$AllowDirty, [string]$ResignRelease,
    [switch]$PrepareOnly, [string]$SshConfigPath
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
if ($PrepareOnly -and ($LocalRoot -or $SshUser -or $SshConfigPath -or $PSBoundParameters.ContainsKey('ServerIp') -or $PSBoundParameters.ContainsKey('SshPort'))) {
    throw 'PrepareOnly 仅制作本地签名产物，不能同时指定 SSH 或 LocalRoot 交付参数。'
}
if ($PrepareOnly -and -not $Resume -and -not $PublicBaseUrl) { throw 'PrepareOnly 需要明确指定 PublicBaseUrl，不从 SSH 主机推断更新源。' }
$sshClientArguments = @()
if ($SshConfigPath) { $sshClientArguments = @('-F', (Resolve-Path -LiteralPath $SshConfigPath).Path) }
$releaseRoot = Join-Path $projectRoot '.local/artifacts/releases'
[IO.Directory]::CreateDirectory($releaseRoot) | Out-Null
$publisherLock = [IO.FileStream]::new((Join-Path $releaseRoot '.publisher.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
$sequenceLock = $null
$explicitVersion = [bool]$Version
function Normalized([version]$v) { return [version]::new($v.Major,$v.Minor,[Math]::Max(0,$v.Build),[Math]::Max(0,$v.Revision)) }
function Save-Record {
    $temporary = $recordPath + '.tmp'
    [IO.File]::WriteAllText($temporary, ($record | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporary, $recordPath, $true)
}
function Invoke-Tool([string[]]$Arguments) {
    $output = & dotnet $tool @Arguments
    if ($LASTEXITCODE -ne 0) { throw "更新工具失败：$($Arguments[0])" }
    return ($output -join "`n")
}
function Invoke-Server([string[]]$Arguments) {
    if ($PrepareOnly) { throw 'PrepareOnly 禁止执行服务端命令。' }
    if ($LocalRoot) { return Invoke-Tool $Arguments }
    # Remote paths are fixed; dynamic identifiers/digests are validated before use.
    $output = & ssh @sshClientArguments -p $SshPort $target ("/opt/portkiller-updates/tool/PortKiller.UpdateTool " + ($Arguments -join ' '))
    if ($LASTEXITCODE -ne 0) { throw '服务端操作失败；使用同一 ReleaseId 和 -Resume 恢复。' }
    return ($output -join "`n")
}
try {
    $toolDirectory=Join-Path $releaseRoot '.tool'
    & dotnet build (Join-Path $projectRoot 'src/Tools/PortKiller.UpdateTool/PortKiller.UpdateTool.csproj') -c Release -o $toolDirectory --nologo
    if ($LASTEXITCODE -ne 0) { throw '更新工具构建失败。' }
    $tool = Join-Path $toolDirectory 'PortKiller.UpdateTool.dll'
    if ($Resume) {
        if ($Resume -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,100}$') { throw 'ReleaseId 格式错误。' }
        $releaseDirectory = Join-Path $releaseRoot $Resume
        $recordPath = Join-Path $releaseDirectory 'release-record.json'
        $record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json
        $preparedRecord = $record.PSObject.Properties['Mode'] -and $record.Mode -eq 'PrepareOnly'
        if ([bool]$PrepareOnly -ne [bool]$preparedRecord) { throw '准备记录只能使用 -PrepareOnly -Resume 复核；原发布记录保持原交付模式。' }
        if ($record.Phase -eq 'Superseded') { throw '本次发布已被更新的正式清单取代，不能恢复旧版本。' }
        if ($record.Phase -notin @('Verified','Staged','ManifestActivated','PublicVerified','Completed')) { throw '本次尚未完成签名验证，请创建新记录。' }
        $Version = $record.Version; $PublicBaseUrl = [uri]$record.PublicBaseUrl; $AllowHttp = $record.AllowHttp
    } else {
        $dirty = @(& git -C $projectRoot status --porcelain)
        if ($LASTEXITCODE -ne 0) { throw '无法读取 Git 状态。' }
        if ($dirty.Count -gt 0 -and -not $AllowDirty) { throw '发布要求干净工作区；本地演练可显式使用 -AllowDirty，CI 禁止该选项。' }
        if (-not $PrepareOnly -and -not $ServerIp) {
            $localConfig = Join-Path $projectRoot '.local/config/publisher.local.json'
            if (Test-Path -LiteralPath $localConfig) { $ServerIp = (Get-Content -LiteralPath $localConfig -Raw | ConvertFrom-Json).ServerIp }
        }
        if (-not $PublicBaseUrl) {
            $address = $null
            if (-not [Net.IPAddress]::TryParse($ServerIp, [ref]$address)) { throw '请提供 PublicBaseUrl 或兼容模式的 ServerIp。' }
            $PublicBaseUrl = [uri]"http://${ServerIp}:$HttpPort/stable/"; $AllowHttp = $true
        }
        if (($PublicBaseUrl.Scheme -ne 'https' -and -not ($AllowHttp -and $PublicBaseUrl.Scheme -eq 'http')) -or $PublicBaseUrl.UserInfo -or $PublicBaseUrl.Query -or $PublicBaseUrl.Fragment) { throw '分发地址必须是 HTTPS；HTTP 需要 -AllowHttp。' }
        $keyDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'PortKiller/publisher/keys'
        if (-not $SigningKeyPath) { $SigningKeyPath = Join-Path $keyDirectory 'update-private.pem' }
        if (-not $PublicKeyPath) { $PublicKeyPath = Join-Path $keyDirectory 'update-public.pem' }
        $privateKey = [Security.Cryptography.RSA]::Create(); $publicKey = [Security.Cryptography.RSA]::Create()
        try {
            $privateKey.ImportFromPem([IO.File]::ReadAllText([IO.Path]::GetFullPath($SigningKeyPath)))
            $publicKey.ImportFromPem([IO.File]::ReadAllText([IO.Path]::GetFullPath($PublicKeyPath)))
            $privateFingerprint=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($privateKey.ExportSubjectPublicKeyInfo()))
            $keyFingerprint=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($publicKey.ExportSubjectPublicKeyInfo()))
            if ($privateKey.KeySize -lt 2048 -or $privateFingerprint -ne $keyFingerprint) { throw 'SigningKeyMismatch: 签名私钥与分发公钥不匹配。' }
        } finally { $privateKey.Dispose(); $publicKey.Dispose() }
        [xml]$project = [IO.File]::ReadAllText((Join-Path $projectRoot 'src/PortKiller.Desktop/PortKiller.Desktop.csproj'))
        $current = [version]$project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
        if (-not $Version) { $Version = "$($current.Major).$($current.Minor).$([Math]::Max(0,$current.Build)+1)" }
        if ($Version -notmatch '^\d+\.\d+(\.\d+){0,2}$') { throw '版本格式错误。' }
        if (-not $ResignRelease -and (Normalized ([version]$Version)) -le (Normalized $current)) { throw "版本必须高于项目版本 $current。" }
        $releaseId = "$([DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmss'))-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        $releaseDirectory = Join-Path $releaseRoot $releaseId
        [IO.Directory]::CreateDirectory($releaseDirectory) | Out-Null
        $recordPath = Join-Path $releaseDirectory 'release-record.json'
        $record = [pscustomobject]@{Schema=2; ReleaseId=$releaseId; Mode=$(if ($PrepareOnly) { 'PrepareOnly' } else { 'Publish' }); Version=$Version; Phase='Created'; PublicBaseUrl=$PublicBaseUrl.AbsoluteUri; AllowHttp=[bool]$AllowHttp; Commit=(& git -C $projectRoot rev-parse HEAD); Dirty=($dirty.Count -gt 0); KeyFingerprint=$keyFingerprint; BaselineKnown=(-not [bool]$PrepareOnly); Expected=$(if ($PrepareOnly) { $null } else { 'none' }); Sequence=0L; ManifestSha256=''; PackageSha256=''; Runtime='win-x64'; NativeAot=$false; FileVersion=''; ExeSha256=''; PackageBytes=0L; ManifestBytes=0L; ExpiresUtc=''; PreparedUtc=''; SourceRelease=$ResignRelease}
        Save-Record
        if ($ClientReportUrl -and ($ClientReportUrl.Scheme -ne 'https' -or $ClientReportUrl.UserInfo -or $ClientReportUrl.Query -or $ClientReportUrl.Fragment)) { throw 'ClientReportUrl 必须是不含凭据、查询或片段的 HTTPS 地址。' }
        if (-not $ClientReportUrl -and $PublicBaseUrl.Scheme -eq 'https') { $ClientReportUrl = [uri]::new($PublicBaseUrl, '/api/v1/client-installations/') }
        $settings = [ordered]@{Enabled=$true; UseSystemProxy=[bool]$UseSystemProxy; AllowHttp=[bool]$AllowHttp; ManifestUrl=$PublicBaseUrl.AbsoluteUri.TrimEnd('/')+'/manifest.json'; ClientReportUrl=$(if ($ClientReportUrl) { $ClientReportUrl.AbsoluteUri } else { '' }); PublicKeyPem=[IO.File]::ReadAllText([IO.Path]::GetFullPath($PublicKeyPath)); RealtimeEnabled=$true; PollSeconds=30; CheckTimeoutSeconds=8; DownloadTimeoutSeconds=180}
        [IO.File]::WriteAllText((Join-Path $releaseDirectory 'verification.json'), ($settings | ConvertTo-Json -Depth 4))
    }
    $config = Join-Path $releaseDirectory 'verification.json'
    if ($Version -notmatch '^\d+\.\d+(\.\d+){0,2}$' -or $record.ReleaseId -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,100}$' -or (-not $PrepareOnly -and $record.Expected -notmatch '^(none|[A-Fa-f0-9]{64})$')) { throw '发布记录格式无效。' }
    $serverRoot = if ($LocalRoot) { [IO.Path]::GetFullPath($LocalRoot) } else { '/srv/portkiller-updates/stable' }
    $serverConfig = if ($LocalRoot) { $config } else { '/etc/portkiller-updates/verification.json' }
    if (-not $PrepareOnly -and -not $LocalRoot) {
        if ($SshUser -notmatch '^[A-Za-z_][A-Za-z0-9_.-]*$' -or $ServerIp -notmatch '^[A-Za-z0-9][A-Za-z0-9.-]*$') { throw '请提供有效 SSH 用户和主机。' }
        $target = "$SshUser@$ServerIp"
    }
    $packageName = "PortKiller-$Version-win-x64.zip"
    $package = Join-Path $releaseDirectory $packageName; $manifest = Join-Path $releaseDirectory 'manifest.json'
    if (-not $Resume) {
        $currentReceipt = $null
        if (-not $PrepareOnly) { $currentReceipt = (Invoke-Server @('inspect','--root',$serverRoot,'--config',$serverConfig)) | ConvertFrom-Json }
        if ($null -ne $currentReceipt) { $record.Expected=$currentReceipt.ManifestSha256 }
        if (-not $explicitVersion -and -not $ResignRelease -and $currentReceipt -and (Normalized ([version]$currentReceipt.Version)) -ge (Normalized ([version]$Version))) {
            $baseline=[version]$currentReceipt.Version
            $Version="$($baseline.Major).$($baseline.Minor).$([Math]::Max(0,$baseline.Build)+1)"
            $record.Version=$Version
            $packageName="PortKiller-$Version-win-x64.zip"; $package=Join-Path $releaseDirectory $packageName
        }
        $sequenceDirectory=Split-Path -Parent ([IO.Path]::GetFullPath($SigningKeyPath))
        $sequenceLock=[IO.FileStream]::new((Join-Path $sequenceDirectory 'sequence.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        $sequencePath=Join-Path $sequenceDirectory 'update-sequence.txt'
        $previous=if (Test-Path -LiteralPath $sequencePath) { [long]::Parse([IO.File]::ReadAllText($sequencePath)) } else { 0L }
        $sequenceBaseline=[Math]::Max($previous,[long]$currentReceipt.Sequence)
        if ($previous -lt 0 -or $sequenceBaseline -eq [long]::MaxValue) { throw '本地或服务器序号不可继续分配。' }
        $record.Sequence=[Math]::Max([DateTimeOffset]::UtcNow.ToUnixTimeSeconds(),$sequenceBaseline+1)
        $seqTmp=$sequencePath+'.tmp'; [IO.File]::WriteAllText($seqTmp,[string]$record.Sequence); [IO.File]::Move($seqTmp,$sequencePath,$true)
        Save-Record
        $publish=Join-Path $releaseDirectory 'publish'
        if ($ResignRelease) {
            if ($ResignRelease -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,100}$') { throw '续签 ReleaseId 格式错误。' }
            $old=Join-Path $releaseRoot $ResignRelease
            Copy-Item -LiteralPath (Join-Path $old $packageName) -Destination $package
            & (Join-Path $PSScriptRoot 'Renew-UpdateManifest.ps1') -ManifestPath (Join-Path $old 'manifest.json') -OutputPath $manifest -SigningKeyPath $SigningKeyPath -PublicKeyPath $PublicKeyPath -Sequence $record.Sequence
        } else {
            if ($PreparedPublishDirectory) {
                & (Join-Path $PSScriptRoot 'Get-AotArtifactInfo.ps1') -ExecutablePath (Join-Path $PreparedPublishDirectory 'PortKiller.exe') | Out-Null
                [IO.Directory]::CreateDirectory($publish)|Out-Null
                foreach ($name in @('PortKiller.exe','av_libglesv2.dll','libHarfBuzzSharp.dll','libSkiaSharp.dll')) { Copy-Item -LiteralPath (Join-Path $PreparedPublishDirectory $name) -Destination $publish }
            } else {
                & (Join-Path $projectRoot 'scripts/build/Build.ps1') -Configuration Release -IncludeUi
                if ($LASTEXITCODE -ne 0) { throw '构建回归失败。' }
                $buildArgs=@{OutputDirectory=$publish;Version=$Version}; if ($VcVarsAllPath) { $buildArgs.VcVarsAllPath=$VcVarsAllPath }
                & (Join-Path $projectRoot 'scripts/build/Publish-Aot.ps1') @buildArgs
            }
            Copy-Item -LiteralPath $config -Destination (Join-Path $publish 'update-settings.json')
            if (-not $ReleaseNotesPath) { $ReleaseNotesPath=Join-Path $projectRoot 'docs/RELEASE-NOTES.txt' }
            & (Join-Path $PSScriptRoot 'New-UpdateRelease.ps1') -PublishDirectory $publish -Version $Version -PackageBaseUrl $PublicBaseUrl -SigningKeyPath $SigningKeyPath -OutputDirectory $releaseDirectory -Sequence $record.Sequence -ReleaseNotesPath $ReleaseNotesPath -AllowHttp:$AllowHttp
            if (-not $PrepareOnly) {
                $zip=[IO.Compression.ZipFile]::Open((Join-Path $releaseDirectory "PortKiller-$Version-setup.zip"),[IO.Compression.ZipArchiveMode]::Create)
                try { foreach ($file in @('PortKiller.exe','av_libglesv2.dll','libHarfBuzzSharp.dll','libSkiaSharp.dll','update-settings.json')) { [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,(Join-Path $publish $file),$file)|Out-Null } } finally { $zip.Dispose() }
            }
        }
        $verified = (Invoke-Tool @('verify','--config',$config,'--manifest',$manifest,'--package',$package)) | ConvertFrom-Json
        $record.ManifestSha256=(Get-FileHash -LiteralPath $manifest).Hash; $record.PackageSha256=(Get-FileHash -LiteralPath $package).Hash
        if ($verified.Version -ne $record.Version -or $verified.Sequence -ne $record.Sequence -or $verified.Runtime -ne 'win-x64' -or $verified.ManifestSha256 -ne $record.ManifestSha256 -or $verified.Sha256 -ne $record.PackageSha256) { throw '制作记录与已验签产物不一致。' }
        $aotTemporary = Join-Path $releaseDirectory '.verify-native.exe'
        $aotZip = [IO.Compression.ZipFile]::OpenRead($package)
        try {
            [IO.Compression.ZipFileExtensions]::ExtractToFile($aotZip.GetEntry('PortKiller.exe'), $aotTemporary, $false)
            $aotInfo = & (Join-Path $PSScriptRoot 'Get-AotArtifactInfo.ps1') -ExecutablePath $aotTemporary
            $record.NativeAot=$aotInfo.NativeAot; $record.FileVersion=$aotInfo.FileVersion; $record.ExeSha256=$aotInfo.ExeSha256
        } finally { $aotZip.Dispose(); if (Test-Path -LiteralPath $aotTemporary) { Remove-Item -LiteralPath $aotTemporary } }
        $record.PackageBytes=(Get-Item -LiteralPath $package).Length; $record.ManifestBytes=(Get-Item -LiteralPath $manifest).Length
        $record.ExpiresUtc=$verified.ExpiresUtc; $record.PreparedUtc=[DateTimeOffset]::UtcNow.ToString('O')
        $record.Phase='Verified'; Save-Record
    }
    if ((Get-FileHash -LiteralPath $manifest).Hash -ne $record.ManifestSha256 -or (Get-FileHash -LiteralPath $package).Hash -ne $record.PackageSha256) { throw '已签名产物发生变化，拒绝恢复。' }
    Invoke-Tool @('verify','--config',$config,'--manifest',$manifest,'--package',$package)|Out-Null
    if ($PrepareOnly) {
        Write-Output "准备验证完成：$($record.ReleaseId)；网站上传文件：$manifest 和 $package；记录：$recordPath"
        return
    }
    $alreadyActivated = $false
    if ($Resume) {
        $currentReceipt = (Invoke-Server @('recover','--root',$serverRoot,'--config',$serverConfig)) | ConvertFrom-Json
        if ($currentReceipt -and $currentReceipt.ManifestSha256 -eq $record.ManifestSha256) {
            $alreadyActivated = $true; $record.Phase='ManifestActivated'; Save-Record
            [IO.File]::WriteAllText((Join-Path $releaseDirectory 'server-receipt.json'), ($currentReceipt | ConvertTo-Json -Depth 5))
        }
        elseif ($currentReceipt -and [long]$currentReceipt.Sequence -gt [long]$record.Sequence -and (Normalized ([version]$currentReceipt.Version)) -ge (Normalized ([version]$record.Version))) {
            $record.Phase='Superseded'; Save-Record
            throw '本次发布已被更新的正式清单取代，不能恢复旧版本。'
        }
    }
    if (-not $alreadyActivated) {
        if ($LocalRoot) { $remoteManifest=$manifest; $remotePackage=$package } else {
            $stage='/srv/portkiller-updates/.staging/'+$record.ReleaseId
            & ssh @sshClientArguments -p $SshPort $target "mkdir -p -m 700 $stage"
            if ($LASTEXITCODE -ne 0) { throw '创建暂存目录失败。' }
            & scp @sshClientArguments -P $SshPort -- $manifest $package "${target}:$stage/"
            if ($LASTEXITCODE -ne 0) { throw '上传失败；使用同一 ReleaseId 恢复。' }
            $remoteManifest="$stage/manifest.json"; $remotePackage="$stage/$packageName"
        }
        $record.Phase='Staged'; Save-Record
        $receipt=Invoke-Server @('activate','--root',$serverRoot,'--config',$serverConfig,'--manifest',$remoteManifest,'--package',$remotePackage,'--expected',$record.Expected)
        [IO.File]::WriteAllText((Join-Path $releaseDirectory 'server-receipt.json'),$receipt)
        $record.Phase='ManifestActivated'; Save-Record
    }
    Invoke-Tool @('public-check','--config',$config,'--expected',$record.ManifestSha256)|Out-Null
    $record.Phase='PublicVerified'; Save-Record
    $record.Phase='Completed'; Save-Record
    Write-Output "发布验证完成：$($record.ReleaseId)；记录：$recordPath"
} catch {
    if ($recordPath) { Write-Warning "已保留记录：$recordPath；签名完成后使用 -Resume <ReleaseId>。" }
    throw
} finally { if ($sequenceLock) { $sequenceLock.Dispose() }; $publisherLock.Dispose() }
