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

/// <summary>Explicit startup/outcome reports only; no timer or background polling.</summary>
public sealed class ClientReportService : IDisposable
{
    private readonly Uri _baseUri;
    private readonly HttpClient _http;
    private readonly ClientIdentityStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Action<string>? _log;
    private bool _reregistered;

    public ClientReportService(Uri baseUri, bool useSystemProxy = true, ClientIdentityStore? store = null,
        HttpMessageHandler? handler = null, Action<string>? log = null)
    {
        _baseUri = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/");
        _store = store ?? new ClientIdentityStore();
        _http = new HttpClient(handler ?? new HttpClientHandler
        { UseProxy = useSystemProxy, AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
        _log = log;
    }

    // Catch every failure at this boundary; reporting is never part of updater authorization.
    public async Task ReportAsync(string version, string result, CancellationToken token = default)
    {
        bool entered = false;
        try
        {
            if (result is not ("started" or "installed" or "rolled_back" or "failed")) return;
            await _gate.WaitAsync(token).ConfigureAwait(false); entered = true;
            using var lease = _store.AcquireLease();
            var identity = _store.LoadOrCreate();
            string machine = Environment.MachineName;
            if (machine.Length > 128) machine = machine[..128];
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (!identity.Registered)
                {
                    using var registration = await SendAsync("register", identity, version, machine, result, token).ConfigureAwait(false);
                    if (registration.StatusCode == HttpStatusCode.Conflict && TryReset(ref identity)) continue;
                    if (!registration.IsSuccessStatusCode) { Log("client_registration_rejected"); return; }
                    // The response is deliberately not logged or read into diagnostic exceptions.
                    identity.Registered = true;
                    _store.Save(identity);
                }
                using var response = await SendAsync("report", identity, version, machine, result, token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized && TryReset(ref identity)) continue;
                if (!response.IsSuccessStatusCode) Log("client_report_rejected");
                return;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { Log("client_report_unavailable"); }
        finally { if (entered) _gate.Release(); }
    }

    private bool TryReset(ref ClientIdentity identity)
    {
        if (_reregistered) return false;
        _reregistered = true;
        // A revoked identity remains revoked in the server database. Never overwrite its token.
        identity = _store.Create();
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
    public void Dispose() => _http.Dispose();
}

internal sealed record ClientRegistrationPayload(string InstallationId, string Version, string MachineName, int ProtocolVersion, string ClientToken);
internal sealed record ClientReportPayload(string Version, string MachineName, string Result);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ClientIdentity))]
[JsonSerializable(typeof(ClientIdentityCollection))]
[JsonSerializable(typeof(ClientIdentityEnvelope))]
[JsonSerializable(typeof(ClientRegistrationPayload))]
[JsonSerializable(typeof(ClientReportPayload))]
internal partial class ClientReportJsonContext : JsonSerializerContext { }
