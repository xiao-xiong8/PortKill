using System;
using System.Collections.Generic;

namespace PortKiller.Models;

public interface IProcessMetadataProvider : IDisposable
{
    event Action? Changed;
    IReadOnlyDictionary<ProcessIdentity, ProcessMetadata> Snapshot { get; }
    IProcessMetadataSession CreateSession();
}

/// <summary>每个消费者持有独立需求；Observe 只在后台采集线程调用。</summary>
public interface IProcessMetadataSession : IDisposable
{
    IReadOnlyDictionary<int, ProcessObservation> Observe(IReadOnlyList<ProcessReference> processes);
}
