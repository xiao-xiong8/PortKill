using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace PortKiller.Updates;

// 配置随应用分发；服务器只能发布签名清单，不能更改信任公钥或安装路径。
public sealed class UpdateOptions : ManifestVerificationOptions
{
    public bool Enabled { get; set; }
    public bool UseSystemProxy { get; set; } = true;
    public bool RealtimeEnabled { get; set; } = true;
    public string EventsUrl { get; set; } = "";
    public int PollSeconds { get; set; } = 30;
    public int CheckTimeoutSeconds { get; set; } = 8;
    public int DownloadTimeoutSeconds { get; set; } = 180;

    public override Uri Validate()
    {
        if (!Uri.TryCreate(ManifestUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps && !(AllowHttp && uri.Scheme == Uri.UriSchemeHttp)
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Query.Length != 0)
            throw new InvalidDataException("更新源必须是 HTTPS；仅显式启用时允许 HTTP，且地址不得包含凭据或查询参数。");
        if (PollSeconds is < 30 or > 86400 || CheckTimeoutSeconds is < 1 or > 30
            || DownloadTimeoutSeconds is < 10 or > 1800)
            throw new InvalidDataException("更新超时或轮询间隔越界。");
        using var rsa = RSA.Create();
        rsa.ImportFromPem(PublicKeyPem);
        if (rsa.KeySize < 2048) throw new InvalidDataException("更新公钥至少需要 RSA 2048 位。");
        if (RealtimeEnabled) _ = GetEventsUri(uri);
        return uri;
    }

    public Uri GetEventsUri(Uri? origin = null)
    {
        origin ??= new Uri(ManifestUrl, UriKind.Absolute);
        var events = string.IsNullOrWhiteSpace(EventsUrl) ? new Uri(origin, "events") : new Uri(EventsUrl, UriKind.Absolute);
        if (events.Scheme != origin.Scheme || !events.Authority.Equals(origin.Authority, StringComparison.OrdinalIgnoreCase)
            || events.UserInfo.Length != 0 || events.Query.Length != 0 || events.Fragment.Length != 0)
            throw new InvalidDataException("事件地址必须与更新清单同源且不含凭据、查询或片段。");
        return events;
    }

    public static UpdateOptions Load(string directory)
    {
        string path = Path.Combine(directory, "update-settings.json");
        using var input = File.Exists(path) ? File.OpenRead(path)
            : typeof(UpdateOptions).Assembly.GetManifestResourceStream("PortKiller.Updates.ProductionFeed.json")
                ?? throw new InvalidDataException("内置更新配置缺失。");
        var options = JsonSerializer.Deserialize(input, UpdateJsonContext.Default.UpdateOptions)
            ?? throw new InvalidDataException("更新配置为空。");
        // Updates preserve the external settings file, including the original production cadence.
        if (options.ManifestUrl == "https://xiaoxiongupdate.duckdns.org/stable/manifest.json" && options.PollSeconds == 300)
            options.PollSeconds = 30;
        return options;
    }
}
