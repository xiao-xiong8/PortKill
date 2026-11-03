using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace PortKiller.Docker;

/// <summary>仅通过本机 Named Pipe 调用 Engine；不执行 Docker CLI 或容器内命令。</summary>
public sealed class DockerEngineClient : IDockerEngineClient
{
    private readonly SemaphoreSlim _initialization = new(1, 1);
    private readonly object _connectionGate = new();
    private EngineConnection? _connection;
    private bool _disposed;

    public async Task<DockerInventory> ReadInventoryAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var callerToken = token; token = deadline.Token;
        try
        {
            using var lease = await AcquireAsync(token).ConfigureAwait(false);
            var connection = lease.Connection;
            var info = await GetAsync(connection, "/info", DockerJsonContext.Default.EngineInfoDto, token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(info.ID)) throw new InvalidDataException("Engine 未返回身份，无法关联本机容器。");
            string engineId = info.ID;
            var observedAt = DateTimeOffset.UtcNow;
            var ids = await GetAsync(connection, "/containers/json?all=false", DockerJsonContext.Default.ContainerListDtoArray, token).ConfigureAwait(false);
            if (ids.Length > 1024) throw new InvalidDataException("运行容器数超过单次查询上限（1024）。");
            var containers = new List<DockerContainer>();
            foreach (var id in ids)
            {
                token.ThrowIfCancellationRequested();
                var dto = await GetOptionalAsync(connection, "/containers/" + SafeId(id.Id) + "/json", token).ConfigureAwait(false);
                if (dto != null) containers.Add(Convert(dto, engineId));
            }
            var after = await GetAsync(connection, "/info", DockerJsonContext.Default.EngineInfoDto, token).ConfigureAwait(false);
            if (after.ID != engineId) throw new InvalidOperationException("Docker Engine 在采集期间发生切换，快照未发布。");
            return new(engineId, observedAt, containers.ToArray(), Endpoint: connection.Pipe);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return DockerInventory.Unavailable("Docker 完整采集超过 10 秒，状态尚未确认。"); }
        catch (Exception error) when (error is IOException or HttpRequestException or JsonException or TimeoutException or UnauthorizedAccessException or InvalidOperationException)
        { return DockerInventory.Unavailable(error.Message); }
    }

    public async Task<DockerContainer?> InspectAsync(ContainerIdentity identity, CancellationToken token)
    {
        using var lease = await AcquireAsync(token).ConfigureAwait(false);
        await CheckEngineAsync(lease.Connection, identity, token).ConfigureAwait(false);
        var dto = await GetOptionalAsync(lease.Connection, "/containers/" + SafeId(identity.Id) + "/json", token).ConfigureAwait(false);
        return dto == null ? null : Convert(dto, identity.EngineId);
    }
    public async Task StopGracefullyAsync(ContainerIdentity identity, CancellationToken token)
    {
        using var lease = await AcquireAsync(token).ConfigureAwait(false);
        var connection = lease.Connection;
        await CheckEngineAsync(connection, identity, token).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, connection.Api + "/containers/" + SafeId(identity.Id) + "/stop?t=-1");
        try
        {
            using var response = await connection.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            await CheckResponseAsync(response, token).ConfigureAwait(false);
        }
        catch { Invalidate(connection); throw; }
    }
    public Task KillAsync(ContainerIdentity identity, CancellationToken token) => PostAsync(identity, "/kill", null, token);
    public Task UpdateRestartPolicyAsync(ContainerIdentity identity, RestartPolicy policy, CancellationToken token)
        => PostAsync(identity, "/update", new UpdateRequestDto { RestartPolicy = new() { Name = policy.Name, MaximumRetryCount = policy.MaximumRetryCount } }, token);

    private async Task PostAsync(ContainerIdentity identity, string action, UpdateRequestDto? update, CancellationToken token)
    {
        using var lease = await AcquireAsync(token).ConfigureAwait(false);
        var connection = lease.Connection;
        await CheckEngineAsync(connection, identity, token).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var request = new HttpRequestMessage(HttpMethod.Post, connection.Api + "/containers/" + SafeId(identity.Id) + action);
        if (update != null) request.Content = JsonContent.Create(update, DockerJsonContext.Default.UpdateRequestDto);
        try
        {
            using var response = await connection.Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            await CheckResponseAsync(response, timeout.Token).ConfigureAwait(false);
        }
        catch { Invalidate(connection); throw; }
    }
    private async Task CheckEngineAsync(EngineConnection connection, ContainerIdentity identity, CancellationToken token)
    {
        var info = await GetAsync(connection, "/info", DockerJsonContext.Default.EngineInfoDto, token).ConfigureAwait(false);
        if (info.ID != identity.EngineId) throw new InvalidOperationException("Docker Engine 身份已变化，请刷新并重新确认。");
    }
    private async Task<ConnectionLease> AcquireAsync(CancellationToken token)
    {
        await _initialization.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var pipes = DiscoverPipes();
            string selection = string.Join("|", pipes);
            lock (_connectionGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_connection is { } current)
                {
                    if (current.Selection == selection) return current.Lease();
                    _connection = null; current.Release();
                }
            }
            Exception? last = null;
            foreach (var pipe in pipes)
            {
                var handler = new SocketsHttpHandler
                {
                    UseProxy = false,
                    ConnectCallback = async (_, ct) =>
                    {
                        var stream = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
                        try { await stream.ConnectAsync(1000, ct).ConfigureAwait(false); return stream; }
                        catch { stream.Dispose(); throw; }
                    }
                };
                var client = new HttpClient(handler) { BaseAddress = new Uri("http://docker/"), Timeout = Timeout.InfiniteTimeSpan };
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    using var response = await client.GetAsync("/version", HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    await CheckResponseAsync(response, timeout.Token).ConfigureAwait(false);
                    var version = await ReadJsonAsync(response, DockerJsonContext.Default.EngineVersionDto, timeout.Token).ConfigureAwait(false);
                    if (!Version.TryParse(version.ApiVersion, out var maximum) || maximum < new Version(1, 41)
                        || (Version.TryParse(version.MinAPIVersion, out var minimum) && minimum > new Version(1, 51)))
                        throw new InvalidDataException("Engine API 版本不支持本工具的容器操作（需要可协商的 1.41—1.51）。");
                    var connection = new EngineConnection(client, "/v" + (maximum > new Version(1, 51) ? "1.51" : version.ApiVersion), pipe, selection);
                    lock (_connectionGate)
                    {
                        ObjectDisposedException.ThrowIf(_disposed, this);
                        _connection = connection;
                        return connection.Lease();
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { client.Dispose(); throw; }
                catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException or JsonException)
                { client.Dispose(); last = error; }
                catch { client.Dispose(); throw; }
            }
            throw new IOException("无法连接本机 Docker Engine。请确认 Docker 已启动且当前用户可访问其管道。", last);
        }
        finally { _initialization.Release(); }
    }

    private void Invalidate(EngineConnection connection)
    {
        lock (_connectionGate)
        {
            if (_connection != connection) return;
            _connection = null;
            // 正在等待 stop?t=-1 的请求持有独立租约，后台恢复连接不能中断它。
            connection.Release();
        }
    }
    private sealed class EngineConnection(HttpClient http, string api, string pipe, string selection)
    {
        public HttpClient Http { get; } = http;
        public string Api { get; } = api;
        public string Pipe { get; } = pipe;
        public string Selection { get; } = selection;
        private int _references = 1;
        public ConnectionLease Lease() { Interlocked.Increment(ref _references); return new(this); }
        public void Release() { if (Interlocked.Decrement(ref _references) == 0) Http.Dispose(); }
    }
    private sealed class ConnectionLease(EngineConnection connection) : IDisposable
    {
        public EngineConnection Connection { get; } = connection;
        public void Dispose() => Connection.Release();
    }

    internal static string[] DiscoverPipes()
    {
        string directory = Environment.GetEnvironmentVariable("DOCKER_CONFIG")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docker");
        string? context = Environment.GetEnvironmentVariable("DOCKER_CONTEXT");
        string? endpoint = Environment.GetEnvironmentVariable("DOCKER_HOST");
        if (!string.IsNullOrEmpty(context)) endpoint = null;
        if (string.IsNullOrEmpty(context) && string.IsNullOrEmpty(endpoint))
        {
            string config = Path.Combine(directory, "config.json");
            if (File.Exists(config)) context = ReadContext(config)?.CurrentContext;
        }
        if (!string.IsNullOrWhiteSpace(context) && context != "default")
        {
            string root = Path.Combine(directory, "contexts", "meta");
            string? selected = null;
            if (Directory.Exists(root))
                foreach (string folder in Directory.EnumerateDirectories(root).Take(256))
                {
                    string file = Path.Combine(folder, "meta.json");
                    var meta = File.Exists(file) ? ReadContext(file) : null;
                    if (meta?.Name == context) { selected = meta.Endpoints?.Docker?.Host; break; }
                }
            if (selected == null) throw new InvalidOperationException("无法解析当前 Docker context，未回退到其他 Engine。");
            endpoint = selected;
        }
        if (!string.IsNullOrWhiteSpace(endpoint)) return new[] { ParseLocalPipe(endpoint) };
        return new[] { "docker_engine", "dockerDesktopLinuxEngine", "dockerDesktopWindowsEngine" };
    }
    internal static string ParseLocalPipe(string endpoint)
    {
        const string prefix = "npipe:////./pipe/";
        string normalized = endpoint.Replace('\\', '/');
        if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("当前 Docker 端点不是本机 Named Pipe；远程 context 不参与 Windows 宿主端口关联。");
        string pipe = normalized[prefix.Length..];
        if (pipe.Length == 0 || pipe.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.')))
            throw new InvalidDataException("Docker 管道名称无效。");
        return pipe;
    }
    private static DockerContextDto? ReadContext(string file)
    {
        if (new FileInfo(file).Length > 1024 * 1024) throw new InvalidDataException("Docker 配置超过读取上限。");
        return JsonSerializer.Deserialize(File.ReadAllText(file), DockerJsonContext.Default.DockerContextDto);
    }
    private async Task<T> GetAsync<T>(EngineConnection connection, string path, JsonTypeInfo<T> type, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var response = await connection.Http.GetAsync(connection.Api + path, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            await CheckResponseAsync(response, timeout.Token).ConfigureAwait(false);
            return await ReadJsonAsync(response, type, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { Invalidate(connection); throw new TimeoutException("Docker 查询超时，无法确认容器状态。"); }
        catch { Invalidate(connection); throw; }
    }
    private async Task<ContainerInspectDto?> GetOptionalAsync(EngineConnection connection, string path, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var response = await connection.Http.GetAsync(connection.Api + path, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            await CheckResponseAsync(response, timeout.Token).ConfigureAwait(false);
            return await ReadJsonAsync(response, DockerJsonContext.Default.ContainerInspectDto, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { Invalidate(connection); throw new TimeoutException("Docker 查询超时，无法确认容器状态。"); }
        catch { Invalidate(connection); throw; }
    }
    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> type, CancellationToken token)
    {
        const int limit = 8 * 1024 * 1024;
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream(); var block = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(block, token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > limit) throw new InvalidDataException("Docker 响应超过 8 MiB 上限。");
            buffer.Write(block, 0, count);
        }
        return JsonSerializer.Deserialize(buffer.ToArray(), type) ?? throw new InvalidDataException("Docker 响应为空。");
    }
    private static Task CheckResponseAsync(HttpResponseMessage response, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotModified)
            throw new HttpRequestException($"Docker API 返回 {(int)response.StatusCode} ({response.ReasonPhrase})；操作结果尚未确认。", null, response.StatusCode);
        return Task.CompletedTask;
    }
    private static string SafeId(string id)
        => id.Length == 64 && id.All(Uri.IsHexDigit) ? id : throw new InvalidDataException("Engine 返回的完整容器 ID 无效。");

    internal static DockerContainer Convert(ContainerInspectDto dto, string engine)
    {
        var bindings = new List<PublishedBinding>(); bool unresolved = false;
        foreach (var port in dto.NetworkSettings?.Ports ?? new Dictionary<string, BindingDto[]?>())
        {
            string[] parts = port.Key.Split('/');
            if (parts.Length != 2 || !int.TryParse(parts[0], out int inner) || inner is < 1 or > 65535 || parts[1] is not ("tcp" or "udp")) continue;
            foreach (var binding in port.Value ?? Array.Empty<BindingDto>())
            {
                if (!IPAddress.TryParse(binding.HostIp, out var address) || !int.TryParse(binding.HostPort, out int host) || host is < 1 or > 65535)
                { unresolved = true; continue; }
                bindings.Add(new(new(address, host, parts[1] == "tcp" ? PortTransport.Tcp : PortTransport.Udp), inner));
            }
        }
        var labels = dto.Config?.Labels;
        return new(new(engine, SafeId(dto.Id)), dto.Name.TrimStart('/'), dto.Config?.Image ?? "", dto.Image,
            labels?.GetValueOrDefault("com.docker.compose.project"), labels?.GetValueOrDefault("com.docker.compose.service"),
            dto.HostConfig?.NetworkMode ?? "", dto.State?.Running == true, dto.State?.Restarting == true,
            dto.State?.StartedAt ?? "", dto.RestartCount, dto.Config?.StopSignal ?? "",
            new(dto.HostConfig?.RestartPolicy?.Name ?? "no", dto.HostConfig?.RestartPolicy?.MaximumRetryCount ?? 0),
            bindings.Distinct().ToArray(), unresolved, labels?.ContainsKey("com.docker.swarm.service.id") == true);
    }
    public void Dispose()
    {
        lock (_connectionGate) { _disposed = true; _connection?.Release(); _connection = null; }
    }
}

internal sealed class EngineVersionDto { public string ApiVersion { get; set; } = ""; public string MinAPIVersion { get; set; } = ""; }
internal sealed class EngineInfoDto { public string ID { get; set; } = ""; }
internal sealed class ContainerListDto { public string Id { get; set; } = ""; }
internal sealed class ContainerInspectDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Image { get; set; } = "";
    public int RestartCount { get; set; }
    public ContainerConfigDto? Config { get; set; }
    public ContainerStateDto? State { get; set; }
    public HostConfigDto? HostConfig { get; set; }
    public NetworkSettingsDto? NetworkSettings { get; set; }
}
internal sealed class ContainerConfigDto { public string Image { get; set; } = ""; public string StopSignal { get; set; } = ""; public Dictionary<string, string>? Labels { get; set; } }
internal sealed class ContainerStateDto { public bool Running { get; set; } public bool Restarting { get; set; } public string StartedAt { get; set; } = ""; }
internal sealed class HostConfigDto { public string NetworkMode { get; set; } = ""; public PolicyDto? RestartPolicy { get; set; } }
internal sealed class PolicyDto { public string Name { get; set; } = "no"; public int MaximumRetryCount { get; set; } }
internal sealed class NetworkSettingsDto { public Dictionary<string, BindingDto[]?>? Ports { get; set; } }
internal sealed class BindingDto { public string HostIp { get; set; } = ""; public string HostPort { get; set; } = ""; }
internal sealed class UpdateRequestDto { public PolicyDto RestartPolicy { get; set; } = new(); }
internal sealed class DockerContextDto
{
    [JsonPropertyName("currentContext")] public string? CurrentContext { get; set; }
    public string? Name { get; set; }
    public ContextEndpointsDto? Endpoints { get; set; }
}
internal sealed class ContextEndpointsDto { [JsonPropertyName("docker")] public ContextEndpointDto? Docker { get; set; } }
internal sealed class ContextEndpointDto { public string? Host { get; set; } }

[JsonSerializable(typeof(EngineVersionDto))]
[JsonSerializable(typeof(EngineInfoDto))]
[JsonSerializable(typeof(ContainerListDto[]))]
[JsonSerializable(typeof(ContainerInspectDto))]
[JsonSerializable(typeof(UpdateRequestDto))]
[JsonSerializable(typeof(DockerContextDto))]
[JsonSerializable(typeof(RestartPolicyChange))]
internal partial class DockerJsonContext : JsonSerializerContext;
