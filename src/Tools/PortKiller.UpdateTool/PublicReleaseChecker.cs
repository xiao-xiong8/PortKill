using System.Net;
using System.Security.Cryptography;
using PortKiller.Updates;

namespace PortKiller.UpdateTool;

public static class PublicReleaseChecker
{
    public static async Task<object> CheckAsync(ManifestVerificationOptions options, string? expected,
        CancellationToken token)
    {
        options.Validate();
        if (expected != null && (expected.Length != 64 || !expected.All(Uri.IsHexDigit)))
            throw new ArgumentException("The expected manifest digest is invalid.");
        using var http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(5)
        }) { Timeout = Timeout.InfiniteTimeSpan };
        using var response = await http.GetAsync(options.ManifestUrl, HttpCompletionOption.ResponseHeadersRead, token);
        RequireFullResponse(response);
        if (response.Content.Headers.ContentLength > 65_536) throw new InvalidDataException("公网清单超限。");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        byte[] buffer = new byte[64 * 1024]; int read;
        while ((read = await input.ReadAsync(buffer, token)) != 0)
        {
            if (output.Length + read > 65_536) throw new InvalidDataException("公网清单超限。");
            output.Write(buffer, 0, read);
        }
        byte[] bytes = output.ToArray();
        var verified = UpdateManifest.Verify(bytes, options);
        string digest = Convert.ToHexString(SHA256.HashData(bytes));
        if (expected != null && !digest.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("公网清单与发布记录不匹配。");
        string temporary = Path.GetTempFileName();
        try
        {
            using var download = await http.GetAsync(verified.PackageUrl, HttpCompletionOption.ResponseHeadersRead, token);
            RequireFullResponse(download);
            if (download.Content.Headers.ContentLength is { } length && length != verified.Size)
                throw new InvalidDataException("公网包长度不匹配。");
            await using (var source = await download.Content.ReadAsStreamAsync(token))
            await using (var file = File.Create(temporary))
            {
                long total = 0;
                while ((read = await source.ReadAsync(buffer, token)) != 0)
                {
                    total += read;
                    if (total > verified.Size) throw new InvalidDataException("公网包超限。");
                    await file.WriteAsync(buffer.AsMemory(0, read), token);
                }
            }
            token.ThrowIfCancellationRequested();
            ReleasePackage.Verify(temporary, verified);
            using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, verified.PackageUrl);
            rangeRequest.Headers.Range = new(0, 0);
            using var range = await http.SendAsync(rangeRequest, HttpCompletionOption.ResponseHeadersRead, token);
            if (range.StatusCode != HttpStatusCode.PartialContent || range.Content.Headers.ContentRange?.Unit != "bytes"
                || range.Content.Headers.ContentRange.From != 0 || range.Content.Headers.ContentRange.To != 0
                || range.Content.Headers.ContentRange.Length != verified.Size)
                throw new InvalidDataException("公网 Range 验证失败。");
            await using var rangeStream = await range.Content.ReadAsStreamAsync(token);
            byte[] one = new byte[1];
            using var expectedByte = File.OpenRead(temporary);
            if (await rangeStream.ReadAsync(one, token) != 1 || one[0] != expectedByte.ReadByte()
                || await rangeStream.ReadAsync(one, token) != 0)
                throw new InvalidDataException("公网 Range 内容不正确。");
        }
        finally { File.Delete(temporary); }
        token.ThrowIfCancellationRequested();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new { verified.Version, verified.Sequence, verified.ExpiresUtc,
            ManifestSha256 = digest, PackageSha256 = verified.Sha256.ToUpperInvariant(),
            DaysRemaining = (verified.ExpiresUtc - now).TotalDays,
            Warning = verified.ExpiresUtc < now.AddDays(14), Critical = verified.ExpiresUtc < now.AddDays(7) };
    }

    private static void RequireFullResponse(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.OK) throw new InvalidDataException("公网源未返回完整文件。");
    }
}
