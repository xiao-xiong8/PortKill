using System;
using System.Collections.Generic;
using System.Linq;
using PortKiller.Models;
using System.Threading;

namespace PortKiller.Docker;

/// <summary>服务名称和类别不参与关联；通配重叠只产生候选，不能任选一个容器。</summary>
public static class EndpointOwnershipResolver
{
    public static EndpointOwnership Resolve(PortConnection row, DockerInventory inventory,
        IReadOnlyList<PortConnection>? hostRows = null, DateTimeOffset? now = null)
        => CreateIndex(inventory, hostRows ?? new[] { row }).Resolve(row, now);

    internal static EndpointOwnershipIndex CreateIndex(DockerInventory inventory, IReadOnlyList<PortConnection> hostRows,
        CancellationToken token = default) => new(inventory, hostRows, token);

    public static IReadOnlyList<ContainerStopPlan> CreatePlans(IEnumerable<PortConnection> rows, DockerInventory inventory,
        IReadOnlyList<PortConnection> hostRows)
    {
        var index = CreateIndex(inventory, hostRows);
        var resolved = rows.Select(row => (Row: row, Owner: index.Resolve(row))).ToArray();
        if (resolved.Any(x => x.Owner.Kind != OwnershipKind.Docker))
            throw new InvalidOperationException(string.Join("\n", resolved.Where(x => x.Owner.Kind != OwnershipKind.Docker).Select(x => x.Owner.Evidence)));
        return resolved.GroupBy(x => x.Owner.Container!.Identity).Select(group => new ContainerStopPlan(
            group.First().Owner.Container!, group.Select(x => HostEndpoint.From(x.Row)).Distinct().ToArray(), DateTimeOffset.UtcNow,
            group.All(x => index.HasHostBinding(HostEndpoint.From(x.Row))))).ToArray();
    }
}
