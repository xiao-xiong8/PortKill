using System.Text.Json;
using PortKiller.Updates;
using PortKiller.UpdateTool;

try
{
    if (args.Length < 3 || args.Length % 2 != 1) throw new ArgumentException("Usage: verify|activate|inspect|recover|public-check --config file [--root dir --manifest file --package file --expected hash|none]");
    var flags = Enumerable.Range(0, (args.Length - 1) / 2).ToDictionary(i => args[i * 2 + 1], i => args[i * 2 + 2]);
    var options = JsonSerializer.Deserialize(File.ReadAllText(flags["--config"]), ProtocolJsonContext.Default.ManifestVerificationOptions)
        ?? throw new InvalidDataException("验证配置无效。");
    options.Validate();
    object? result;
    switch (args[0])
    {
        case "verify":
            var manifest = UpdateManifest.Verify(ReleaseStore.ReadManifest(flags["--manifest"]), options);
            ReleasePackage.Verify(flags["--package"], manifest);
            result = new { manifest.Version, manifest.Sequence, manifest.ExpiresUtc }; break;
        case "activate": result = new ReleaseStore(flags["--root"], options).Activate(flags["--manifest"], flags["--package"], flags["--expected"]); break;
        case "inspect": case "recover": result = new ReleaseStore(flags["--root"], options).Inspect(args[0] == "recover"); break;
        case "public-check":
            using (var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(5) }) { Timeout = TimeSpan.FromMinutes(5) })
            {
                using var response = await http.GetAsync(options.ManifestUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > 65536) throw new InvalidDataException("公网清单超限。");
                using var input = await response.Content.ReadAsStreamAsync();
                using var output = new MemoryStream();
                byte[] buffer = new byte[4096]; int read;
                while ((read = await input.ReadAsync(buffer)) != 0) { if (output.Length + read > 65536) throw new InvalidDataException("公网清单超限。"); output.Write(buffer, 0, read); }
                byte[] bytes = output.ToArray();
                var verified = UpdateManifest.Verify(bytes, options);
                if (flags.TryGetValue("--expected", out string? expected) && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) != expected)
                    throw new InvalidDataException("公网清单与发布记录不匹配。");
                string temporary = Path.GetTempFileName();
                try
                {
                    using var download = await http.GetAsync(verified.PackageUrl, HttpCompletionOption.ResponseHeadersRead);
                    download.EnsureSuccessStatusCode();
                    using (var source = await download.Content.ReadAsStreamAsync())
                    using (var file = File.Create(temporary))
                    {
                        long total = 0;
                        while ((read = await source.ReadAsync(buffer)) != 0) { total += read; if (total > verified.Size) throw new InvalidDataException("公网包超限。"); await file.WriteAsync(buffer.AsMemory(0, read)); }
                    }
                    ReleasePackage.Verify(temporary, verified);
                    using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, verified.PackageUrl);
                    rangeRequest.Headers.Range = new(0, 0);
                    using var range = await http.SendAsync(rangeRequest, HttpCompletionOption.ResponseHeadersRead);
                    if (range.StatusCode != System.Net.HttpStatusCode.PartialContent || range.Content.Headers.ContentRange?.From != 0
                        || range.Content.Headers.ContentRange?.To != 0 || range.Content.Headers.ContentRange?.Length != verified.Size)
                        throw new InvalidDataException("公网 Range 验证失败。");
                    await using var rangeStream = await range.Content.ReadAsStreamAsync();
                    byte[] one = new byte[1];
                    using var expectedByte = File.OpenRead(temporary);
                    if (await rangeStream.ReadAsync(one) != 1 || one[0] != expectedByte.ReadByte()
                        || await rangeStream.ReadAsync(one) != 0) throw new InvalidDataException("公网 Range 内容不正确。");
                }
                finally { File.Delete(temporary); }
                result = new { verified.Version, verified.Sequence, verified.ExpiresUtc, DaysRemaining = (verified.ExpiresUtc - DateTimeOffset.UtcNow).TotalDays,
                    Warning = verified.ExpiresUtc < DateTimeOffset.UtcNow.AddDays(14), Critical = verified.ExpiresUtc < DateTimeOffset.UtcNow.AddDays(7) };
            }
            break;
        default: throw new ArgumentException("未知命令。");
    }
    Console.WriteLine(JsonSerializer.Serialize(result));
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message); return 1; }
