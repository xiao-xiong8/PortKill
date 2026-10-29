using System.Text.Json.Serialization;

namespace PortKiller.UpdateManagement.Contracts;

[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record MonitoringCpu(long? ObservedAtUtcMs, string? Error, double? Percent);
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record MonitoringMemory(long? ObservedAtUtcMs, string? Error, long? TotalBytes, long? UsedBytes);
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record MonitoringNetwork(long? ObservedAtUtcMs, string? Error, string? Interface, double? RxBytesPerSec, double? TxBytesPerSec);
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record MonitoringDisk(string Label, long? ObservedAtUtcMs, string? Error, long? TotalBytes, long? FreeBytes);
public sealed record MonitoringResources(MonitoringCpu Cpu, MonitoringMemory Memory, MonitoringNetwork Network, MonitoringDisk[] Disks);
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record MonitoringPoint(long TimestampUtcMs, double? CpuPercent, double? MemoryPercent,
    double? RxBytesPerSec, double? TxBytesPerSec, string? NetworkInterface);
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record MonitoringContainer(string Service, string? ContainerId, string State, string Health, int RestartCount);
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record MonitoringDocker(long? ObservedAtUtcMs, string? Error, MonitoringContainer[] Services);
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record MonitoringProbe(string Name, long? ObservedAtUtcMs, string? Error, string Status,
    int? HttpStatus, string? Database, string? ManifestStatus, bool? ManifestValid);
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record MonitoringSnapshot(int SchemaVersion, long CollectorStartedAtUtcMs, long GeneratedAtUtcMs,
    MonitoringResources Resources, MonitoringPoint[] Series, MonitoringDocker Docker, MonitoringProbe[] AppChecks);
public sealed record MonitoringAlert(string Code, string Source, string Severity, string Message);
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record MonitoringManifest(string Status, string? Version, double? DaysRemaining);
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record MonitoringFreshness(int ResourceTtlMs, int CollectorTtlMs, int DockerTtlMs, int DiskTtlMs, int ProbeTtlMs);
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record MonitoringResponse(string Status, long ServedAtUtcMs, MonitoringSnapshot? Snapshot,
    MonitoringManifest Manifest, MonitoringAlert[] Alerts, MonitoringFreshness? Freshness = null);
