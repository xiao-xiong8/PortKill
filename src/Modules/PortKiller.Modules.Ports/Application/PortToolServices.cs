using PortKiller.Services;

namespace PortKiller.Modules.Ports;

/// <summary>端口工作区的依赖集合，刷新和释放复检共享同一采集队列。</summary>
public sealed class PortToolServices
{
    public PortToolServices(NetworkScannerService scanner, ProcessManagerService processes,
        ProcessDetailService details, HttpSysDiagnosticService diagnostics)
    {
        Scanner = scanner;
        Processes = processes;
        Details = details;
        Diagnostics = diagnostics;
        Scans = new ScanCoordinator(scanner);
        Release = new PortReleaseService(processes, Scans);
    }

    public NetworkScannerService Scanner { get; }
    public ProcessManagerService Processes { get; }
    public ProcessDetailService Details { get; }
    public HttpSysDiagnosticService Diagnostics { get; }
    public ScanCoordinator Scans { get; }
    public PortReleaseService Release { get; }
}
