using System;
using PortKiller.Services;
using PortKiller.Models;

namespace PortKiller.Modules.Ports;

/// <summary>端口工作区的依赖集合，刷新和释放复检共享同一采集队列。</summary>
public sealed class PortToolServices : IDisposable
{
    private readonly IProcessMetadataSession? _metadataSession;
    private readonly bool _ownsMetadata;
    public PortToolServices(NetworkScannerService scanner, ProcessManagerService processes,
        ProcessDetailService details, HttpSysDiagnosticService diagnostics,
        IProcessMetadataProvider? metadata = null, bool ownsMetadata = false)
    {
        Scanner = scanner;
        Processes = processes;
        Details = details;
        Diagnostics = diagnostics;
        Metadata = metadata;
        _ownsMetadata = ownsMetadata;
        if (metadata != null)
        {
            _metadataSession = metadata.CreateSession();
            scanner.SetMetadataSession(_metadataSession);
        }
        Scans = new ScanCoordinator(scanner);
        Release = new PortReleaseService(processes, Scans);
    }

    public NetworkScannerService Scanner { get; }
    public ProcessManagerService Processes { get; }
    public ProcessDetailService Details { get; }
    public HttpSysDiagnosticService Diagnostics { get; }
    public ScanCoordinator Scans { get; }
    public PortReleaseService Release { get; }
    public IProcessMetadataProvider? Metadata { get; }
    public void Dispose()
    {
        _metadataSession?.Dispose();
        if (_ownsMetadata) Metadata?.Dispose();
    }
}
