using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace PortKiller.Updates;

// 配置随应用分发；服务器只能发布签名清单，不能更改信任公钥或安装路径。
public sealed class UpdateOptions
{
    public bool Enabled { get; set; }
    public bool AllowHttp { get; set; }
    public string ManifestUrl { get; set; } = "";
    public string PublicKeyPem { get; set; } = "";
    public int PollSeconds { get; set; } = 300;
    public int CheckTimeoutSeconds { get; set; } = 8;
    public int DownloadTimeoutSeconds { get; set; } = 180;

    internal Uri Validate()
    {
        if (!Uri.TryCreate(ManifestUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps && !(AllowHttp && uri.Scheme == Uri.UriSchemeHttp)
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Query.Length != 0)
            throw new InvalidDataException("更新源必须是 HTTPS；仅显式启用时允许 HTTP，且地址不得包含凭据或查询参数。");
        if (PollSeconds is < 60 or > 86400 || CheckTimeoutSeconds is < 1 or > 30
            || DownloadTimeoutSeconds is < 10 or > 1800)
            throw new InvalidDataException("更新超时或轮询间隔越界。");
        using var rsa = RSA.Create();
        rsa.ImportFromPem(PublicKeyPem);
        if (rsa.KeySize < 2048) throw new InvalidDataException("更新公钥至少需要 RSA 2048 位。");
        return uri;
    }

    public static UpdateOptions Load(string directory)
    {
        string path = Path.Combine(directory, "update-settings.json");
        return File.Exists(path)
            ? JsonSerializer.Deserialize(File.ReadAllText(path), UpdateJsonContext.Default.UpdateOptions)
                ?? throw new InvalidDataException("更新配置为空。")
            : new UpdateOptions();
    }
}
