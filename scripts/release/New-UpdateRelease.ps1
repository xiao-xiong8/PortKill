#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][uri]$PackageBaseUrl,
    [Parameter(Mandatory)][string]$SigningKeyPath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [long]$Sequence = 0,
    [string]$ReleaseNotesPath,
    [switch]$AllowHttp
)
$ErrorActionPreference = 'Stop'
$releaseNotes = @()
if ($ReleaseNotesPath) {
    $releaseNotes = @([IO.File]::ReadAllLines([IO.Path]::GetFullPath($ReleaseNotesPath)) | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    if ($releaseNotes.Count -gt 20 -or @($releaseNotes | Where-Object { $_.Length -gt 300 }).Count -gt 0) {
        throw '更新说明最多 20 行，每行不超过 300 字。'
    }
}
if ($Version -notmatch '^\d+\.\d+(\.\d+){0,2}$') { throw '仅支持两至四段数字正式版本。' }
if ($Sequence -lt 0) { throw '发布序号不能为负数。' }
$validScheme = $PackageBaseUrl.Scheme -eq 'https' -or ($AllowHttp -and $PackageBaseUrl.Scheme -eq 'http')
if (-not $validScheme -or $PackageBaseUrl.UserInfo -or $PackageBaseUrl.Query -or $PackageBaseUrl.Fragment) {
    throw '发布地址必须使用 HTTPS；HTTP 需要显式指定 -AllowHttp，且地址不得含凭据、查询或片段。'
}
$publishPath = [IO.Path]::GetFullPath($PublishDirectory)
$releasePath = [IO.Path]::GetFullPath($OutputDirectory)
$binaryNames = @('PortKiller.exe','av_libglesv2.dll','libHarfBuzzSharp.dll','libSkiaSharp.dll')
foreach ($name in $binaryNames) { if (-not (Test-Path -LiteralPath (Join-Path $publishPath $name))) { throw "缺少 $name" } }
if (Test-Path -LiteralPath (Join-Path $publishPath 'PortKiller.dll')) { throw '必须使用 Native AOT 发布目录。' }
$binaryVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publishPath 'PortKiller.exe')).FileVersion
function Normalize-Version([string]$value) {
    $parsed = [version]$value
    return [version]::new($parsed.Major, $parsed.Minor, [Math]::Max(0, $parsed.Build), [Math]::Max(0, $parsed.Revision))
}
if ((Normalize-Version $binaryVersion) -ne (Normalize-Version $Version)) { throw "程序版本 $binaryVersion 与清单版本 $Version 不一致。" }
[IO.Directory]::CreateDirectory($releasePath) | Out-Null
$packageName = "PortKiller-$Version-win-x64.zip"
$packagePath = Join-Path $releasePath $packageName
$manifestPath = Join-Path $releasePath 'manifest.json'
if ((Test-Path -LiteralPath $packagePath) -or (Test-Path -LiteralPath $manifestPath)) { throw '输出已存在；请使用新目录，避免覆盖正在发布的内容。' }
$zip = [IO.Compression.ZipFile]::Open($packagePath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($name in $binaryNames) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $publishPath $name), $name, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $zip.Dispose() }
$packageFile = Get-Item -LiteralPath $packagePath
if ($packageFile.Length -gt 268435456) { throw '压缩包超过客户端 256 MiB 上限。' }
$payloadObject = [ordered]@{
    Schema = 2; Version = $Version; Runtime = 'win-x64'
    PackageUrl = $PackageBaseUrl.AbsoluteUri.TrimEnd('/') + '/' + $packageName
    Size = $packageFile.Length; Sha256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
    Sequence = $(if ($Sequence -gt 0) { $Sequence } else { [DateTimeOffset]::UtcNow.ToUnixTimeSeconds() })
    PublishedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    ExpiresUtc = [DateTimeOffset]::UtcNow.AddDays(90).ToString('O')
    ReleaseNotes = $releaseNotes
}
$payload = [Text.Encoding]::UTF8.GetBytes(($payloadObject | ConvertTo-Json -Compress))
$rsa = [Security.Cryptography.RSA]::Create()
try {
    $rsa.ImportFromPem([IO.File]::ReadAllText([IO.Path]::GetFullPath($SigningKeyPath)))
    if ($rsa.KeySize -lt 2048) { throw '签名密钥至少需要 RSA 2048 位。' }
    $signature = $rsa.SignData($payload, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
} finally { $rsa.Dispose() }
$signed = [ordered]@{ Payload = [Convert]::ToBase64String($payload); Signature = [Convert]::ToBase64String($signature) }
[IO.File]::WriteAllText($manifestPath, ($signed | ConvertTo-Json))
Write-Output "更新包：$packagePath"
Write-Output "清单：$manifestPath（先上传版本包，再原子发布清单）"
