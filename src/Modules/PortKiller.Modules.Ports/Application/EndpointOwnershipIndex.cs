using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using PortKiller.Models;
using PortKiller.Native;

namespace PortKiller.Docker;

/// <summary>一份快照共享索引：地址只解析一次，同一宿主端点的归属只计算一次。</summary>
internal sealed class EndpointOwnershipIndex
{
    private readonly DockerInventory _inventory;
    private readonly Dictionary<PortConnection, HostEndpoint?> _endpoints = new();
    private readonly Dictionary<EndpointKey, PublishedPorts> _published = new();
    private readonly Dictionary<EndpointKey, HostAddresses> _conflicts = new(), _bindings = new();
    private readonly Dictionary<HostEndpoint, EndpointOwnership?> _resolved = new();
    private readonly record struct EndpointKey(PortTransport Transport, AddressFamily Family, int Port)
    {
        internal static EndpointKey From(HostEndpoint endpoint) => new(endpoint.Transport, endpoint.Address.AddressFamily, endpoint.Port);
    }

    internal EndpointOwnershipIndex(DockerInventory inventory, IReadOnlyList<PortConnection> hosts, CancellationToken token)
    {
        _inventory = inventory;
        foreach (var host in hosts)
        {
            token.ThrowIfCancellationRequested();
            var endpoint = Parse(host); _endpoints[host] = endpoint;
            if (endpoint == null || host.IsDockerPublication || host.IsTimeWait) continue;
            var key = EndpointKey.From(endpoint);
            if (host.IsListening || host.Protocol == "UDP") Get(_bindings, key).Add(endpoint.Address, host);
            if ((host.IsListening || host.IsEstablished || host.Protocol == "UDP") && host.Pid > 4
                && !SystemProtection.IsContainerPlatform(host.ProcessName)) Get(_conflicts, key).Add(endpoint.Address, host);
        }
        foreach (var container in inventory.Containers)
        {
            token.ThrowIfCancellationRequested();
            if (!container.Running && !container.Restarting) continue;
            foreach (var binding in container.Bindings)
            {
                token.ThrowIfCancellationRequested();
                var key = EndpointKey.From(binding.Host);
                if (!_published.TryGetValue(key, out var ports)) _published.Add(key, ports = new());
                ports.Add(binding.Host.Address, container);
            }
        }
    }

    internal EndpointOwnership Resolve(PortConnection row, DateTimeOffset? now = null)
    {
        if (!_endpoints.TryGetValue(row, out var target)) _endpoints.Add(row, target = Parse(row));
        if (target == null) return new(OwnershipKind.Unknown, "宿主端点的地址、地址族、端口或协议无效。");
        if (!_inventory.IsFresh(now ?? DateTimeOffset.UtcNow))
            return new(OwnershipKind.Unknown, _inventory.Error ?? "Docker 快照已过期，请重新核验。");
        if (!_resolved.TryGetValue(target, out var owner)) _resolved.Add(target, owner = ResolvePublished(target));
        if (owner != null) return owner;
        bool platform = SystemProtection.IsContainerPlatform(row.ProcessName);
        return new(platform || row.IsDockerPublication ? OwnershipKind.Unknown : OwnershipKind.Native,
            platform ? "宿主为容器平台承载进程，但尚无唯一发布关系，禁止终止承载进程。" : "本机 Engine 未发现对应的实际端口发布。");
    }

    private EndpointOwnership? ResolvePublished(HostEndpoint target)
    {
        var key = EndpointKey.From(target);
        if (!_published.TryGetValue(key, out var ports)) return null;
        var candidates = ports.Find(target);
        if (candidates.Count == 0) return null;
        if (candidates.Count != 1)
            return new(OwnershipKind.Ambiguous, "端点覆盖多个容器：" + string.Join("、", candidates.Select(c => $"{c.Name} ({c.ShortId})")));
        var container = candidates[0];
        if (container.NetworkMode is "host" || container.NetworkMode.StartsWith("container:", StringComparison.Ordinal)
            || container.IsSwarmTask || container.HasUnresolvedBindings)
            return new(OwnershipKind.Unsupported, "特殊网络拓扑或发布地址不完整，尚不能唯一确认操作范围。", container);
        if (container.Restarting) return new(OwnershipKind.Unknown, "容器正在重新启动，请等待稳定后核验。", container);
        if (target.IsWildcard && !ports.HasWildcard)
            return new(OwnershipKind.Ambiguous, "Windows 通配端点与 Docker 的具体地址只存在覆盖关系，请选中具体 Docker 发布记录。", container);
        if (_conflicts.TryGetValue(key, out var conflicts) && conflicts.Find(target) is { } host)
            return new(OwnershipKind.Ambiguous, $"Engine 有发布记录，但 Windows 仍由 {host.ProcessName} (PID {host.Pid}) 持有；转发关系尚未确认。", container);
        return new(OwnershipKind.Docker, $"本机 Engine {_inventory.EngineId} 的实际发布映射唯一匹配 {target}。宿主 PID 不作为容器身份。", container);
    }

    internal bool HasHostBinding(HostEndpoint target)
        => _bindings.TryGetValue(EndpointKey.From(target), out var hosts) && hosts.Find(target) != null;
    private static HostEndpoint? Parse(PortConnection row)
    { try { return HostEndpoint.From(row); } catch (ArgumentException) { return null; } }
    private static HostAddresses Get(Dictionary<EndpointKey, HostAddresses> map, EndpointKey key)
    { if (!map.TryGetValue(key, out var result)) map.Add(key, result = new()); return result; }
    private static bool IsWildcard(IPAddress address) => address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);
    private static IPAddress Wildcard(HostEndpoint target) => target.Address.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any;

    private sealed class HostAddresses
    {
        private readonly Dictionary<IPAddress, PortConnection> _addresses = new();
        private PortConnection? _first;
        internal void Add(IPAddress address, PortConnection host) { _addresses.TryAdd(address, host); _first ??= host; }
        internal PortConnection? Find(HostEndpoint target) => target.IsWildcard ? _first
            : _addresses.GetValueOrDefault(target.Address) ?? _addresses.GetValueOrDefault(Wildcard(target));
    }
    private sealed class PublishedPorts
    {
        private readonly Dictionary<IPAddress, Dictionary<ContainerIdentity, DockerContainer>> _addresses = new();
        private readonly Dictionary<ContainerIdentity, DockerContainer> _all = new();
        internal bool HasWildcard { get; private set; }
        internal void Add(IPAddress address, DockerContainer container)
        {
            if (!_addresses.TryGetValue(address, out var group)) _addresses.Add(address, group = new());
            group.TryAdd(container.Identity, container); _all.TryAdd(container.Identity, container);
            HasWildcard |= IsWildcard(address);
        }
        internal List<DockerContainer> Find(HostEndpoint target)
        {
            if (target.IsWildcard) return _all.Values.ToList();
            var candidates = new Dictionary<ContainerIdentity, DockerContainer>();
            if (_addresses.TryGetValue(target.Address, out var exact)) foreach (var pair in exact) candidates.TryAdd(pair.Key, pair.Value);
            if (_addresses.TryGetValue(Wildcard(target), out var wildcard)) foreach (var pair in wildcard) candidates.TryAdd(pair.Key, pair.Value);
            return candidates.Values.ToList();
        }
    }
}
