using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using PortKiller.UpdateNotifier;
using PortKiller.Updates;
using static PortKiller.UpdateRegression.AssertEx;

namespace PortKiller.UpdateRegression;

internal static class ServerChecks
{
    internal static async Task NotificationsAndLoad()
    {
        using var fixture = new Fixture();
        await using var app = await fixture.HostAsync();
        byte[] package = fixture.Package();
        File.WriteAllBytes(Path.Combine(fixture.Public, "manifest.json"), fixture.Envelope("1.0.0", 1, package));
        var monitor = app.Services.GetRequiredService<ReleaseMonitor>();
        using var http = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = 1100 }) { Timeout = Timeout.InfiniteTimeSpan };
        using var token = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        int hints = 0;
        var events = new UpdateEventClient(http, fixture.Options.GetEventsUri()).RunAsync(() => Interlocked.Increment(ref hints), token.Token);
        await Eventually(() => hints >= 1);
        // Invalid writes and lower sequence must not be broadcast.
        File.WriteAllText(Path.Combine(fixture.Public, "manifest.json"), "broken"); await Task.Delay(1200); That(hints == 1, "Malformed manifest broadcast");
        File.WriteAllBytes(Path.Combine(fixture.Public, "manifest.json"), fixture.Envelope("1.0.0", 2, package)); await Eventually(() => hints == 2);
        File.WriteAllBytes(Path.Combine(fixture.Public, "manifest.json"), fixture.Envelope("1.0.0", 1, package)); await Task.Delay(1200); That(hints == 2, "Rollback broadcast");
        File.WriteAllBytes(Path.Combine(fixture.Public, "manifest.json"), fixture.Envelope("1.0.0", 3, package)); await Eventually(() => hints == 3);
        var connections = new List<HttpResponseMessage>();
        using var process = Process.GetCurrentProcess();
        long memoryBefore = process.WorkingSet64; var cpuBefore = process.TotalProcessorTime; var watch = Stopwatch.StartNew();
        try
        {
            foreach (int target in new[] { 100, 500, 1000 })
            {
                var opened = await Task.WhenAll(Enumerable.Range(0, target - connections.Count).Select(_ => http.GetAsync(fixture.Options.GetEventsUri(), HttpCompletionOption.ResponseHeadersRead, token.Token)));
                connections.AddRange(opened);
                await Eventually(() => monitor.Connections == target + 1, 20000);
                foreach (var response in opened)
                {
                    var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token.Token));
                    string? line; int count = 0;
                    do { line = await reader.ReadLineAsync(token.Token); count++; } while (!string.IsNullOrEmpty(line) && count < 10);
                    That(count >= 3, "Connection did not receive current signed-release hint");
                }
                process.Refresh();
                Console.WriteLine($"METRIC SSE connections={monitor.Connections} RSS={process.WorkingSet64 / 1048576d:F1}MiB");
            }
            await Task.Delay(1500, token.Token);
            process.Refresh();
            Console.WriteLine($"METRIC SSE 1000 ramp elapsed={watch.Elapsed.TotalSeconds:F2}s CPU={(process.TotalProcessorTime - cpuBefore).TotalMilliseconds:F0}ms RSS-delta={(process.WorkingSet64 - memoryBefore) / 1048576d:F1}MiB failures=0");
        }
        finally { foreach (var connection in connections) connection.Dispose(); }
        token.Cancel(); try { await events; } catch (OperationCanceledException) { }
        await Eventually(() => monitor.Connections == 0, 20000);
        // Process restart rebuilds current state from the active manifest.
        string url = new Uri(fixture.Options.ManifestUrl).GetLeftPart(UriPartial.Authority);
        await app.StopAsync();
        await using var restarted = NotifierHost.Create(fixture.Public, fixture.Options, url); await restarted.StartAsync();
        using var resumed = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int recovered = 0;
        var run = new UpdateEventClient(http, fixture.Options.GetEventsUri()).RunAsync(() => Interlocked.Increment(ref recovered), resumed.Token);
        await Eventually(() => recovered == 1); resumed.Cancel(); try { await run; } catch (OperationCanceledException) { }
    }

    internal static async Task DiscoveryLatency(int samples)
    {
        using var fixture = new Fixture(); await using var app = await fixture.HostAsync();
        byte[] package = fixture.Package();
        File.WriteAllBytes(Path.Combine(fixture.Public, "manifest.json"), fixture.Envelope("1.0.0", 1, package));
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var service = fixture.Service(http);
        using var token = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var run = service.RunAsync(token.Token);
        await Task.Delay(100); service.NotifyEnvironmentChanged();
        await Eventually(() => service.Available?.Sequence == 1);
        var latencies = new List<double>();
        var monitor = app.Services.GetRequiredService<ReleaseMonitor>(); await Eventually(() => monitor.Connections == 1);
        for (int sample = 0; sample < samples; sample++)
        {
            await Task.Delay(2100, token.Token);
            byte[] next = fixture.Envelope("1.0.0", sample + 2, package);
            var watch = Stopwatch.StartNew();
            string temporary = Path.Combine(fixture.Public, "next.tmp"); File.WriteAllBytes(temporary, next); File.Move(temporary, Path.Combine(fixture.Public, "manifest.json"), true);
            await Eventually(() => service.Available?.Sequence == sample + 2, 6000);
            latencies.Add(watch.Elapsed.TotalMilliseconds);
            if ((sample + 1) % 20 == 0) Console.WriteLine($"METRIC discovery samples={sample + 1}/{samples}");
        }
        latencies.Sort(); double p95 = latencies[(int)Math.Ceiling(samples * .95) - 1];
        Console.WriteLine($"METRIC discovery n={samples} loopback P95={p95:F1}ms max={latencies[^1]:F1}ms");
        That(p95 <= 5000, "Discovery P95 exceeded 5s");
        token.Cancel(); await run;
        await ManualLatency();
    }

    internal static async Task ManualLatency()
    {
        using var fixture = new Fixture(); await using var app = await fixture.HostAsync();
        File.WriteAllBytes(Path.Combine(fixture.Public, "manifest.json"), fixture.Envelope("1.0.0", 1, fixture.Package()));
        using var http = new HttpClient();
        // Advance only the cooldown clock; each request still uses the real loopback transport/signature/disk path.
        var clock = new TestClock(); var manual = new UpdateService(fixture.Options, Path.Combine(fixture.Root, "manual-cache"), http, "0.1", clock: clock, trustPath: Path.Combine(fixture.Root, "manual-trust.json"));
        var manualLatency = new List<double>();
        for (int sample = 0; sample < 100; sample++)
        {
            clock.Advance(); var watch = Stopwatch.StartNew();
            await manual.CheckAsync(default, false);
            That(manual.Snapshot.Stage == UpdateStage.Available, "Manual check failed");
            manualLatency.Add(watch.Elapsed.TotalMilliseconds);
        }
        manualLatency.Sort();
        Console.WriteLine($"METRIC manual check n=100 cooldown-clock-advanced loopback P95={manualLatency[94]:F1}ms max={manualLatency[^1]:F1}ms");
        That(manualLatency[94] <= 2000, "Manual check P95 exceeded 2s");
    }
}
