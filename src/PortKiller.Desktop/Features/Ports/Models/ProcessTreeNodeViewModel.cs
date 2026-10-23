namespace PortKiller.Models;

public sealed class ProcessTreeNodeViewModel : ProcessTreeNode
{
    public Avalonia.Thickness RowPadding => new(IsRoot ? 8 : 8 + Depth * 16, 0, 0, 0);
    public static ProcessTreeNodeViewModel FromSnapshot(ProcessTreeNode item) => new()
    { Name = item.Name, Pid = item.Pid, Depth = item.Depth, IsRoot = item.IsRoot };
}
