using System.Collections.Generic;
using System.Collections.Immutable;
using PortKiller.Models;

namespace PortKiller.Features.Ports.Services;

internal static class PortsProcessMetadataAdapter
{
    internal static readonly IReadOnlyDictionary<ProcessIdentity, ProcessMetadata> Empty =
        ImmutableDictionary<ProcessIdentity, ProcessMetadata>.Empty;
    internal static void Apply(IEnumerable<PortConnectionItem> rows, IReadOnlyDictionary<ProcessIdentity, ProcessMetadata> snapshot)
    {
        foreach (var row in rows) row.ApplyMetadata(PortConnectionQueryService.GetMetadata(row, snapshot));
    }
    internal static bool IsPending(IReadOnlyList<PortConnection> rows, IReadOnlyDictionary<ProcessIdentity, ProcessMetadata> snapshot)
    {
        foreach (var row in rows)
            if (!row.IsTimeWait && row.Pid > 4 && row.ProcessIdentity.IsVerified &&
                snapshot.TryGetValue(row.ProcessIdentity, out var metadata) && metadata.State == ProcessMetadataState.Pending) return true;
        return false;
    }
}
