using PortKiller.UpdateManagement.Contracts;

namespace PortKiller.UpdateManagement.Monitoring;

public static class MonitoringPolicy
{
    public const int ResourceTtlMs = 20_000;
    public const int CollectorTtlMs = 20_000;
    public const int DockerTtlMs = 45_000;
    public const int DiskTtlMs = 90_000;
    public const int ProbeTtlMs = 90_000;
    public static MonitoringFreshness Freshness { get; } = new(ResourceTtlMs, CollectorTtlMs, DockerTtlMs, DiskTtlMs, ProbeTtlMs);
}
