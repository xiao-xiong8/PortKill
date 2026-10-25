using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace PortKiller.UpdateAdmin.Tests;

public sealed class PerformanceTests
{
    [Fact]
    [Trait("Category", "Performance")]
    public async Task ControlledPerformanceMeasurementsStayWithinLocalTargets()
    {
        var report = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["task"] = "P8-02",
            ["phase"] = "P8",
            ["status"] = "passed",
            ["platform"] = Environment.OSVersion.VersionString,
            ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            ["machine"] = Environment.MachineName,
            ["samples"] = new { api = 100, sse = 30, concurrentUploads = 2, uploadBytesPerStream = 268_435_456L }
        };

        var api = await MeasureApiAsync();
        report["api"] = api;
        Assert.True(api.P95Milliseconds <= 200, $"普通管理 API P95 超过 200ms：{api.P95Milliseconds:F1}ms");

        var sse = await MeasureSseAsync();
        report["sse"] = sse;
        Assert.True(sse.P95Milliseconds <= 1000, $"任务 SSE P95 超过 1s：{sse.P95Milliseconds:F1}ms");

        var worker = await MeasureWorkerStartupAsync();
        report["workerStartup"] = worker;
        Assert.True(worker.P95Milliseconds <= 2000, $"Worker 启动任务 P95 超过 2s：{worker.P95Milliseconds:F1}ms");

        var uploads = await MeasureConcurrentUploadMemoryAsync();
        report["concurrentUploads"] = uploads;
        Assert.True(uploads.ExtraPrivateMiB < 128, $"并发上传额外私有内存超过 128MiB：{uploads.ExtraPrivateMiB:F1}MiB");

