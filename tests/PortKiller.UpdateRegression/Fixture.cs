using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using PortKiller.UpdateNotifier;
using PortKiller.Updates;

namespace PortKiller.UpdateRegression;

internal sealed class Fixture : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "PortKiller-UpdateRegression-" + Guid.NewGuid().ToString("N"));
    internal RSA Key { get; } = RSA.Create(2048);
    internal UpdateOptions Options { get; }
    internal string Public => Path.Combine(Root, "stable");
    internal bool BlockManifest { get; set; }
    internal Fixture()
    {
        Directory.CreateDirectory(Root); Directory.CreateDirectory(Public);
        Options = new() { Enabled = true, AllowHttp = true, ManifestUrl = "http://127.0.0.1/stable/manifest.json", PublicKeyPem = Key.ExportSubjectPublicKeyInfoPem() };
    }
    internal byte[] Package(byte[]? executable = null, byte dependencyByte = 1)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (string file in ReleasePackage.Files)
            {
                using var stream = zip.CreateEntry(file).Open();
                stream.Write(file == "PortKiller.exe" ? executable ?? File.ReadAllBytes(typeof(Fixture).Assembly.Location) : new byte[] { dependencyByte, 2, 3, 4 });
            }
        return output.ToArray();
    }
    internal byte[] Envelope(string version, long sequence, byte[] package, string[]? notes = null, DateTimeOffset? expires = null)
    {
        var manifest = new UpdateManifest { Version = version, Sequence = sequence, PackageUrl = new Uri(new Uri(Options.ManifestUrl), $"PortKiller-{version}-win-x64.zip").AbsoluteUri,
            Size = package.Length, Sha256 = Convert.ToHexString(SHA256.HashData(package)), PublishedUtc = DateTimeOffset.UtcNow.AddMinutes(-1), ExpiresUtc = expires ?? DateTimeOffset.UtcNow.AddDays(30), ReleaseNotes = notes ?? [] };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ProtocolJsonContext.Default.UpdateManifest);
        return JsonSerializer.SerializeToUtf8Bytes(new SignedUpdate { Payload = Convert.ToBase64String(bytes), Signature = Convert.ToBase64String(Key.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) }, ProtocolJsonContext.Default.SignedUpdate);
    }
    internal (string Manifest, string Package) Stage(string version, long sequence, byte[] package)
    {
        string directory = Path.Combine(Root, "stage-" + sequence); Directory.CreateDirectory(directory);
        string manifest = Path.Combine(directory, "manifest.json"), path = Path.Combine(directory, $"PortKiller-{version}-win-x64.zip");
        File.WriteAllBytes(path, package); File.WriteAllBytes(manifest, Envelope(version, sequence, package)); return (manifest, path);
    }
    internal async Task<WebApplication> HostAsync()
    {
        var app = NotifierHost.Create(Public, Options, "http://127.0.0.1:0");
        app.MapGet("/stable/manifest.json", () => BlockManifest ? Results.StatusCode(503) : File.Exists(Path.Combine(Public, "manifest.json"))
            ? Results.File(Path.Combine(Public, "manifest.json"), "application/json", enableRangeProcessing: false) : Results.NotFound());
        app.MapGet("/stable/{name}", (string name) => name.StartsWith("PortKiller-") && name.EndsWith("-win-x64.zip") && Path.GetFileName(name) == name && File.Exists(Path.Combine(Public, name))
            ? Results.File(Path.Combine(Public, name), "application/zip", enableRangeProcessing: true) : Results.NotFound());
        await app.StartAsync();
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        Options.ManifestUrl = address + "/stable/manifest.json";
        return app;
    }
    internal UpdateService Service(HttpClient client, TimeProvider? clock = null) => new(Options, Path.Combine(Root, "cache"), client, "0.1", clock: clock, trustPath: Path.Combine(Root, "trust.json"));
    public void Dispose()
    {
        Key.Dispose();
        if (!Path.GetFullPath(Root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Sandbox outside temp");
        try { Directory.Delete(Root, true); } catch (IOException) { }
    }
}
internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
{ protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => action(request, token); }
internal sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => _now;
    internal void Advance(int seconds = 31) => _now += TimeSpan.FromSeconds(seconds);
}
internal static class AssertEx
{
    internal static void That(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    internal static async Task Eventually(Func<bool> condition, int milliseconds = 10000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { if (watch.ElapsedMilliseconds > milliseconds) throw new TimeoutException("Condition not reached"); await Task.Delay(20); }
    }
    internal static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
}
