using System.Collections.Generic;

namespace PortKiller.Models;

public class ScanResult
{
    public IReadOnlyList<PortConnection> Items { get; set; } = new List<PortConnection>();
    public double ElapsedMilliseconds { get; set; }
    public int TotalCount => Items.Count;
    public int ListeningCount { get; set; }
    public int TcpCount { get; set; }
    public int UdpCount { get; set; }
    public ulong Fingerprint { get; set; }
    public bool HasChanged { get; set; } = true;
}
