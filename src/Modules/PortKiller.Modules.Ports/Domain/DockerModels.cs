using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace PortKiller.Docker;

public enum PortTransport { Tcp, Udp }

public sealed record HostEndpoint(IPAddress Address, int Port, PortTransport Transport)
{
    public static HostEndpoint From(PortKiller.Models.PortConnection row)
    {
        if (!IPAddress.TryParse(row.LocalAddress, out var address) || row.LocalPort is < 1 or > 65535
            || row.Protocol is not ("TCP" or "UDP")
            || (address.AddressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6") != row.IpVersion)
            throw new ArgumentException("宿主端点的地址、地址族、端口或协议无效。");
        return new(address, row.LocalPort, row.Protocol == "TCP" ? PortTransport.Tcp : PortTransport.Udp);
    }
    public bool IsWildcard => Address.Equals(IPAddress.Any) || Address.Equals(IPAddress.IPv6Any);
    public bool Overlaps(HostEndpoint other) => Port == other.Port && Transport == other.Transport
        && Address.AddressFamily == other.Address.AddressFamily
        && (Address.Equals(other.Address) || IsWildcard || other.IsWildcard);
    public override string ToString() => $"{Transport.ToString().ToUpperInvariant()}/{(Address.AddressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6")} "
        + (Address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{Address}]:{Port}" : $"{Address}:{Port}");
}

public sealed record ContainerIdentity(string EngineId, string Id);
public sealed record PublishedBinding(HostEndpoint Host, int ContainerPort);
public sealed record RestartPolicy(string Name, int MaximumRetryCount = 0);
public sealed record DockerContainer(ContainerIdentity Identity, string Name, string Image, string ImageId,
    string? ComposeProject, string? ComposeService, string NetworkMode, bool Running, bool Restarting,
    string StartedAt, int RestartCount, string StopSignal, RestartPolicy RestartPolicy,
    IReadOnlyList<PublishedBinding> Bindings, bool HasUnresolvedBindings = false, bool IsSwarmTask = false)
{
    public string ShortId => Identity.Id[..Math.Min(12, Identity.Id.Length)];
    public string BindingSignature => string.Join("|", Bindings.Select(b => $"{b.Host}>{b.ContainerPort}").Order(StringComparer.Ordinal));
    public string Description => $"{Name} ({ShortId}) · {Image}\nCompose：{ComposeProject ?? "未提供"} / {ComposeService ?? "未提供"}"
        + $"\n完整 ID：{Identity.Id}\nEngine：{Identity.EngineId}"
        + $"\n重启策略：{RestartPolicy.Name} ({RestartPolicy.MaximumRetryCount})\n"
        + string.Join("\n", Bindings.Select(b => $"{b.Host} → {b.ContainerPort}/{b.Host.Transport.ToString().ToLowerInvariant()}"));
    public bool SameInstance(DockerContainer other) => Identity == other.Identity && StartedAt == other.StartedAt
        && RestartCount == other.RestartCount && BindingSignature == other.BindingSignature
        && NetworkMode == other.NetworkMode && StopSignal == other.StopSignal
        && IsSwarmTask == other.IsSwarmTask && HasUnresolvedBindings == other.HasUnresolvedBindings;
}

public sealed record DockerInventory(string EngineId, DateTimeOffset ObservedAt, IReadOnlyList<DockerContainer> Containers,
    string? Error = null, string Endpoint = "")
{
    public static DockerInventory Unavailable(string reason) => new("", DateTimeOffset.UtcNow, Array.Empty<DockerContainer>(), reason);
    public bool IsFresh(DateTimeOffset now) => Error == null && EngineId.Length > 0
        && now >= ObservedAt && now - ObservedAt <= TimeSpan.FromSeconds(5);
}

public enum OwnershipKind { Native, Docker, Ambiguous, Unknown, Unsupported }
public sealed record EndpointOwnership(OwnershipKind Kind, string Evidence, DockerContainer? Container = null)
{
    public bool HasDockerEvidence => Kind != OwnershipKind.Native;
    public string Display => Container is { } c ? $"Docker · {c.Name} ({c.ShortId})" : Kind switch
    {
        OwnershipKind.Native => "Windows 原生", OwnershipKind.Ambiguous => "Docker 多个候选",
        OwnershipKind.Unsupported => "Docker 特殊网络", _ => "Docker 待确认"
    };
}

public sealed record ContainerStopPlan(DockerContainer Container, IReadOnlyList<HostEndpoint> Targets, DateTimeOffset CreatedAt, bool HadHostBinding = true);
public enum ContainerStopState { Released, PortHeld, Reoccupied, AwaitingForceConfirmation, Failed, Unknown }
public sealed record ContainerStopResult(ContainerStopState State, string Detail, bool Pending = true)
{
    public bool Released => State == ContainerStopState.Released;
}
