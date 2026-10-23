#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$Version,
    [Parameter(Mandatory)][string]$SshUser,
    [string]$ServerIp = $env:PORTKILLER_UPDATE_SERVER_IP,
    [ValidateRange(1, 65535)][int]$SshPort = 22,
    [ValidateRange(1, 65535)][int]$HttpPort = 8080,
    [string]$SigningKeyPath,
    [string]$PublicKeyPath,
    [string]$ReleaseNotesPath,
    [string]$VcVarsAllPath
)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
if ([string]::IsNullOrWhiteSpace($ServerIp)) {
    $publisherSettingsPath = Join-Path $projectRoot 'config/publisher.local.json'
    if (Test-Path -LiteralPath $publisherSettingsPath) {
        $publisherSettings = Get-Content -LiteralPath $publisherSettingsPath -Raw | ConvertFrom-Json
        $ServerIp = [string]$publisherSettings.ServerIp
    }
}
if (-not $ReleaseNotesPath) { $ReleaseNotesPath = Join-Path $projectRoot 'docs/RELEASE-NOTES.txt' }
if (-not (Test-Path -LiteralPath $ReleaseNotesPath)) { throw '找不到更新说明文件。' }
$projectFile = Join-Path $projectRoot 'src/PortKiller.Desktop/PortKiller.Desktop.csproj'
[xml]$project = [IO.File]::ReadAllText($projectFile)
$versionNode = $project.SelectSingleNode('/Project/PropertyGroup/Version')
if ($null -eq $versionNode) { throw 'Desktop 项目缺少 Version 属性。' }
$currentVersion = [string]$versionNode.InnerText
if ($currentVersion -notmatch '^\d+\.\d+(\.\d+){0,2}$') { throw "项目版本格式无效：$currentVersion" }

function Normalize-Version([string]$value) {
    $parsed = [version]$value
    return [version]::new($parsed.Major, $parsed.Minor, [Math]::Max(0, $parsed.Build), [Math]::Max(0, $parsed.Revision))
}
if (-not $Version) {
    $parsed = [version]$currentVersion
    $patch = [Math]::Max(0, $parsed.Build) + 1
    $Version = "$($parsed.Major).$($parsed.Minor).$patch"
}
if ($Version -notmatch '^\d+\.\d+(\.\d+){0,2}$' -or (Normalize-Version $Version) -le (Normalize-Version $currentVersion)) {
    throw "发布版本必须高于当前版本 $currentVersion。"
}
if ($SshUser -notmatch '^[A-Za-z_][A-Za-z0-9_.-]*$') { throw 'SSH 用户名格式无效。' }
$parsedIp = $null
if (-not [Net.IPAddress]::TryParse($ServerIp, [ref]$parsedIp) -or $parsedIp.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
    throw 'ServerIp 必须是 IPv4 地址。'
}
if (-not $SigningKeyPath -or -not $PublicKeyPath) {
    $keyDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'PortKiller/publisher/keys'
    if (-not $SigningKeyPath) { $SigningKeyPath = Join-Path $keyDirectory 'update-private.pem' }
    if (-not $PublicKeyPath) { $PublicKeyPath = Join-Path $keyDirectory 'update-public.pem' }
}
$SigningKeyPath = (Resolve-Path -LiteralPath $SigningKeyPath).Path
$PublicKeyPath = (Resolve-Path -LiteralPath $PublicKeyPath).Path
foreach ($tool in @('ssh', 'scp')) { if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "缺少 OpenSSH 命令：$tool" } }

