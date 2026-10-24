using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PortKiller.Updates;

public sealed class UpdateManifest
{
    public int Schema { get; set; } = 2;
    public string Version { get; set; } = "";
    public string Runtime { get; set; } = "win-x64";
    public string PackageUrl { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public long Sequence { get; set; }
    public DateTimeOffset PublishedUtc { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }
    // 可选字段，旧清单继续可读；文字随 payload 签名，界面只按纯文本呈现。
    public string[] ReleaseNotes { get; set; } = [];

    [JsonIgnore]
    public string PayloadSha256 { get; private set; } = "";

    public static Version ParseVersion(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !System.Version.TryParse(text, out var version) || version.Minor < 0
            || text.Length > 32 || text.Any(c => !char.IsAsciiDigit(c) && c != '.'))
            throw new InvalidDataException("仅支持两至四段数字正式版本号。");
        return new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
    }

    public static UpdateManifest Verify(byte[] envelope, ManifestVerificationOptions options, DateTimeOffset? now = null, bool ignoreExpiry = false)
    {
        if (envelope.Length > 65536) throw new InvalidDataException("更新清单过大。");
        var signed = JsonSerializer.Deserialize(envelope, ProtocolJsonContext.Default.SignedUpdate)
            ?? throw new InvalidDataException("更新清单为空。");
        if (string.IsNullOrEmpty(signed.Payload) || string.IsNullOrEmpty(signed.Signature)) throw new InvalidDataException("更新签名为空。");
        byte[] payload = Convert.FromBase64String(signed.Payload);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(options.PublicKeyPem);
        if (!rsa.VerifyData(payload, Convert.FromBase64String(signed.Signature),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new InvalidDataException("更新清单签名不可信。");
        var manifest = JsonSerializer.Deserialize(payload, ProtocolJsonContext.Default.UpdateManifest)
            ?? throw new InvalidDataException("更新内容为空。");
        _ = ParseVersion(manifest.Version);
        if (manifest.ReleaseNotes is null || manifest.ReleaseNotes.Length > 20
            || manifest.ReleaseNotes.Any(note => string.IsNullOrWhiteSpace(note) || note.Length > 300))
            throw new InvalidDataException("更新说明最多 20 项，每项不超过 300 字。 ");
        var origin = options.Validate();
        bool currentSchema = manifest.Schema == 2;
        if (manifest.Schema == 1 && origin.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("HTTP 更新必须使用带有效期和发布序号的签名清单。");
        if (manifest.Schema is not (1 or 2) || manifest.Runtime != "win-x64"
            || !Uri.TryCreate(manifest.PackageUrl, UriKind.Absolute, out var package)
            || package.Scheme != origin.Scheme || !package.Authority.Equals(origin.Authority, StringComparison.OrdinalIgnoreCase)
            || package.UserInfo.Length != 0 || package.Fragment.Length != 0 || package.Query.Length != 0
            || manifest.Size is <= 0 or > 268435456
            || string.IsNullOrEmpty(manifest.Sha256) || manifest.Sha256.Length != 64 || !manifest.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("更新包平台、地址、大小或摘要不合法。");
        if (currentSchema)
        {
            DateTimeOffset utcNow = now ?? DateTimeOffset.UtcNow;
            TimeSpan lifetime = manifest.ExpiresUtc - manifest.PublishedUtc;
            if (manifest.Sequence <= 0 || manifest.PublishedUtc > utcNow.AddMinutes(5)
                || !ignoreExpiry && manifest.ExpiresUtc <= utcNow || lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromDays(180))
                throw new InvalidDataException("更新清单发布序号或有效期无效。");
        }
        manifest.PayloadSha256 = Convert.ToHexString(SHA256.HashData(payload));
        return manifest;
    }
}
