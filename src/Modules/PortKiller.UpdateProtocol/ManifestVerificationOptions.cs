using System.Security.Cryptography;

namespace PortKiller.Updates;

public class ManifestVerificationOptions
{
    public bool AllowHttp { get; set; }
    public string ManifestUrl { get; set; } = "";
    public string PublicKeyPem { get; set; } = "";

    public virtual Uri Validate()
    {
        if (!Uri.TryCreate(ManifestUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps && !(AllowHttp && uri.Scheme == Uri.UriSchemeHttp)
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Query.Length != 0)
            throw new InvalidDataException("更新源必须使用 HTTPS；HTTP 需要显式启用，且不得包含凭据、查询或片段。");
        using var rsa = RSA.Create();
        rsa.ImportFromPem(PublicKeyPem);
        if (rsa.KeySize < 2048) throw new InvalidDataException("更新公钥至少需要 RSA 2048 位。");
        return uri;
    }
}
