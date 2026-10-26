using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace PortKiller.Updates;

/// <summary>单飞检测/下载；不接触正在运行的二进制。UI 和轮询共享同一个入口。</summary>
public sealed class UpdateService
{
    private static readonly HttpClient SharedClient = CreateClient(useSystemProxy: true);
    private static readonly HttpClient DirectClient = CreateClient(useSystemProxy: false);

    private static HttpClient CreateClient(bool useSystemProxy) => new(new SocketsHttpHandler
    {
        UseProxy = useSystemProxy,
        AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient _client;
    private readonly UpdateOptions _options;
    private readonly string _cache;
    private readonly Version _current;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeProvider _clock;
    private readonly string _trustPath;
    private readonly UpdateOperationLog _operations;
    private string? _etag;
    private DateTimeOffset? _verifiedManifestExpiresUtc;
    private DateTimeOffset _serverNextCheck;
    private DateTimeOffset _nextAutomaticCheck;
    private PreparedUpdate? _prepared;
    private UpdateCoordinator? _coordinator;
    private byte[]? _availableEnvelope;
    private readonly object _downloadControl = new();
    private CancellationTokenSource? _downloadCancellation;
    private volatile bool _pauseRequested;
    private UpdateSnapshot _snapshot = new(UpdateStage.Idle, "检查并获取最新版本");
    public UpdateSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public UpdateManifest? Available { get; private set; }
    public event Action<UpdateSnapshot>? Changed;
    public PreparedUpdate? Prepared => Volatile.Read(ref _prepared);
    public UpdateManifest? Ready => Prepared?.Manifest;
    public string? ReadyPackage => Prepared?.PackagePath;
    public static string CurrentVersion
    {
        get
        {
            Version version = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 1);
            if (version.Build <= 0 && version.Revision <= 0) return version.ToString(2);
            if (version.Revision <= 0) return version.ToString(3);
            return version.ToString(4);
        }
    }
    public string VersionLabel => "v" + CurrentVersion;
    public string VersionLabelAot => VersionLabel + " · Native AOT";
    public event Action<string, bool>? Status;
    public UpdateService(UpdateOptions options, string cache, HttpClient? client = null, string? current = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, TimeProvider? clock = null, string? trustPath = null)
    {
        _options = options; _cache = cache;
        _client = client ?? (options.UseSystemProxy ? SharedClient : DirectClient);
        _current = UpdateManifest.ParseVersion(current ?? CurrentVersion); _delay = delay ?? Task.Delay;
        _clock = clock ?? TimeProvider.System;
        _trustPath = trustPath ?? UpdateTrustStore.DefaultPath;
        _operations = new UpdateOperationLog(cache);
    }

    public Task RunAsync(CancellationToken token, Func<bool>? automaticCheckEnabled = null)
        => Task.Run(async () =>
    {
        if (!_options.Enabled) { Publish(new(UpdateStage.Disabled, "自动更新未配置")); return; }
        try
        {
            _options.Validate();
            _coordinator = new UpdateCoordinator(_options, _client,
                automaticCheckEnabled ?? (() => true),
                () => Snapshot.Stage != UpdateStage.Paused && _gate.CurrentCount != 0,
                ct => CheckCoreAsync(ct, false, true), _clock);
            await _coordinator.RunAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Fail(ex, UpdateFailure.Configuration); }
        finally { _coordinator = null; }
    }, token);

    public void NotifyEnvironmentChanged() => _coordinator?.RequestCheck();

    public Task CheckAsync(CancellationToken token, bool download = true)
        => Task.Run(() => CheckCoreAsync(token, download, false));

    private async Task<bool> CheckCoreAsync(CancellationToken token, bool download, bool automatic)
    {
        if (!_options.Enabled) { Publish(new(UpdateStage.Disabled, "自动更新未配置，请先配置发布源")); return true; }
        if (!_gate.Wait(0)) return false;
        try
        {
            token.ThrowIfCancellationRequested();
            var now = _clock.GetUtcNow();
            if (now < _serverNextCheck || automatic && now < _nextAutomaticCheck) return false;
            _nextAutomaticCheck = now.AddSeconds(2);
            var uri = _options.Validate();
            if (!automatic) Publish(new(UpdateStage.Checking, "正在检查应用更新…", Available));
            // 仅成功校验后保留 ETag；下载失败可在下一轮重试同一版本。
            bool noRelease = false;
            byte[]? envelope = await RetryAsync(async ct =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                bool trustedEtag = !_options.AllowHttp && _etag != null
                    && (_verifiedManifestExpiresUtc == null || _verifiedManifestExpiresUtc > _clock.GetUtcNow());
                if (trustedEtag) request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(_etag!));
                request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                ThrowIfTransient(response);
                if (response.StatusCode == HttpStatusCode.NotFound) { noRelease = true; return null; }
                if (response.StatusCode == HttpStatusCode.NotModified && trustedEtag) return null;
                response.EnsureSuccessStatusCode();
                byte[] bytes = await ReadBoundedAsync(response, 65536, ct);
                // 不提前采纳服务端 ETag，防无效清单导致后续永久 304。
                var verified = UpdateManifest.Verify(bytes, _options, _clock.GetUtcNow());
                UpdateTrustStore.Accept(verified, _trustPath);
                _candidateManifest = verified;
                _candidateEtag = response.Headers.ETag?.ToString();
                _candidateExpiresUtc = verified.Schema >= 2 ? verified.ExpiresUtc : null;
                return bytes;
            }, _options.CheckTimeoutSeconds, token);
            if (noRelease)
            {
                // Keep the empty-feed identity separate, while presenting it as no available update.
                _etag = null; _verifiedManifestExpiresUtc = null;
                _candidateManifest = null; _candidateEtag = null; _candidateExpiresUtc = null;
                Available = null; _availableEnvelope = null; Volatile.Write(ref _prepared, null);
                Publish(new(UpdateStage.NoRelease, "当前版本为最新"));
                return true;
            }
            if (envelope == null)
            {
                if (Prepared is { } prepared && Available != null
                    && prepared.Identity == ReleaseIdentity.From(Available))
                    Publish(new(UpdateStage.Ready, $"版本 {prepared.Manifest.Version} 已就绪", prepared.Manifest, prepared.Manifest.Size));
                else if (Available != null)
                {
                    Publish(new(UpdateStage.Available, $"发现新版本 {Available.Version}", Available));
                    if (download && _availableEnvelope != null) await PreparePackageAsync(_availableEnvelope, token);
                }
                else Publish(new(UpdateStage.Latest, "当前已是最新版本"));
                return true;
            }
            var manifest = _candidateManifest ?? throw new InvalidDataException("缺少已验证清单。");
            if (UpdateManifest.ParseVersion(manifest.Version) <= _current)
            {
                _etag = _candidateEtag; _verifiedManifestExpiresUtc = _candidateExpiresUtc;
                Available = null; _availableEnvelope = null; Volatile.Write(ref _prepared, null);
                Publish(new(UpdateStage.Latest, "当前已是最新版本")); return true;
            }
            if (Prepared is { } old && old.Identity != ReleaseIdentity.From(manifest))
                Volatile.Write(ref _prepared, null);
            Available = manifest; _availableEnvelope = envelope;
            _etag = _candidateEtag; _verifiedManifestExpiresUtc = _candidateExpiresUtc;
            string cachedPackage = PackagePath(manifest);
            if (await IsValidPackageAsync(cachedPackage, manifest, token))
            {
                await MarkReadyAsync(cachedPackage, manifest, envelope, token);
                return true;
            }
            Publish(new(UpdateStage.Available, $"发现新版本 {manifest.Version}", manifest));
            if (download) await PreparePackageAsync(envelope, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Fail(ex);
        }
        finally { _gate.Release(); }
        return _serverNextCheck <= _clock.GetUtcNow();
    }
    private string? _candidateEtag;
    private UpdateManifest? _candidateManifest;
    private DateTimeOffset? _candidateExpiresUtc;

