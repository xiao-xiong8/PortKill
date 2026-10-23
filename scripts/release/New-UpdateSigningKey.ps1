#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$keyDirectory = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($keyDirectory) | Out-Null
$privatePath = Join-Path $keyDirectory 'update-private.pem'
$publicPath = Join-Path $keyDirectory 'update-public.pem'
if ((Test-Path -LiteralPath $privatePath) -or (Test-Path -LiteralPath $publicPath)) { throw '目标密钥已存在，拒绝覆盖。' }
$rsa = [Security.Cryptography.RSA]::Create(3072)
try {
    [IO.File]::WriteAllText($privatePath, $rsa.ExportPkcs8PrivateKeyPem())
    [IO.File]::WriteAllText($publicPath, $rsa.ExportSubjectPublicKeyInfoPem())
} finally { $rsa.Dispose() }
Write-Output "公钥：$publicPath"
Write-Output "私钥：$privatePath（仅供离线发布签名，禁止上传到公开服务器）"