        string? output = Environment.GetEnvironmentVariable("PORTKILL_PERF_OUTPUT");
        if (!string.IsNullOrWhiteSpace(output))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine("PERF " + JsonSerializer.Serialize(report));
    }

    private static async Task<LatencyMetric> MeasureApiAsync()
    {
        await using var app = await TestApplication.StartAsync(realHttp: true);
        await MfaTests.EnrollAsync(app);
        for (int i = 0; i < 10; i++) (await app.Client.GetAsync("/api/v1/system/status")).EnsureSuccessStatusCode();
        var values = new List<double>(100);
        for (int i = 0; i < 100; i++)
        {
            var watch = Stopwatch.StartNew();
            using var response = await app.Client.GetAsync("/api/v1/system/status");
            response.EnsureSuccessStatusCode();
            values.Add(watch.Elapsed.TotalMilliseconds);
        }
        return LatencyMetric.Create(values, "loopback HTTPS authenticated GET /api/v1/system/status");
    }

    private static async Task<LatencyMetric> MeasureSseAsync()
    {
        await using var app = await JobEventTests.StartAsync(realHttp: true);
        string id = await JobEventTests.CreateTaskAsync(app);
        app.App.Services.GetRequiredService<PortKiller.UpdateManagement.Configuration.AdminOptions>().JobStreamPollMilliseconds = 100;
        await using var stream = await EventConnection.OpenAsync(app, id);
        Assert.Equal("snapshot", (await stream.NextAsync()).Kind);
        var values = new List<double>(30);
        for (int i = 0; i < 30; i++)
        {
            var watch = Stopwatch.StartNew();
            await JobEventTests.AppendAsync(app, id, "perf_" + i);
            var frame = await stream.NextAsync();
            while (frame.Kind == "snapshot") frame = await stream.NextAsync();
            Assert.Equal("job", frame.Kind);
            values.Add(watch.Elapsed.TotalMilliseconds);
        }
        return LatencyMetric.Create(values, "loopback HTTPS persisted job event to SSE frame");
    }

    private static async Task<LatencyMetric> MeasureWorkerStartupAsync()
    {
        var values = new List<double>(5);
        for (int sample = 0; sample < 5; sample++)
        {
            await using var app = await TestApplication.StartAsync();
            await app.StopAsync();
            string executable = Path.Combine(FindRepositoryRoot(), "src", "Services", "PortKiller.UpdatePublisher.Worker", "bin", "Release", "net10.0",
                "PortKiller.UpdatePublisher.Worker" + (OperatingSystem.IsWindows() ? ".exe" : ""));
            Assert.True(File.Exists(executable), $"Worker apphost was not built: {executable}");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            start.ArgumentList.Add("--Administration:DataRoot"); start.ArgumentList.Add(app.Root);
            start.ArgumentList.Add("--UpdateTool:Enabled"); start.ArgumentList.Add("false");
            using var process = new Process { StartInfo = start };
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            process.OutputDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data?.Contains("publisher", StringComparison.OrdinalIgnoreCase) == true
                    && eventArgs.Data.Contains("started", StringComparison.OrdinalIgnoreCase)) ready.TrySetResult();
            };
            var watch = Stopwatch.StartNew();
            Assert.True(process.Start());
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            try
            {
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
                values.Add(watch.Elapsed.TotalMilliseconds);
            }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            }
        }
        return LatencyMetric.Create(values, "worker process start to publisher loop readiness");
    }

    private static async Task<UploadMemoryMetric> MeasureConcurrentUploadMemoryAsync()
    {
        await using var app = await TestApplication.StartAsync();
        await MfaTests.EnrollAsync(app);
        const long bytes = 256L * 1024 * 1024;
        var ids = new string[2];
        foreach (int index in Enumerable.Range(0, ids.Length))
        {
            using var created = await app.Client.PostAsJsonAsync("/api/v1/uploads/", new { manifestBytes = 4, packageBytes = bytes });
            created.EnsureSuccessStatusCode();
            ids[index] = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
            using var manifest = new ByteArrayContent([1, 2, 3, 4]);
            manifest.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var uploaded = await app.Client.PutAsync($"/api/v1/uploads/{ids[index]}/manifest", manifest);
            uploaded.EnsureSuccessStatusCode();
        }

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        long before = process.PrivateMemorySize64;
        long peak = before;
        using var sampling = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!sampling.IsCancellationRequested)
            {
                process.Refresh(); InterlockedMax(ref peak, process.PrivateMemorySize64);
                await Task.Delay(20, sampling.Token).ConfigureAwait(false);
            }
        });
        try
        {
            var writes = ids.Select(id => SendLargeUploadAsync(app.Client, id, bytes)).ToArray();
            await Task.WhenAll(writes);
        }
        finally
        {
            sampling.Cancel();
            try { await sampler; } catch (OperationCanceledException) { }
        }
        return new UploadMemoryMetric(bytes, peak - before, (peak - before) / 1048576d,
            "two concurrent streamed 256MiB package uploads; process private bytes delta");
    }

    private static async Task SendLargeUploadAsync(HttpClient client, string id, long bytes)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/uploads/{id}/package")
        { Content = new RepeatingContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
    }

    private static void InterlockedMax(ref long location, long value)
    {
        long current;
        do { current = Interlocked.Read(ref location); if (value <= current) return; }
        while (Interlocked.CompareExchange(ref location, value, current) != current);
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? current = new(AppContext.BaseDirectory); current != null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "PortKill.Server.slnx"))) return current.FullName;
        throw new DirectoryNotFoundException("Repository root could not be located from test output.");
    }

    private sealed record LatencyMetric(string Description, int Count, double P50Milliseconds, double P95Milliseconds, double MaxMilliseconds)
    {
        public static LatencyMetric Create(IReadOnlyCollection<double> values, string description)
        {
            var sorted = values.OrderBy(value => value).ToArray();
            return new(description, sorted.Length, Percentile(sorted, .50), Percentile(sorted, .95), sorted[^1]);
        }
        private static double Percentile(double[] sorted, double percentile) => sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1)];
    }

    private sealed record UploadMemoryMetric(long BytesPerStream, long ExtraPrivateBytes, double ExtraPrivateMiB, string Description);

    private sealed class RepeatingContent(long size) : HttpContent
    {
        private static readonly byte[] Chunk = new byte[64 * 1024];
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            long remaining = size;
            while (remaining > 0)
            {
                int count = (int)Math.Min(Chunk.Length, remaining);
                await stream.WriteAsync(Chunk.AsMemory(0, count));
                remaining -= count;
            }
        }
        protected override bool TryComputeLength(out long length) { length = size; return true; }
    }
}