    /// <summary>检测和下载分开；并发入口共用单飞闸门，关闭窗口不会取消此任务。</summary>
    public Task DownloadAvailableAsync(CancellationToken token)
    {
        if (!_gate.Wait(0)) return Task.CompletedTask;
        return Task.Run(async () =>
        {
        try { token.ThrowIfCancellationRequested(); if (_availableEnvelope != null) await PreparePackageAsync(_availableEnvelope, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Fail(ex); }
        finally { _gate.Release(); }
        });
    }

    public InstallationLease? TryAcquireInstallation()
    {
        if (!_gate.Wait(0)) return null;
        var prepared = Prepared;
        if (prepared == null || Snapshot.Stage != UpdateStage.Ready || Available == null
            || prepared.Manifest.Schema >= 2 && prepared.Manifest.ExpiresUtc <= _clock.GetUtcNow()
            || prepared.Identity != ReleaseIdentity.From(Available)) { _gate.Release(); return null; }
        return new InstallationLease(prepared, _gate);
    }

    public sealed class InstallationLease(PreparedUpdate update, SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public PreparedUpdate Update { get; } = update;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    public void PauseDownload()
    {
        lock (_downloadControl)
        {
            if (Snapshot.Stage != UpdateStage.Downloading || _downloadCancellation == null) return;
            _pauseRequested = true;
            _downloadCancellation.Cancel();
        }
    }

    private string PackagePath(UpdateManifest manifest) => Path.Combine(_cache, manifest.Sha256.ToLowerInvariant() + ".zip");

    private async Task PreparePackageAsync(byte[] envelope, CancellationToken token)
    {
        // 用户可能稍后才点下载，此时重新验签和检查有效期。
        var manifest = UpdateManifest.Verify(envelope, _options, _clock.GetUtcNow());
        UpdateTrustStore.Accept(manifest, _trustPath);
        Directory.CreateDirectory(_cache);
        string package = PackagePath(manifest);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        lock (_downloadControl) { _pauseRequested = false; _downloadCancellation = cancellation; }
        try
        {
            if (!await IsValidPackageAsync(package, manifest, cancellation.Token))
                await DownloadAsync(package, manifest, cancellation.Token);
            await MarkReadyAsync(package, manifest, envelope, cancellation.Token);
        }
        catch (OperationCanceledException) when (_pauseRequested && !token.IsCancellationRequested)
        {
            long bytes = File.Exists(package + ".partial") ? new FileInfo(package + ".partial").Length : 0;
            Publish(new(UpdateStage.Paused, "下载已暂停，可继续下载", manifest, bytes));
        }
        finally
        {
            lock (_downloadControl) _downloadCancellation = null;
            if (!_pauseRequested || token.IsCancellationRequested) TryDelete(package + ".partial");
        }
    }

    private async Task MarkReadyAsync(string package, UpdateManifest manifest, byte[] envelope, CancellationToken token)
    {
        await File.WriteAllBytesAsync(package + ".manifest", envelope, token);
        Volatile.Write(ref _prepared, new PreparedUpdate(manifest, package, ReleaseIdentity.From(manifest)));
        TrimCache(package);
        Publish(new(UpdateStage.Ready, $"版本 {manifest.Version} 已下载，确认后重启安装", manifest, manifest.Size));
    }

    private void Publish(UpdateSnapshot snapshot)
    {
        _operations.Record(snapshot);
        Volatile.Write(ref _snapshot, snapshot);
        Changed?.Invoke(snapshot);
        Status?.Invoke(snapshot.Message, snapshot.Stage == UpdateStage.Failed);
    }

    private void Fail(Exception ex, UpdateFailure? failure = null)
    {
        UpdateLog.Write("UpdateCheck", ex);
        var kind = failure ?? (ex switch
        {
            InvalidDataException or CryptographicException or FormatException => UpdateFailure.Integrity,
            OperationCanceledException => UpdateFailure.Network,
            HttpRequestException { StatusCode: not null } => UpdateFailure.Server,
            HttpRequestException or HttpIOException => UpdateFailure.Network,
            IOException or UnauthorizedAccessException => UpdateFailure.Storage,
            _ => UpdateFailure.Unknown
        });
        Publish(new(UpdateStage.Failed, "更新未完成，当前版本可继续使用", Available,
            Failure: kind, ErrorDetail: $"{ex.GetType().Name}: {ex.Message}"));
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task DownloadAsync(string path, UpdateManifest manifest, CancellationToken token)
    {
        int attempt = 0;
        await RetryAsync(async ct =>
        {
            attempt++;
            string partial = path + ".partial";
            long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            if (offset > manifest.Size) { TryDelete(partial); offset = 0; }
            var timer = Stopwatch.StartNew();
            long received = 0; double lastReport = -1;
            Publish(new(UpdateStage.Downloading, attempt > 1 ? $"网络重试 {attempt}/3…" : "正在下载更新…", manifest, offset, Attempt: attempt));
            if (offset < manifest.Size)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, manifest.PackageUrl);
                // 仅向同一 SHA-256 包续传；最终仍校验整包。服务器不支持 Range 则从头下载。
                if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                ThrowIfTransient(response); response.EnsureSuccessStatusCode();
                if (response.StatusCode == HttpStatusCode.PartialContent)
                {
                    var range = response.Content.Headers.ContentRange;
                    if (range?.Unit != "bytes" || range.From != offset || range.Length != manifest.Size || range.To != manifest.Size - 1)
                        throw new InvalidDataException("更新包续传范围不正确。");
                }
                else offset = 0;
                if (response.Content.Headers.ContentLength is long length && length != manifest.Size - offset)
                    throw new InvalidDataException("更新包长度与清单不符。");
                await using (var input = await response.Content.ReadAsStreamAsync(ct))
                await using (var output = new FileStream(partial, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                {
                    byte[] buffer = new byte[65536]; long total = offset; int read;
                    while ((read = await input.ReadAsync(buffer, ct)) != 0)
                    {
                        total += read;
                        if (total > manifest.Size) throw new InvalidDataException("更新包超过声明大小。");
                        await output.WriteAsync(buffer.AsMemory(0, read), ct);
                        received += read;
                        if (timer.Elapsed.TotalSeconds - lastReport >= .2)
                        {
                            lastReport = timer.Elapsed.TotalSeconds;
                            Publish(new(UpdateStage.Downloading, "正在下载更新…", manifest, total,
                                received / Math.Max(.001, timer.Elapsed.TotalSeconds), attempt));
                        }
                    }
                    await output.FlushAsync(ct);
                }
            }
            ct.ThrowIfCancellationRequested();
            Publish(new(UpdateStage.Verifying, "正在校验更新包…", manifest, manifest.Size));
            if (!await IsValidPackageAsync(partial, manifest, ct)) throw new InvalidDataException("更新包 SHA-256 校验失败。");
            File.Move(partial, path, true);
            return true;
        }, _options.DownloadTimeoutSeconds, token);
    }

    internal static async Task<bool> IsValidPackageAsync(string path, UpdateManifest manifest, CancellationToken token)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != manifest.Size) return false;
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<T> RetryAsync<T>(Func<CancellationToken, Task<T>> action, int seconds, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
            try { return await action(deadline.Token); }
            catch (Exception ex) when (attempt < 2 && !token.IsCancellationRequested
                && (ex is OperationCanceledException || ex is TransientUpdateException
                    || ex is HttpIOException
                    || ex is HttpRequestException { StatusCode: null }))
            {
                var backoff = TimeSpan.FromSeconds((1 << attempt) + Random.Shared.NextDouble() * .5);
                if (ex is TransientUpdateException { RetryAfter: { } retryAfter } && retryAfter > backoff) backoff = retryAfter;
                await _delay(backoff, token);
            }
        }
    }
    private void ThrowIfTransient(HttpResponseMessage response)
    {
        if ((int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
        {
            TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
            if (response.Headers.RetryAfter?.Date is { } date) retryAfter = date - _clock.GetUtcNow();
            if (retryAfter > TimeSpan.Zero)
            {
                var until = _clock.GetUtcNow().Add(retryAfter.Value);
                if (until > _serverNextCheck) _serverNextCheck = until;
            }
            if (retryAfter > TimeSpan.FromSeconds(60))
            {
                _serverNextCheck = _clock.GetUtcNow().Add(retryAfter.Value);
                throw new HttpRequestException("更新服务器要求稍后重试。", null, response.StatusCode);
            }
            throw new TransientUpdateException(retryAfter);
        }
    }
    private sealed class TransientUpdateException(TimeSpan? retryAfter) : IOException
    { internal TimeSpan? RetryAfter { get; } = retryAfter; }

    private void TrimCache(string current)
    {
        try
        {
            // 保留当前包和一个最近的包；崩溃残留的半成品不长期累积。
            var old = new DirectoryInfo(_cache).EnumerateFiles("*.zip")
                .Where(x => !x.FullName.Equals(current, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.LastWriteTimeUtc).Skip(1);
            foreach (var file in old) { file.Delete(); File.Delete(file.FullName + ".manifest"); }
            foreach (var file in new DirectoryInfo(_cache).EnumerateFiles("*.partial")) file.Delete();
        }
        catch (IOException) { } // 清理不影响已校验的更新包
        catch (UnauthorizedAccessException) { }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int limit, CancellationToken token)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream(); byte[] buffer = new byte[4096]; int read;
        while ((read = await stream.ReadAsync(buffer, token)) != 0)
        {
            if (output.Length + read > limit) throw new InvalidDataException("更新清单过大。");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
