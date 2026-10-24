using System.Net;
using System.Text;
using PortKiller.Updates;
using static PortKiller.UpdateRegression.AssertEx;

namespace PortKiller.UpdateRegression;

internal static class ClientChecks
{
    internal static async Task IdentityAndLease()
    {
        using var fixture = new Fixture();
        fixture.Options.AllowHttp = false; fixture.Options.ManifestUrl = "https://updates.example/stable/manifest.json";
        byte[] package = fixture.Package(); int generation = 1, downloads = 0, manifests = 0; bool modified = true, corrupt = false;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".zip")) { downloads++; return Task.FromResult(Bytes(corrupt ? [0] : package)); }
            manifests++;
            if (!modified) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            var response = Bytes(fixture.Envelope(generation == 1 ? "1.1" : "1.2", generation, package)); response.Headers.ETag = new("\"" + generation + "\""); return Task.FromResult(response);
        }));
        var clock = new TestClock(); var service = fixture.Service(http, clock);
        await service.CheckAsync(default); That(service.Ready?.Version == "1.1", "A not ready");
        using (var lease = service.TryAcquireInstallation())
        {
            That(lease?.Update.Manifest.Version == "1.1", "Lease missing"); generation = 2; clock.Advance();
            int before = manifests; await service.CheckAsync(default, false); That(before == manifests, "Check advanced trust while confirmation open");
        }
        // Remove cached ZIP so B must be downloaded even though this fixture reuses the same content.
        File.Delete(service.ReadyPackage!);
        await service.CheckAsync(default, false);
        That(service.Available?.Version == "1.2" && service.Ready == null, "B retained old Ready A");
        modified = false; clock.Advance(); await service.CheckAsync(default, false);
        That(service.Snapshot.Stage == UpdateStage.Available && service.Ready == null, "304 resurrected A");
        corrupt = true; await service.DownloadAvailableAsync(default); That(service.Ready == null && service.Snapshot.Stage == UpdateStage.Failed, "Failed B left A installable");
        corrupt = false; await service.DownloadAvailableAsync(default); That(service.Ready?.Sequence == 2, "B recovery failed");
        int beforeRenewal = downloads; generation = 3; modified = true; clock.Advance(); await service.CheckAsync(default, false);
        That(service.Prepared?.Identity.Sequence == 3 && downloads == beforeRenewal, "Renewal failed to reuse ZIP/update identity");
    }

    internal static async Task PendingAndSwitch()
    {
        using var fixture = new Fixture(); fixture.Options.RealtimeEnabled = false;
        int checks = 0; bool available = false, enabled = true;
        using var http = new HttpClient(); using var cancellation = new CancellationTokenSource();
        var coordinator = new UpdateCoordinator(fixture.Options, http, () => enabled, () => available, _ => { Interlocked.Increment(ref checks); return Task.FromResult(true); });
        var run = coordinator.RunAsync(cancellation.Token);
        for (int i = 0; i < 10000; i++) coordinator.RequestCheck();
        await Task.Delay(150); That(checks == 0, "Busy check executed");
        available = true; await Eventually(() => checks == 1); await Task.Delay(200); That(checks == 1, "Unbounded duplicate checks");
        enabled = false; coordinator.RequestCheck(); await Task.Delay(1200); That(checks == 1, "Automatic disabled still checked");
        enabled = true; coordinator.RequestCheck(); await Eventually(() => checks == 2);
        cancellation.Cancel(); try { await run; } catch (OperationCanceledException) { }
    }

    internal static async Task BackgroundAndRetryAfter()
    {
        using var fixture = new Fixture(); int calls = 0;
        var callerContext = new SynchronizationContext();
        using var http = new HttpClient(new Handler((_, _) =>
        {
            That(SynchronizationContext.Current != callerContext, "HTTP/validation work entered caller context");
            calls++; var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(120)); return Task.FromResult(response);
        }));
        var clock = new TestClock(); var service = fixture.Service(http, clock);
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(callerContext);
        var first = service.CheckAsync(default, false);
        SynchronizationContext.SetSynchronizationContext(previous);
        await first; That(calls == 1, "Long Retry-After was immediately retried");
        clock.Advance(); await service.CheckAsync(default, false); That(calls == 1, "Manual bypassed Retry-After");
        for (int i = 0; i < 4; i++) clock.Advance();
        await service.CheckAsync(default, false); That(calls == 2, "Did not resume after Retry-After");
    }

    internal static async Task EventBounds()
    {
        int hints = 0;
        string sample = ": heartbeat\r\n\r\nevent: release\r\ndata: {\"schema\":1,\"channel\":\"stable\",\r\ndata: \"version\":\"1.0\",\"sequence\":999999999}\r\n\r\n";
        await UpdateEventClient.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(sample)), () => hints++, default);
        That(hints == 1, "CRLF/multiline parse failed");
        foreach (string invalid in new[] { new string('x', 4097), "event: release\ndata: broken\n\n", string.Concat(Enumerable.Repeat("data: " + new string('x', 3000) + "\n", 3)) })
        {
            bool rejected = false;
            try { await UpdateEventClient.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(invalid)), () => hints++, default); }
            catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException) { rejected = true; }
            That(rejected, "Unbounded/malformed event accepted");
        }
    }

    internal static async Task FinalRetryAfterIsRetained()
    {
        using var fixture = new Fixture(); fixture.Options.RealtimeEnabled = false;
        int requests = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(20)); return Task.FromResult(response);
        }));
        var clock = new TestClock();
        var service = new UpdateService(fixture.Options, Path.Combine(fixture.Root, "cache"), http, "0.1",
            delay: (_, _) => Task.CompletedTask, clock: clock, trustPath: Path.Combine(fixture.Root, "trust.json"));
        await service.CheckAsync(default, false); That(requests == 3, "Retry count wrong");
        using var cancellation = new CancellationTokenSource(); var run = service.RunAsync(cancellation.Token);
        await Task.Delay(100); service.NotifyEnvironmentChanged(); clock.Advance(3);
        await Task.Delay(1200); That(requests == 3, "Push bypassed final short Retry-After");
        clock.Advance(); await Eventually(() => requests == 6);
        cancellation.Cancel(); await run;
    }

    internal static async Task PauseRetainsNotification()
    {
        using var fixture = new Fixture(); fixture.Options.RealtimeEnabled = false;
        int generation = 1, manifests = 0, downloads = 0; byte[] package = fixture.Package();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith(".zip"))
            { Interlocked.Increment(ref manifests); return Task.FromResult(Bytes(fixture.Envelope(generation == 1 ? "1.1" : "1.2", generation, package))); }
            int count = Interlocked.Increment(ref downloads);
            if (count == 1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new PauseStream(package)) });
            int offset = (int)(request.Headers.Range?.Ranges.Single().From ?? 0);
            var response = Bytes(package[offset..]);
            response.StatusCode = HttpStatusCode.PartialContent;
            response.Content.Headers.ContentRange = new(offset, package.Length - 1, package.Length);
            return Task.FromResult(response);
        }));
        var clock = new TestClock(); var service = fixture.Service(http, clock);
        using var cancellation = new CancellationTokenSource(); var run = service.RunAsync(cancellation.Token);
        await service.CheckAsync(default, false);
        void Pause(UpdateSnapshot snapshot) { if (snapshot.Stage == UpdateStage.Downloading && snapshot.DownloadedBytes > 0) service.PauseDownload(); }
        service.Changed += Pause; await service.DownloadAvailableAsync(default);
        That(service.Snapshot.Stage == UpdateStage.Paused, "Not paused");
        generation = 2; clock.Advance(); service.NotifyEnvironmentChanged();
        await Task.Delay(1200); That(downloads == 1 && manifests == 1 && service.Snapshot.Stage == UpdateStage.Paused, "Notification resumed paused download");
        service.Changed -= Pause; await service.DownloadAvailableAsync(default);
        await Eventually(() => service.Available?.Sequence == 2);
        That(service.Prepared?.Identity.Sequence == 2 && downloads == 2 && manifests == 2, "Pending notification lost after resume");
        cancellation.Cancel(); await run;
    }

    private sealed class PauseStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (Position > 0) await Task.Delay(Timeout.Infinite, token);
            return await base.ReadAsync(buffer[..Math.Min(buffer.Length, 8)], token);
        }
    }

    internal static async Task ReconnectAndFallback()
    {
        int attempts = 0, hints = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            int attempt = Interlocked.Increment(ref attempts);
            var response = Bytes(Encoding.UTF8.GetBytes(attempt == 1 ? "event: release\ndata: broken\n\n" : "event: release\ndata: {\"schema\":1,\"channel\":\"stable\",\"version\":\"1.0\",\"sequence\":1}\n\n"));
            response.Content.Headers.ContentType = new("text/event-stream"); return Task.FromResult(response);
        }));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = new UpdateEventClient(http, new Uri("http://127.0.0.1/events")).RunAsync(() => Interlocked.Increment(ref hints), cancellation.Token);
        await Eventually(() => hints > 0); That(attempts >= 2, "Malformed/disconnected SSE did not reconnect");
        cancellation.Cancel(); try { await run; } catch (OperationCanceledException) { }
        using var fixture = new Fixture(); int eventRequests = 0;
        using var unavailable = new HttpClient(new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("events")) { eventRequests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)); }
            return Task.FromResult(Bytes(fixture.Envelope("1.0", 1, fixture.Package())));
        }));
        var service = fixture.Service(unavailable);
        using var lifetime = new CancellationTokenSource(); var serviceRun = service.RunAsync(lifetime.Token);
        await Task.Delay(100); service.NotifyEnvironmentChanged();
        await Eventually(() => eventRequests == 1); await Task.Delay(1200);
        That(eventRequests == 1 && service.Snapshot.Stage == UpdateStage.Available, "SSE 404 broke normal discovery/fallback");
        lifetime.Cancel(); await serviceRun;
    }
}
