using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace PortKiller.Updates;

// 配置随应用分发；服务器只能发布签名清单，不能更改信任公钥或安装路径。
public sealed class UpdateOptions : ManifestVerificationOptions
{
    private const string ProductionManifestUrl = "https://xiaoxiongupdate.duckdns.org/stable/manifest.json";

    public bool Enabled { get; set; }
    public bool UseSystemProxy { get; set; } = true;
    public bool RealtimeEnabled { get; set; } = true;
    public string EventsUrl { get; set; } = "";
    public string ClientReportUrl { get; set; } = "";
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
        UpdateOptions options;
        if (File.Exists(path))
        {
            byte[] bytes = File.ReadAllBytes(path);
            bool hasClientReportUrl = false;
            try
            {
                using var document = JsonDocument.Parse(bytes);
                hasClientReportUrl = document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty(nameof(ClientReportUrl), out _);
            }
            catch (JsonException)
            {
                // Keep the existing deserialization error and message for malformed settings.
            }

            options = JsonSerializer.Deserialize(bytes, UpdateJsonContext.Default.UpdateOptions)
                ?? throw new InvalidDataException("更新配置为空。");

            // Older official packages did not contain ClientReportUrl. Use the embedded
            // production endpoint in memory only; never overwrite a user's settings file.
            // Custom feeds remain opt-in so reports cannot be sent to an unintended host.
            if (!hasClientReportUrl && string.Equals(options.ManifestUrl, ProductionManifestUrl, StringComparison.OrdinalIgnoreCase))
                options.ClientReportUrl = LoadEmbedded().ClientReportUrl;
        }
        else
        {
            options = LoadEmbedded();
        }

        // Updates preserve the external settings file, including the original production cadence.
        if (string.Equals(options.ManifestUrl, ProductionManifestUrl, StringComparison.OrdinalIgnoreCase) && options.PollSeconds == 300)
            options.PollSeconds = 30;
        return options;
    }

    private static UpdateOptions LoadEmbedded()
    {
        using var input = typeof(UpdateOptions).Assembly.GetManifestResourceStream("PortKiller.Updates.ProductionFeed.json")
            ?? throw new InvalidDataException("内置更新配置缺失。");
        return JsonSerializer.Deserialize(input, UpdateJsonContext.Default.UpdateOptions)
            ?? throw new InvalidDataException("内置更新配置为空。");
    }

    // Reporting configuration is validated independently so it can never disable updates.
    public Uri? GetClientReportUri()
    {
        if (string.IsNullOrWhiteSpace(ClientReportUrl)) return null;
        if (!Uri.TryCreate(ClientReportUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && !(AllowHttp && uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp))
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("客户端上报地址必须是无凭据、查询参数和片段的 HTTPS 地址。");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }
}