Push-Location -LiteralPath $projectRoot
try {
    & (Join-Path $projectRoot 'scripts/build/Build.ps1') -Configuration Release -IncludeUi
    if ($LASTEXITCODE -ne 0) { throw '构建或回归验证失败。' }

    $publishDirectory = Join-Path $projectRoot 'artifacts/publish/win-x64'
    $publishArgs = @{ OutputDirectory = $publishDirectory; Version = $Version }
    if ($VcVarsAllPath) { $publishArgs.VcVarsAllPath = $VcVarsAllPath }
    & (Join-Path $projectRoot 'scripts/build/Publish-Aot.ps1') @publishArgs

    $publicKey = [IO.File]::ReadAllText($PublicKeyPath).Trim()
    $settings = [ordered]@{
        Enabled = $true; AllowHttp = $true
        ManifestUrl = "http://${ServerIp}:$HttpPort/stable/manifest.json"
        PublicKeyPem = $publicKey
        PollSeconds = 300; CheckTimeoutSeconds = 8; DownloadTimeoutSeconds = 180
    }
    [IO.File]::WriteAllText((Join-Path $publishDirectory 'update-settings.json'),
        ($settings | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))

    $timestamp = [DateTimeOffset]::UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'")
    $releaseDirectory = Join-Path $projectRoot "artifacts/releases/$Version-$timestamp"
    $keyDirectory = Split-Path -Parent $SigningKeyPath
    [IO.Directory]::CreateDirectory($keyDirectory) | Out-Null
    $sequencePath = Join-Path $keyDirectory 'update-sequence.txt'
    $previousSequence = if (Test-Path -LiteralPath $sequencePath) { [long]::Parse([IO.File]::ReadAllText($sequencePath).Trim()) } else { 0L }
    $sequence = [Math]::Max([DateTimeOffset]::UtcNow.ToUnixTimeSeconds(), $previousSequence + 1)
    $sequenceTemporary = $sequencePath + '.tmp'
    [IO.File]::WriteAllText($sequenceTemporary, $sequence.ToString([Globalization.CultureInfo]::InvariantCulture), [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($sequenceTemporary, $sequencePath, $true)

    & (Join-Path $projectRoot 'scripts/release/New-UpdateRelease.ps1') `
        -PublishDirectory $publishDirectory -Version $Version `
        -PackageBaseUrl "http://${ServerIp}:$HttpPort/stable/" `
        -SigningKeyPath $SigningKeyPath -OutputDirectory $releaseDirectory `
        -Sequence $sequence -ReleaseNotesPath $ReleaseNotesPath -AllowHttp

    $packageName = "PortKiller-$Version-win-x64.zip"
    $packagePath = Join-Path $releaseDirectory $packageName
    $manifestPath = Join-Path $releaseDirectory 'manifest.json'
    $package = Get-Item -LiteralPath $packagePath
    $packageHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $stageName = "portkiller-upload-$Version-$([guid]::NewGuid().ToString('N'))"
    $target = "$SshUser@$ServerIp"
    & ssh -p $SshPort $target "mkdir -m 700 -- '$stageName'"
    if ($LASTEXITCODE -ne 0) { throw '无法连接服务器或创建上传暂存目录。' }
    & scp -P $SshPort -- $packagePath $manifestPath "${target}:$stageName/"
    if ($LASTEXITCODE -ne 0) { throw 'SCP 上传失败；服务器暂存目录保留供检查。' }

    $remoteCommand = @"
set -eu
stage='$stageName'
root='/srv/portkiller-updates/stable'
cd "`$HOME/`$stage"
test -w "`$root"
test "`$(stat -c %s '$packageName')" = '$($package.Length)'
printf '%s  %s\n' '$packageHash' '$packageName' | sha256sum -c -
printf '%s  %s\n' '$manifestHash' 'manifest.json' | sha256sum -c -
test ! -e "`$root/$packageName"
install -m 0644 '$packageName' "`$root/$packageName.uploading"
mv -T "`$root/$packageName.uploading" "`$root/$packageName"
curl -fSs --max-time 15 -o /dev/null 'http://127.0.0.1:$HttpPort/stable/$packageName'
install -m 0644 'manifest.json' "`$root/manifest.json.uploading"
mv -T "`$root/manifest.json.uploading" "`$root/manifest.json"
curl -fSs --max-time 15 -o /dev/null 'http://127.0.0.1:$HttpPort/stable/manifest.json'
rm -rf -- "`$HOME/`$stage"
"@
    & ssh -p $SshPort $target $remoteCommand
    if ($LASTEXITCODE -ne 0) { throw '服务器校验或原子发布失败；请检查 SSH 输出和暂存目录。' }

    $versionNode.InnerText = $Version
    $project.Save($projectFile)
    Write-Output "发布成功：版本 $Version，HTTP ${ServerIp}:$HttpPort；产物保存在 $releaseDirectory"
    Write-Output '初次安装时将 update-settings.json 与 AOT 四文件放在同一目录；后续更新只替换四个程序文件。'
} finally { Pop-Location }
