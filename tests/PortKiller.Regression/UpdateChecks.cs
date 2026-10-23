using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PortKiller.Updates;

namespace PortKiller.Regression;

internal static class UpdateChecks
{
    internal static async Task DiscoveryAndProgress()
    {
        using var fixture = new Fixture(); int downloads = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".zip")) { downloads++; return Task.FromResult(Bytes(fixture.Package)); }
            return Task.FromResult(Bytes(fixture.Envelope(releaseNotes: ["支持暂停下载", "保留旧版本"])));
        }));
        var service = fixture.Service(http);
        var states = new List<UpdateSnapshot>(); service.Changed += states.Add;
        await service.CheckAsync(CancellationToken.None, download: false);
        Program.Assert(service.Snapshot.Stage == UpdateStage.Available && downloads == 0 && service.Ready == null,
            "发现版本时未经用户选择即开始下载。");
        Program.Assert(service.Available?.ReleaseNotes.SequenceEqual(new[] { "支持暂停下载", "保留旧版本" }) == true,
            "已签名更新说明没有传到界面模型。");
        await service.DownloadAvailableAsync(CancellationToken.None);
        Program.Assert(downloads == 1 && service.Snapshot.Stage == UpdateStage.Ready && service.Snapshot.Percent == 100,
            "手动启动下载后未就绪。");
        Program.Assert(states.Any(x => x.Stage == UpdateStage.Downloading && x.DownloadedBytes > 0 && x.BytesPerSecond > 0)
            && states.Any(x => x.Stage == UpdateStage.Verifying), "缺少真实下载速度或校验阶段。");
        Program.Assert(states.Where(x => x.Stage == UpdateStage.Downloading).All(x => x.Percent is >= 0 and <= 100),
            "下载进度越界。");
        bool rejected = false;
        try { UpdateManifest.Verify(fixture.Envelope(releaseNotes: [new string('x', 301)]), fixture.Options); }
        catch (InvalidDataException) { rejected = true; }
        Program.Assert(rejected, "超长更新说明未拦截。");
    }

    internal static async Task PauseResumeAndRangeFallback()
    {
        foreach (int rangeMode in new[] { 0, 1, 2 })
        {
            bool supportsRange = rangeMode != 1;
            using var fixture = new Fixture(); int downloads = 0; long? resumedFrom = null;
            var resumeResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            HttpResponseMessage? pendingResponse = null;
            using var http = new HttpClient(new Handler((request, _) =>
            {
                if (!request.RequestUri!.AbsolutePath.EndsWith(".zip")) return Task.FromResult(Bytes(fixture.Envelope()));
                downloads++;
                if (downloads == 1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StreamContent(new PauseStream(fixture.Package)) });
                resumedFrom = request.Headers.Range?.Ranges.Single().From;
                int offset = supportsRange ? (int)(resumedFrom ?? 0) : 0;
                var response = Bytes(fixture.Package[offset..]);
                if (supportsRange)
                {
                    response.StatusCode = HttpStatusCode.PartialContent;
                    response.Content.Headers.ContentRange = new(rangeMode == 2 ? offset + 1 : offset, fixture.Package.Length - 1, fixture.Package.Length);
                }
                pendingResponse = response;
                return resumeResponse.Task;
            }));
            var service = fixture.Service(http);
            service.Changed += PauseAfterProgress;
            void PauseAfterProgress(UpdateSnapshot state)
            { if (state.Stage == UpdateStage.Downloading && state.DownloadedBytes > 0) service.PauseDownload(); }
            await service.CheckAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Program.Assert(service.Snapshot.Stage == UpdateStage.Paused && service.Ready == null
                && Directory.EnumerateFiles(fixture.Directory, "*.partial").Single() != null, "暂停丢失了已下载内容。");
            service.Changed -= PauseAfterProgress;
            var concurrent = Enumerable.Range(0, 20).Select(_ => service.DownloadAvailableAsync(CancellationToken.None)).ToArray();
            resumeResponse.SetResult(pendingResponse!);
            await Task.WhenAll(concurrent);
            Program.Assert(resumedFrom == 8 && downloads == 2, "续传偏移或并发去重不正确。");
            Program.Assert(rangeMode == 2 ? service.Ready == null && service.Snapshot.Failure == UpdateFailure.Integrity
                : service.Ready != null, "无效续传范围被接受，或 Range 回退失败。");
            Program.Assert(!Directory.EnumerateFiles(fixture.Directory, "*.partial").Any(), "就绪后半成品未清理。");
        }
    }

    private sealed class PauseStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position > 0) await Task.Delay(Timeout.Infinite, cancellationToken);
            return await base.ReadAsync(buffer[..Math.Min(buffer.Length, 8)], cancellationToken);
        }
    }
    internal static async Task NormalAndRepeated()
    {
        using var fixture = new Fixture();
        using var started = new SemaphoreSlim(0);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int manifests = 0, downloads = 0;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".zip"))
            { downloads++; return Bytes(fixture.Package); }
            manifests++;
            if (manifests == 1)
            {
                started.Release(); await release.Task.WaitAsync(token);
                var response = Bytes(fixture.Envelope()); response.Headers.ETag = new("\"release-2\""); return response;
            }
            Program.Assert(request.Headers.IfNoneMatch.Single().Tag == "\"release-2\"", "未复用 ETag。");
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        }));
        var clock = new TestClock(); var service = fixture.Service(http, clock);
        var first = service.CheckAsync(CancellationToken.None); await started.WaitAsync();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => service.CheckAsync(CancellationToken.None)));
        release.SetResult(); await first;
        Program.Assert(service.Ready?.Version == "1.0.2" && File.Exists(service.ReadyPackage), "正常更新未就绪。");
        clock.Advance(); await service.CheckAsync(CancellationToken.None);
        Program.Assert(manifests == 2 && downloads == 1, "重复触发或 304 导致重复下载。");
        var second = fixture.Service(http, clock);
        // 进程重启后的缓存仍须通过长度和摘要验证。
        using var cachedHttp = new HttpClient(new Handler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith(".zip") ? throw new Exception("缓存不应重复下载") : Bytes(fixture.Envelope()))));
        second = fixture.Service(cachedHttp, clock); await second.CheckAsync(CancellationToken.None);
        Program.Assert(second.Ready != null, "有效缓存没有复用。");
    }

    internal static async Task NoUpdate()
    {
        using var fixture = new Fixture(); int requests = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            requests++; return Task.FromResult(Bytes(fixture.Envelope("1.0.1")));
        }));
        var service = fixture.Service(http); await service.CheckAsync(CancellationToken.None);
        Program.Assert(service.Ready == null && requests == 1, "相同版本不应下载。");
        Program.Assert(UpdateManifest.ParseVersion("1.0.1") == UpdateManifest.ParseVersion("1.0.1.0"), "版本归一化错误。");
    }

    internal static async Task NetworkAndRetry()
    {
        using var fixture = new Fixture(); int downloads = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith(".zip")) return Task.FromResult(Bytes(fixture.Envelope()));
            downloads++;
            if (downloads == 1) throw new HttpRequestException("模拟断网");
            if (downloads == 2) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StreamContent(new BrokenStream(fixture.Package)) });
            return Task.FromResult(Bytes(fixture.Package));
        }));
        var service = fixture.Service(http); await service.CheckAsync(CancellationToken.None);
        Program.Assert(service.Ready != null && downloads == 3, "瞬时中断没有有限重试成功。");
        int failures = 0;
        using var offline = new HttpClient(new Handler((_, _) => { failures++; throw new HttpRequestException("断网"); }));
        service = fixture.Service(offline); await service.CheckAsync(CancellationToken.None);
        Program.Assert(failures == 3 && service.Ready == null, "断网重试未在三次内结束。");
        Program.Assert(!Directory.EnumerateFiles(fixture.Directory, "*.partial").Any(), "失败留下下载半成品。");
    }

    internal static async Task InvalidAndCancelled()
    {
        using var fixture = new Fixture();
        byte[] corrupt = (byte[])fixture.Package.Clone(); corrupt[10] ^= 1;
        using var badHttp = new HttpClient(new Handler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith(".zip") ? Bytes(corrupt) : Bytes(fixture.Envelope()))));
        var service = fixture.Service(badHttp); await service.CheckAsync(CancellationToken.None);
        Program.Assert(service.Ready == null, "损坏下载被接受。");
        byte[] forged = fixture.Envelope(); forged[^8] ^= 1;
        bool rejected = false;
        try { UpdateManifest.Verify(forged, fixture.Options); } catch { rejected = true; }
        Program.Assert(rejected, "伪造签名未拒绝。");
        int requests = 0;
        using var hanging = new HttpClient(new Handler(async (_, ct) =>
        {
            requests++; await Task.Delay(Timeout.Infinite, ct); return Bytes([]);
        }));
        service = fixture.Service(hanging);
        using var cancel = new CancellationTokenSource(100);
        await service.CheckAsync(cancel.Token);
        Program.Assert(requests == 1 && service.Ready == null, "退出取消后仍在重试。");
        requests = 0;
        await service.CheckAsync(new CancellationToken(true));
        await service.DownloadAvailableAsync(new CancellationToken(true));
        Program.Assert(requests == 0, "调用前已取消仍发起请求。");
    }

    internal static async Task TimeoutAndServerFailure()
    {
        using var fixture = new Fixture(); fixture.Options.CheckTimeoutSeconds = 1;
        int requests = 0;
        using var hanging = new HttpClient(new Handler(async (_, ct) =>
        { requests++; await Task.Delay(Timeout.Infinite, ct); return Bytes([]); }));
        await fixture.Service(hanging).CheckAsync(CancellationToken.None);
        Program.Assert(requests == 3, "超时重试上限错误。");
        requests = 0;
        using var server = new HttpClient(new Handler((_, _) =>
        { requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)); }));
        await fixture.Service(server).CheckAsync(CancellationToken.None);
        Program.Assert(requests == 3, "503 重试上限错误。");
        requests = 0;
        using var missing = new HttpClient(new Handler((_, _) =>
        { requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)); }));
        await fixture.Service(missing).CheckAsync(CancellationToken.None);
        Program.Assert(requests == 1, "404 不应立即重试。");
        requests = 0;
        using var limited = new HttpClient(new Handler((_, _) =>
        {
            requests++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
            return Task.FromResult(response);
        }));
        var limitedService = fixture.Service(limited);
        await limitedService.CheckAsync(CancellationToken.None);
        await limitedService.CheckAsync(CancellationToken.None);
        Program.Assert(requests == 1, "未尊重服务端长时间 Retry-After。");
    }

    internal static async Task HttpAndFreshness()
    {
        using var fixture = new Fixture();
        fixture.Options.ManifestUrl = "http://example.com:8080/manifest.json";
        bool defaultDenied = false;
        try { fixture.Options.Validate(); } catch (InvalidDataException) { defaultDenied = true; }
        Program.Assert(defaultDenied, "HTTP 未显式开启时应拒绝更新源。");
        fixture.Options.AllowHttp = true;
        fixture.Options.Validate();

        int manifests = 0, downloads = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".zip"))
            { downloads++; return Task.FromResult(Bytes(fixture.Package)); }
            manifests++;
            Program.Assert(!request.Headers.IfNoneMatch.Any(), "HTTP 清单不能依赖未认证的 ETag/304。" );
            return Task.FromResult(Bytes(fixture.Envelope(
                sequence: manifests == 1 ? 100 : 99,
                packageUrl: "http://example.com:8080/release.zip")));
        }));
        var clock = new TestClock();
        var service = fixture.Service(http, clock);
        await service.CheckAsync(CancellationToken.None);
        Program.Assert(service.Ready?.Version == "1.0.2" && downloads == 1, "签名 HTTP 更新没有就绪。");
        clock.Advance();
        await service.CheckAsync(CancellationToken.None);
        Program.Assert(manifests == 2 && downloads == 1 && service.Ready?.Version == "1.0.2",
            "HTTP 清单回放后影响了已校验的可用更新。");

        bool rejected = false;
        try { UpdateManifest.Verify(fixture.Envelope(packageUrl: "https://example.com:8080/release.zip"), fixture.Options); }
        catch (InvalidDataException) { rejected = true; }
        Program.Assert(rejected, "HTTP 清单接受了跨协议的包地址。");
        rejected = false;
        try { UpdateManifest.Verify(fixture.Envelope(packageUrl: "http://example.com:8081/release.zip"), fixture.Options); }
        catch (InvalidDataException) { rejected = true; }
        Program.Assert(rejected, "HTTP 清单接受了跨端口的包地址。");
        rejected = false;
        try
        {
            UpdateManifest.Verify(fixture.Envelope(
                expiresUtc: DateTimeOffset.UtcNow.AddMinutes(-1),
                packageUrl: "http://example.com:8080/release.zip"), fixture.Options);
        }
        catch (InvalidDataException) { rejected = true; }
        Program.Assert(rejected, "过期的签名清单仍被接受。");
    }

    internal static void TransactionAndRecovery()
    {
        using var fixture = new Fixture();
        string install = Path.Combine(fixture.Directory, "install"), stage = Path.Combine(fixture.Directory, "stage");
        System.IO.Directory.CreateDirectory(install); System.IO.Directory.CreateDirectory(stage);
        foreach (string file in UpdateInstaller.Files)
        { File.WriteAllText(Path.Combine(install, file), "old-" + file); File.WriteAllText(Path.Combine(stage, file), "new-" + file); }
        try { UpdateInstaller.ApplyFiles(install, stage, new UpdateJournal { Version = "1.0.2" }, i => { if (i == 1) throw new IOException("模拟磁盘失败"); }); }
        catch (IOException) { }
        Program.Assert(File.Exists(Path.Combine(install, ".portkiller-update", "transaction.json")), "未持久化恢复日志。");
        UpdateInstaller.Rollback(install); UpdateInstaller.Rollback(install);
        Program.Assert(UpdateInstaller.Files.All(file => File.ReadAllText(Path.Combine(install, file)) == "old-" + file), "部分安装失败未完整恢复。");
        UpdateInstaller.ApplyFiles(install, stage, new UpdateJournal { Version = "1.0.2" });
        Program.Assert(UpdateInstaller.Files.All(file => File.ReadAllText(Path.Combine(install, file)) == "new-" + file), "正常文件切换失败。");
        // 模拟全部替换后新版本崩溃/尚未确认启动。
        UpdateInstaller.Rollback(install);
        Program.Assert(UpdateInstaller.Files.All(file => File.ReadAllText(Path.Combine(install, file)) == "old-" + file), "未确认的安装未恢复。");
        using var lease = new FileStream(Path.Combine(install, ".portkiller-update", "lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Program.Assert(!UpdateInstaller.RecoverBeforeStartup(install, []), "安装中允许普通进程读取混合版本。");
    }

    internal static void ArchiveAndConfiguration()
    {
        using var fixture = new Fixture(); string zip = Path.Combine(fixture.Directory, "bad.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (string file in UpdateInstaller.Files)
            { using var writer = new StreamWriter(archive.CreateEntry(file == "PortKiller.exe" ? "../PortKiller.exe" : file).Open()); writer.Write("bad"); }
        }
        bool rejected = false;
        try { UpdateInstaller.ExtractPackage(zip, Path.Combine(fixture.Directory, "out")); } catch (InvalidDataException) { rejected = true; }
        Program.Assert(rejected && !File.Exists(Path.Combine(fixture.Directory, "PortKiller.exe")), "路径穿越未阻止。");
        Program.Assert(!UpdateOptions.Load(fixture.Directory).Enabled, "未配置时自动联网。");
        fixture.Options.ManifestUrl = "http://example.com/manifest.json";
        rejected = false; try { fixture.Options.Validate(); } catch (InvalidDataException) { rejected = true; }
        Program.Assert(rejected, "HTTP 未显式开启时未拒绝。");
        fixture.Options.AllowHttp = true;
        Program.Assert(fixture.Options.Validate().Scheme == "http", "HTTP 显式开启后仍不可用。");
    }

    private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
    private sealed class BrokenStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position > 0) throw new HttpIOException(HttpRequestError.ResponseEnded, "模拟传输中途断开");
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, 8)], cancellationToken);
        }
    }
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance() => _now = _now.AddSeconds(31);
    }
    private sealed class Fixture : IDisposable
    {
        private readonly RSA _rsa = RSA.Create(2048);
        private long _sequence = 1;
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "PortKiller-UpdateTests-" + Guid.NewGuid().ToString("N"));
        internal byte[] Package { get; }
        internal UpdateOptions Options { get; }
        internal Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            using var bytes = new MemoryStream();
            using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
                foreach (string file in UpdateInstaller.Files)
                { using var writer = new StreamWriter(zip.CreateEntry(file).Open()); writer.Write("binary-" + file); }
            Package = bytes.ToArray();
            Options = new UpdateOptions { Enabled = true, ManifestUrl = "https://example.com/manifest.json", PublicKeyPem = _rsa.ExportSubjectPublicKeyInfoPem() };
        }
        internal byte[] Envelope(string version = "1.0.2", long sequence = 0,
            DateTimeOffset? publishedUtc = null, DateTimeOffset? expiresUtc = null,
            string? packageUrl = null, int schema = 2, string[]? releaseNotes = null)
        {
            if (sequence <= 0) sequence = Interlocked.Increment(ref _sequence);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new UpdateManifest
            {
                Schema = schema, Version = version,
                ReleaseNotes = releaseNotes ?? [],
                PackageUrl = packageUrl ?? "https://example.com/release.zip",
                Size = Package.Length, Sha256 = Convert.ToHexString(SHA256.HashData(Package)),
                Sequence = sequence,
                PublishedUtc = publishedUtc ?? DateTimeOffset.UtcNow.AddMinutes(-1),
                ExpiresUtc = expiresUtc ?? DateTimeOffset.UtcNow.AddDays(30)
            }, UpdateJsonContext.Default.UpdateManifest);
            return JsonSerializer.SerializeToUtf8Bytes(new SignedUpdate
            { Payload = Convert.ToBase64String(payload), Signature = Convert.ToBase64String(_rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) }, UpdateJsonContext.Default.SignedUpdate);
        }
        internal UpdateService Service(HttpClient client, TimeProvider? clock = null)
            => new(Options, Directory, client, "1.0.1", (_, _) => Task.CompletedTask, clock,
                Path.Combine(Directory, "trusted-release.json"));
        public void Dispose() { _rsa.Dispose(); System.IO.Directory.Delete(Directory, true); }
    }
}
