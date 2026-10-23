namespace PortKiller.Models;

public class ProcessTreeNode
{
    public string Name { get; set; } = string.Empty;
    public int Pid { get; set; }
    public int Depth { get; set; }
    public bool IsRoot { get; set; }


    public string Display => $"{Name,-16} PID {Pid}";
}
