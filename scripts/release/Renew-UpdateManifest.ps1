#requires -Version 7.0
param([Parameter(Mandatory)][string]$ManifestPath,[Parameter(Mandatory)][string]$OutputPath,
    [Parameter(Mandatory)][string]$SigningKeyPath,[Parameter(Mandatory)][string]$PublicKeyPath,[Parameter(Mandatory)][long]$Sequence)
$ErrorActionPreference='Stop'
$signed=Get-Content -LiteralPath $ManifestPath -Raw|ConvertFrom-Json
$bytes=[Convert]::FromBase64String($signed.Payload)
$rsa=[Security.Cryptography.RSA]::Create(); $public=[Security.Cryptography.RSA]::Create()
try {
    $rsa.ImportFromPem([IO.File]::ReadAllText($SigningKeyPath)); $public.ImportFromPem([IO.File]::ReadAllText($PublicKeyPath))
    if (-not $public.VerifyData($bytes,[Convert]::FromBase64String($signed.Signature),[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)) { throw '原清单签名无效。' }
    $payload=[Text.Encoding]::UTF8.GetString($bytes)|ConvertFrom-Json
    if ($payload.Schema -ne 2 -or $Sequence -le $payload.Sequence) { throw '续签必须使用更高序号。' }
    $payload.Sequence=$Sequence; $payload.PublishedUtc=[DateTimeOffset]::UtcNow.ToString('O'); $payload.ExpiresUtc=[DateTimeOffset]::UtcNow.AddDays(90).ToString('O')
    $bytes=[Text.Encoding]::UTF8.GetBytes(($payload|ConvertTo-Json -Compress -Depth 5))
    $signature=$rsa.SignData($bytes,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
    if (-not $public.VerifyData($bytes,$signature,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)) { throw '签名密钥不匹配。' }
    if (Test-Path -LiteralPath $OutputPath) { throw '续签输出已经存在。' }
    [IO.File]::WriteAllText($OutputPath,(@{Payload=[Convert]::ToBase64String($bytes);Signature=[Convert]::ToBase64String($signature)}|ConvertTo-Json))
} finally { $rsa.Dispose(); $public.Dispose() }
