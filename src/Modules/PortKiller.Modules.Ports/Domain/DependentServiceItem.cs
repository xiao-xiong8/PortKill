namespace PortKiller.Models;

public class DependentServiceItem
{
    public string ServiceName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsRunning { get; set; }
    public bool IsInstalled { get; set; } = true;

    public string StateText => !IsInstalled ? "未安装" : IsRunning ? "运行中" : "已停止";
    public bool IsStopped => IsInstalled && !IsRunning;
}
