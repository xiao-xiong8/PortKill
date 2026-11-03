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
        IProcessMetadataProvider? metadata = null, bool ownsMetadata = false,
        PortKiller.Docker.IDockerEngineClient? dockerEngine = null, PortKiller.Docker.IRestartPolicyJournal? dockerPolicies = null,
        PortKiller.Docker.ContainerStopCoordinator? containerStops = null)
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
        DockerEngine = dockerEngine ?? new PortKiller.Docker.DockerEngineClient();
        DockerInventory = new PortKiller.Docker.DockerInventoryCoordinator(DockerEngine);
        DockerPolicies = dockerPolicies ?? new PortKiller.Docker.RestartPolicyJournal();
        ContainerStops = containerStops ?? new PortKiller.Docker.ContainerStopCoordinator(DockerEngine,
            ct => Scans.ScanAsync(0, ct), new PortKiller.Docker.WindowsEndpointAvailabilityProbe(), DockerPolicies);
    }

    public NetworkScannerService Scanner { get; }
    public ProcessManagerService Processes { get; }
    public ProcessDetailService Details { get; }
    public HttpSysDiagnosticService Diagnostics { get; }
    public ScanCoordinator Scans { get; }
    public PortReleaseService Release { get; }
    public IProcessMetadataProvider? Metadata { get; }
    public PortKiller.Docker.IDockerEngineClient DockerEngine { get; }
    public PortKiller.Docker.DockerInventoryCoordinator DockerInventory { get; }
    public PortKiller.Docker.IRestartPolicyJournal DockerPolicies { get; }
    public PortKiller.Docker.ContainerStopCoordinator ContainerStops { get; }
    public void Dispose()
    {
        DockerInventory.Dispose();
        DockerEngine.Dispose();
        _metadataSession?.Dispose();
        if (_ownsMetadata) Metadata?.Dispose();
    }
}
