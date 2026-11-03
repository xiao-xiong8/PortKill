using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PortKiller.Models;

namespace PortKiller.Docker;

public sealed class DockerInventoryCoordinator : IDisposable
{
    private readonly IDockerEngineClient _engine;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _loop;
    private DockerInventory _snapshot = DockerInventory.Unavailable("Docker 尚未查询。");
    public DockerInventory Snapshot => Volatile.Read(ref _snapshot);
    public event Action? Changed;
    public DockerInventoryCoordinator(IDockerEngineClient engine) => _engine = engine;
    public void Start() { _loop ??= RunAsync(); }
    private async Task RunAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
                DockerInventory next;
                try { next = await _engine.ReadInventoryAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested) { next = DockerInventory.Unavailable("Docker 完整采集超时。"); }
                catch (Exception e) when (e is not OperationCanceledException) { next = DockerInventory.Unavailable(e.Message); }
                Volatile.Write(ref _snapshot, next); Changed?.Invoke();
                await Task.Delay(next.Error == null ? 2000 : 10000, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }
    public static IReadOnlyList<PortConnection> Enrich(IReadOnlyList<PortConnection> source, DockerInventory inventory, CancellationToken token = default)
    {
        var hosts = source.Where(r => !r.IsDockerPublication).ToArray();
        var index = EndpointOwnershipResolver.CreateIndex(inventory, hosts, token);
        var result = new List<PortConnection>(hosts.Length);
        foreach (var host in hosts)
        {
            token.ThrowIfCancellationRequested();
            result.Add(Copy(host, index.Resolve(host)));
        }
        if (inventory.IsFresh(DateTimeOffset.UtcNow))
            foreach (var binding in inventory.Containers.Where(c => c.Running).SelectMany(c => c.Bindings).DistinctBy(b => b.Host))
            {
                token.ThrowIfCancellationRequested();
                var row = PublicationRow(binding.Host);
                row.DockerOwnership = index.Resolve(row);
                result.Add(row);
            }
        return result;
    }
    public static PortConnection PublicationRow(HostEndpoint endpoint) => new()
    {
        Protocol = endpoint.Transport == PortTransport.Tcp ? "TCP" : "UDP",
        IpVersion = endpoint.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "IPv4" : "IPv6",
        LocalAddress = endpoint.Address.ToString(), LocalPort = endpoint.Port, RemoteAddress = "*", State = "DOCKER_PUBLISHED",
        ProcessName = "Docker 发布记录", IsDockerPublication = true
    };
    private static PortConnection Copy(PortConnection row, EndpointOwnership owner) => new()
    {
        Protocol = row.Protocol, IpVersion = row.IpVersion, LocalAddress = row.LocalAddress, LocalPort = row.LocalPort,
        RemoteAddress = row.RemoteAddress, RemotePort = row.RemotePort, State = row.State, RawState = row.RawState,
        Pid = row.Pid, ProcessName = row.ProcessName, ProcessCreationTime = row.ProcessCreationTime, ProcessPath = row.ProcessPath,
        ParentPid = row.ParentPid, IsSystemProtected = row.IsSystemProtected, IsHttpSys = row.IsHttpSys,
        RawLocalAddr = row.RawLocalAddr, RawLocalPort = row.RawLocalPort, RawRemoteAddr = row.RawRemoteAddr, RawRemotePort = row.RawRemotePort,
        DockerOwnership = owner
    };
    public void Dispose() { _lifetime.Cancel(); }
}
