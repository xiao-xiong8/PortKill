using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PortKiller.UpdateNotifier;
using PortKiller.Updates;
using static PortKiller.UpdateRegression.AssertEx;

namespace PortKiller.UpdateRegression;

internal static class ProductionFeedChecks
{
    internal static Task DefaultsAndOverrides()
    {
        using var fixture = new Fixture();
        var defaults = UpdateOptions.Load(fixture.Root);
        var uri = defaults.Validate();
        That(defaults.Enabled && defaults.RealtimeEnabled && !defaults.AllowHttp && !defaults.UseSystemProxy,
            "Production updates must be enabled, use verified HTTPS and direct transport");
        That(defaults.PollSeconds == 30, "Production polling is not 30 seconds");
        That(uri.Host == "xiaoxiongupdate.duckdns.org" && uri.AbsolutePath == "/stable/manifest.json"
            && defaults.GetEventsUri().AbsoluteUri == "https://xiaoxiongupdate.duckdns.org/stable/events",
            "Production defaults point at a placeholder or test feed");
        using var key = RSA.Create(); key.ImportFromPem(defaults.PublicKeyPem);
        That(key.KeySize == 3072, "Production trust anchor was not loaded");
        defaults.Enabled = false;
        That(UpdateOptions.Load(fixture.Root).Enabled, "Default configuration was shared and mutated");

        string path = Path.Combine(fixture.Root, "update-settings.json");
        File.WriteAllText(path, JsonSerializer.Serialize(fixture.Options, UpdateJsonContext.Default.UpdateOptions));
        That(UpdateOptions.Load(fixture.Root).ManifestUrl == fixture.Options.ManifestUrl,
            "Explicit custom configuration was replaced by production defaults");
        fixture.Options.PollSeconds = 300;
        File.WriteAllText(path, JsonSerializer.Serialize(fixture.Options, UpdateJsonContext.Default.UpdateOptions));
        That(UpdateOptions.Load(fixture.Root).PollSeconds == 300, "Custom source polling was changed");
        defaults.PollSeconds = 300;
        File.WriteAllText(path, JsonSerializer.Serialize(defaults, UpdateJsonContext.Default.UpdateOptions));
        var migrated = UpdateOptions.Load(fixture.Root);
        That(migrated.PollSeconds == 30 && !migrated.Enabled,
            "Preserved legacy production configuration did not migrate its cadence or lost explicit disable");
        File.WriteAllText(path, "{\"Enabled\":false}");
        That(!UpdateOptions.Load(fixture.Root).Enabled, "Explicit disable was ignored");
        File.WriteAllText(path, "broken");
        bool rejected = false;
        try { _ = UpdateOptions.Load(fixture.Root); } catch (JsonException) { rejected = true; }
        That(rejected, "Invalid explicit configuration silently fell back to production");
        return Task.CompletedTask;
    }

    internal static async Task EmptyFeedThenRealtimePublication()
    {
        using var fixture = new Fixture();
        await using var app = await fixture.HostAsync();
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var clock = new TestClock();
        var service = fixture.Service(http, clock);
        await service.CheckAsync(lifetime.Token, false);
        That(service.Snapshot.Stage == UpdateStage.NoRelease && service.Snapshot.Failure == UpdateFailure.None
            && service.Available == null && service.Prepared == null, "Empty feed was reported as failure or latest");
        var run = service.RunAsync(lifetime.Token);
        try
        {
            await Task.Delay(100, lifetime.Token);
            clock.Advance(); service.NotifyEnvironmentChanged();
            await Eventually(() => app.Services.GetRequiredService<ReleaseMonitor>().Connections == 1);
            clock.Advance();
            byte[] package = fixture.Package();
            File.WriteAllBytes(Path.Combine(fixture.Public, "PortKiller-1.1-win-x64.zip"), package);
            string[] notes = ["新增：本次发布的真实说明", "修复：来自已签名清单的内容"];
            File.WriteAllBytes(Path.Combine(fixture.Public, "manifest.json"), fixture.Envelope("1.1", 1, package, notes));
            await Eventually(() => service.Snapshot.Stage == UpdateStage.Available);
            That(service.Available!.ReleaseNotes.SequenceEqual(notes) && service.Prepared == null,
                "Realtime publication lost signed notes or downloaded without confirmation");
            await service.DownloadAvailableAsync(lifetime.Token);
            That(service.Snapshot.Stage == UpdateStage.Ready && service.Prepared?.Manifest.Sequence == 1,
                "Published package was not downloaded and verified");
        }
        finally { lifetime.Cancel(); await run; }
    }

