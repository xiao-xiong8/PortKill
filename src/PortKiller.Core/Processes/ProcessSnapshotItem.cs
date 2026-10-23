namespace PortKiller.Models;

public class ProcessSnapshotItem
{
    public int Pid { get; set; }
    public int ParentPid { get; set; }
    public string ProcessName { get; set; } = string.Empty;
}
