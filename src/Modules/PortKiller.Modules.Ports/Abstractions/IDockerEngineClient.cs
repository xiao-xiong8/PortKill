using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PortKiller.Docker;

public interface IDockerEngineClient : IDisposable
{
    Task<DockerInventory> ReadInventoryAsync(CancellationToken token);
    Task<DockerContainer?> InspectAsync(ContainerIdentity identity, CancellationToken token);
    Task StopGracefullyAsync(ContainerIdentity identity, CancellationToken token);
    Task KillAsync(ContainerIdentity identity, CancellationToken token);
    Task UpdateRestartPolicyAsync(ContainerIdentity identity, RestartPolicy policy, CancellationToken token);
}

public interface IRestartPolicyJournal
{
    Task SaveAsync(RestartPolicyChange change, CancellationToken token);
    Task<IReadOnlyList<RestartPolicyChange>> ReadAsync(CancellationToken token);
    Task RemoveAsync(RestartPolicyChange change, CancellationToken token);
}

public sealed record RestartPolicyChange(string EngineId, string ContainerId, string ContainerName,
    string OriginalName, int OriginalMaximumRetryCount, DateTimeOffset ChangedAt);