    internal static async Task MissingFeedClearsStaleReadyAndEtag()
    {
        using var fixture = new Fixture();
        fixture.Options.AllowHttp = false; fixture.Options.ManifestUrl = "https://updates.example/stable/manifest.json";
        byte[] package = fixture.Package();
        byte[] signedRelease = fixture.Envelope("1.1", 1, package);
        bool missing = false, afterMissing = false, sentOldEtag = false;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".zip")) return Task.FromResult(Bytes(package));
            if (missing) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (afterMissing) sentOldEtag = request.Headers.IfNoneMatch.Count != 0;
            var response = Bytes(signedRelease); response.Headers.ETag = new("\"release-1\"");
            return Task.FromResult(response);
        }));
        var clock = new TestClock(); var service = fixture.Service(http, clock);
        await service.CheckAsync(default);
        That(service.Prepared != null, "Initial signed package was not prepared");
        clock.Advance(); missing = true; await service.CheckAsync(default, false);
        That(service.Snapshot.Stage == UpdateStage.NoRelease && service.Prepared == null && service.Available == null,
            "404 left a stale version ready for installation");
        clock.Advance(); missing = false; afterMissing = true; await service.CheckAsync(default, false);
        That(!sentOldEtag && service.Snapshot.Stage == UpdateStage.Ready,
            "Feed recovery reused a stale ETag or lost the validated cache");
    }

    // Explicit opt-in: this check reads the public production site and never publishes or installs.
    internal static async Task Online(string install)
    {
        using var fixture = new Fixture();
        var options = UpdateOptions.Load(install); options.Validate();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var service = new UpdateService(options, Path.Combine(fixture.Root, "cache"), current: "0.1",
            trustPath: Path.Combine(fixture.Root, "trust.json"));
        await service.CheckAsync(deadline.Token, false);
        That(service.Snapshot.Stage is UpdateStage.NoRelease or UpdateStage.Available or UpdateStage.Latest or UpdateStage.Ready,
            $"Production manifest failed: {service.Snapshot.ErrorDetail}");
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = options.UseSystemProxy, AllowAutoRedirect = false });
        using var firstFrame = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        firstFrame.CancelAfter(TimeSpan.FromSeconds(options.CheckTimeoutSeconds));
        var watch = Stopwatch.StartNew();
        using var response = await client.GetAsync(options.GetEventsUri(), HttpCompletionOption.ResponseHeadersRead, firstFrame.Token);
        response.EnsureSuccessStatusCode();
        That(response.Content.Headers.ContentType?.MediaType == "text/event-stream", "Production response is not SSE");
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(firstFrame.Token));
        That(await reader.ReadLineAsync(firstFrame.Token) == ": connected" && await reader.ReadLineAsync(firstFrame.Token) == "",
            "Production SSE initial frame is incomplete");
        using var key = RSA.Create(); key.ImportFromPem(options.PublicKeyPem);
        Console.WriteLine(JsonSerializer.Serialize(new { Manifest = options.ManifestUrl, ManifestStage = service.Snapshot.Stage.ToString(),
            Events = options.GetEventsUri().AbsoluteUri, SseFirstFrameMilliseconds = watch.Elapsed.TotalMilliseconds,
            PublicKeySha256 = Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())), options.UseSystemProxy }));
    }

    // The real signed upload artifacts run through the client in an isolated cache, without publishing.
    internal static async Task PreparedRelease(string release, string install)
    {
        using var fixture = new Fixture();
        var options = UpdateOptions.Load(install);
        byte[] envelope = await File.ReadAllBytesAsync(Path.Combine(release, "manifest.json"));
        var manifest = UpdateManifest.Verify(envelope, options);
        byte[] package = await File.ReadAllBytesAsync(Path.Combine(release, Path.GetFileName(new Uri(manifest.PackageUrl).AbsolutePath)));
        int downloads = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri == options.ManifestUrl) return Task.FromResult(Bytes(envelope));
            That(request.RequestUri.AbsoluteUri == manifest.PackageUrl, "Unexpected artifact request");
            Interlocked.Increment(ref downloads);
            return Task.FromResult(Bytes(package));
        }));
        string current = FileVersionInfo.GetVersionInfo(Path.Combine(install, "PortKiller.exe")).FileVersion!;
        var service = new UpdateService(options, Path.Combine(fixture.Root, "prepared-cache"), http, current,
            trustPath: Path.Combine(fixture.Root, "prepared-trust.json"));
        await service.CheckAsync(default, false);
        That(service.Snapshot.Stage == UpdateStage.Available && downloads == 0
            && service.Available!.ReleaseNotes.SequenceEqual(manifest.ReleaseNotes), "Real update discovery or signed notes failed");
        await service.DownloadAvailableAsync(default);
        That(service.Snapshot.Stage == UpdateStage.Ready && downloads == 1
            && service.Prepared?.Manifest.Sha256 == manifest.Sha256, "Real signed update package did not reach verified Ready");
        ReleasePackage.Verify(service.Prepared!.PackagePath, manifest);
        Console.WriteLine($"PASS real prepared update: {current} -> {manifest.Version}; production key, signed notes, explicit download, four-file ZIP, SHA-256 and executable version verified; no publication or installation");
    }
}
