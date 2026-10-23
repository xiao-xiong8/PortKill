using System.Collections.Generic;
using PortKiller.Models;

namespace PortKiller.Modules.Ports.Abstractions;

public interface IPortScanner
{
    ScanResult ScanAllPorts(ulong previousFingerprint = 0);
    Dictionary<int, ProcessSnapshotItem> GetProcessSnapshotMap();
}
