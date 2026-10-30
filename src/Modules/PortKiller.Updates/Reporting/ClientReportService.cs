using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PortKiller.Updates;

/// <summary>Startup/outcome reports with one protected pending state; retries exist only while delivery is pending.</summary>
public sealed class ClientReportService : IDisposable
{
    private readonly Uri _baseUri;
    private readonly HttpClient _http;
    private readonly ClientIdentityStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Action<string>? _log;
    private bool _reregistered;
    private readonly TimeProvider _clock;
    private readonly ITimer _retryTimer;
    private readonly CancellationTokenSource _stop = new();
    private int _disposed;
    private const int MaxScheduledAttempts = 20;
    private const long PendingLifetimeMs = 7L * 24 * 60 * 60 * 1000;

    public ClientReportService(Uri baseUri, bool useSystemProxy = true, ClientIdentityStore? store = null,
        HttpMessageHandler? handler = null, Action<string>? log = null, TimeProvider? clock = null)
    {
        _baseUri = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/");
        _store = store ?? new ClientIdentityStore();
        _http = new HttpClient(handler ?? new HttpClientHandler
        { UseProxy = useSystemProxy, AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
        _log = log;
        _clock = clock ?? TimeProvider.System;
        _retryTimer = _clock.CreateTimer(_ => _ = RetryPendingAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    // Queue before consuming an updater outcome. No network work is performed here.
    public async Task<bool> QueueAsync(string version, string result, CancellationToken token = default)
    {
        if (result is not ("started" or "installed" or "rolled_back" or "failed") || Volatile.Read(ref _disposed) != 0) return false;
        bool entered = false;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
            await _gate.WaitAsync(linked.Token).ConfigureAwait(false); entered = true;
            using var lease = _store.AcquireLease();
            var identity = _store.LoadOrCreate();
            var old = identity.PendingReport;
            long now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
            // A restart must not replace an undelivered outcome for this version with "started".
            bool keepOutcome = old is { Result: not "started" } && old.Version == version
                && old.Endpoint == _baseUri.AbsoluteUri && now - old.QueuedUtcMs < PendingLifetimeMs && result == "started";
            identity.PendingReport = new PendingClientReport {
                Version = version, Result = keepOutcome ? old!.Result : result, Endpoint = _baseUri.AbsoluteUri,
                QueuedUtcMs = keepOutcome ? old!.QueuedUtcMs : now,
                RateLimited = old?.RateLimited == true && old.Endpoint == _baseUri.AbsoluteUri && old.NextAttemptUtcMs > now,
                NextAttemptUtcMs = old?.RateLimited == true && old.Endpoint == _baseUri.AbsoluteUri ? old.NextAttemptUtcMs : 0
            };
            _store.Save(identity);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception) { Log("client_report_queue_unavailable"); return false; }
        finally { if (entered) _gate.Release(); }
    }

    // Reporting never participates in updater authorization or blocks the UI thread.
    public async Task ReportAsync(string version, string result, CancellationToken token = default)
    {
        if (await QueueAsync(version, result, token).ConfigureAwait(false))
            await RetryPendingAsync(token).ConfigureAwait(false);
    }

    public async Task RetryPendingAsync(CancellationToken token = default, bool networkAvailable = false)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        bool entered = false;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
            await _gate.WaitAsync(linked.Token).ConfigureAwait(false); entered = true;
            using var lease = _store.AcquireLease();
            var identity = _store.LoadOrCreate();
            var pending = identity.PendingReport;
            if (pending == null) return;
            long now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
            if (pending.Endpoint != _baseUri.AbsoluteUri || pending.QueuedUtcMs > now + 60_000
                || now - pending.QueuedUtcMs >= PendingLifetimeMs)
            {
                identity.PendingReport = null; _store.Save(identity);
                _retryTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                Log("client_report_pending_expired"); return;
            }
            if (pending.Attempts >= MaxScheduledAttempts && !networkAvailable) return;
            if (pending.NextAttemptUtcMs > now && (!networkAvailable || pending.RateLimited))
            { Schedule(pending.NextAttemptUtcMs - now); return; }
            string machine = Environment.MachineName;
            if (machine.Length > 128) machine = machine[..128];
            try
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    if (!identity.Registered)
                    {
                        using var registration = await SendAsync("register", identity, pending.Version, machine, pending.Result, linked.Token).ConfigureAwait(false);
                        if (registration.StatusCode == HttpStatusCode.Conflict && TryReset(ref identity, pending)) continue;
                        if (!registration.IsSuccessStatusCode) { Defer(identity, pending, registration); return; }
                        identity.Registered = true; _store.Save(identity);
                    }
                    using var response = await SendAsync("report", identity, pending.Version, machine, pending.Result, linked.Token).ConfigureAwait(false);
                    if (response.StatusCode == HttpStatusCode.Unauthorized && TryReset(ref identity, pending)) continue;
                    if (!response.IsSuccessStatusCode) { Defer(identity, pending, response); return; }
                    identity.PendingReport = null; _store.Save(identity);
                    _retryTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                    return;
                }
                Defer(identity, pending, null);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            catch (Exception) { Log("client_report_unavailable"); Defer(identity, pending, null); }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { Log("client_report_unavailable"); }
        finally { if (entered) _gate.Release(); }
    }

    private void Defer(ClientIdentity identity, PendingClientReport pending, HttpResponseMessage? response)
    {
        pending.Attempts++;
        long now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        double seconds = Math.Min(300, 5 * Math.Pow(2, Math.Min(6, pending.Attempts - 1)));
        pending.RateLimited = response?.StatusCode == HttpStatusCode.TooManyRequests;
        if (pending.RateLimited)
        {
            var retry = response!.Headers.RetryAfter;
            seconds = Math.Max(seconds, retry?.Delta?.TotalSeconds
                ?? (retry?.Date is { } date ? (date - _clock.GetUtcNow()).TotalSeconds : 60));
        }
        else if (response is { StatusCode: HttpStatusCode.NotFound }) seconds = Math.Max(60, seconds);
        pending.NextAttemptUtcMs = now + (long)Math.Min(PendingLifetimeMs, seconds * 1000);
        identity.PendingReport = pending; _store.Save(identity);
        if (response != null) Log("client_report_http_" + (int)response.StatusCode);
        if (pending.Attempts < MaxScheduledAttempts) Schedule(pending.NextAttemptUtcMs - now);
        else Log("client_report_retry_budget_exhausted");
    }

    private void Schedule(long milliseconds)
    {
        if (Volatile.Read(ref _disposed) == 0)
            _retryTimer.Change(TimeSpan.FromMilliseconds(Math.Clamp(milliseconds, 1, 86_400_000)), Timeout.InfiniteTimeSpan);
    }

    private bool TryReset(ref ClientIdentity identity, PendingClientReport pending)
    {
        if (_reregistered) return false;
        _reregistered = true;
        // A revoked identity remains revoked in the server database. Never overwrite its token.
        identity = _store.Create();
        identity.PendingReport = pending; _store.Save(identity);
        return true;
    }

    private async Task<HttpResponseMessage> SendAsync(string endpoint, ClientIdentity identity, string version,
        string machine, string result, CancellationToken token)
    {
        byte[] payload = endpoint == "register"
            ? JsonSerializer.SerializeToUtf8Bytes(new ClientRegistrationPayload(identity.InstallationId, version, machine, 1, identity.ClientToken),
                ClientReportJsonContext.Default.ClientRegistrationPayload)
            : JsonSerializer.SerializeToUtf8Bytes(new ClientReportPayload(version, machine, result), ClientReportJsonContext.Default.ClientReportPayload);
        for (int retry = 0; ; retry++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, endpoint));
                request.Content = new ByteArrayContent(payload);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                if (endpoint == "report") request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity.ClientToken);
                var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (retry >= 2 || ((int)response.StatusCode < 500 && response.StatusCode != HttpStatusCode.RequestTimeout)) return response;
                response.Dispose();
            }
            catch (Exception ex) when (retry < 2 && !token.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException) { }
            await Task.Delay(TimeSpan.FromMilliseconds(200 * (retry + 1)), token).ConfigureAwait(false);
        }
    }

    private void Log(string code) { try { _log?.Invoke(code); } catch { /* Diagnostics must remain optional. */ } }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel(); _retryTimer.Dispose(); _http.Dispose();
    }
}

internal sealed record ClientRegistrationPayload(string InstallationId, string Version, string MachineName, int ProtocolVersion, string ClientToken);
internal sealed record ClientReportPayload(string Version, string MachineName, string Result);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ClientIdentity))]
[JsonSerializable(typeof(ClientIdentityCollection))]
[JsonSerializable(typeof(ClientIdentityEnvelope))]
[JsonSerializable(typeof(PendingClientReport))]
[JsonSerializable(typeof(ClientRegistrationPayload))]
[JsonSerializable(typeof(ClientReportPayload))]
internal partial class ClientReportJsonContext : JsonSerializerContext { }
