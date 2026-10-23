using PortKiller.Modules.Ports;
using PortKiller.Services;

namespace PortKiller.Bootstrap;

/// <summary>桌面宿主的显式组合入口；构造依赖时不执行系统查询。</summary>
internal static class ApplicationComposition
{
    internal static PortKiller.Shell.ToolRegistration[] CreateToolCatalog() =>
    [
        new("ports", "端口监控", "M3 4h18v7H3z M3 14h18v7H3z", LegacyIndex: 0),
        new("kernel-diagnostics", "内核诊断", "M12 21s7-3.2 7-9V5.6L12 3 5 5.6V12c0 5.8 7 9 7 9Z M9 12l2 2 4-4", LegacyIndex: 1),
        new("settings", "系统设置", "M12 3v3 M12 18v3 M3 12h3 M18 12h3 M6 6l2 2 M16 16l2 2 M6 18l2-2 M16 8l2-2 M15 12a3 3 0 1 1-6 0 3 3 0 0 1 6 0", LegacyIndex: 2)
    ];

    internal static PortToolServices CreatePortServices(
        HttpSysDiagnosticService? diagnostics = null, NetworkScannerService? scanner = null,
        PortKiller.Models.IProcessMetadataProvider? metadata = null)
        => new(scanner ?? new NetworkScannerService(), new ProcessManagerService(),
            new ProcessDetailService(), diagnostics ?? new HttpSysDiagnosticService(),
            metadata ?? new PortKiller.Processes.WindowsProcessMetadataProvider(), ownsMetadata: metadata == null);
}
