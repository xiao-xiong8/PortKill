using PortKiller.UpdateManagement.Contracts;

namespace PortKiller.UpdateManagement.Monitoring;

internal static class MonitoringValidation
{
    private const long SafeInteger = 9_007_199_254_740_991;
    private static bool Text(string? value) => value == null || value.Length <= 128 && !value.Any(char.IsControl);
    private static bool Time(long? value, long now) => value == null || value > 0 && value <= now + 30_000;
    private static bool Size(long? value) => value == null || value is >= 0 and <= SafeInteger;
    private static bool Number(double? value, double maximum = SafeInteger) => value == null || double.IsFinite(value.Value) && value >= 0 && value <= maximum;

    internal static bool Valid(MonitoringSnapshot value, long now)
    {
        if (value.SchemaVersion != 1 || value.CollectorStartedAtUtcMs <= 0 || value.GeneratedAtUtcMs < value.CollectorStartedAtUtcMs
            || !Time(value.GeneratedAtUtcMs, now) || value.Resources is not { Cpu: { } cpu, Memory: { } memory, Network: { } network, Disks: { } disks }
            || value.Docker is not { Services: { } services } || value.AppChecks is not { } probes || value.Series is not { } points
            || disks.Length > 2 || services.Length > 4 || probes.Length > 3 || points.Length > 181) return false;
        if (!Time(cpu.ObservedAtUtcMs, now) || !Text(cpu.Error) || !Number(cpu.Percent, 100)
            || !Time(memory.ObservedAtUtcMs, now) || !Text(memory.Error) || !Size(memory.TotalBytes) || !Size(memory.UsedBytes)
            || memory.UsedBytes > memory.TotalBytes || !Time(network.ObservedAtUtcMs, now) || !Text(network.Error) || !Text(network.Interface)
            || !Number(network.RxBytesPerSec) || !Number(network.TxBytesPerSec)) return false;
        if (cpu.ObservedAtUtcMs != null && cpu.Error == null && cpu.Percent == null
            || memory.ObservedAtUtcMs != null && memory.Error == null && (memory.TotalBytes is null or 0 || memory.UsedBytes == null)
            || network.ObservedAtUtcMs != null && network.Error == null && (network.Interface == null || network.RxBytesPerSec == null || network.TxBytesPerSec == null)) return false;
        if (disks.Any(disk => disk == null || string.IsNullOrWhiteSpace(disk.Label) || !Text(disk.Label) || !Text(disk.Error)
            || !Time(disk.ObservedAtUtcMs, now) || !Size(disk.TotalBytes) || !Size(disk.FreeBytes) || disk.FreeBytes > disk.TotalBytes
            || disk.ObservedAtUtcMs != null && disk.Error == null && (disk.TotalBytes is null or 0 || disk.FreeBytes == null))) return false;
        if (!Time(value.Docker.ObservedAtUtcMs, now) || !Text(value.Docker.Error)
            || services.Any(service => service == null || service.Service is not ("api" or "worker" or "notifier" or "caddy")
                || !Text(service.ContainerId) || service.State is not ("created" or "running" or "paused" or "restarting" or "removing" or "exited" or "dead" or "missing" or "ambiguous")
                || service.Health is not ("healthy" or "unhealthy" or "starting" or "not_configured") || service.RestartCount < 0)
            || services.Select(service => service.Service).Distinct().Count() != services.Length
            || value.Docker.ObservedAtUtcMs != null && value.Docker.Error == null && services.Length != 4) return false;
        if (probes.Any(probe => probe == null || probe.Name is not ("api" or "notifier" or "download") || !Time(probe.ObservedAtUtcMs, now)
            || !Text(probe.Error) || probe.Status is not ("healthy" or "failed" or "missing" or "unknown")
            || probe.HttpStatus is < 100 or > 599 || probe.Database is not (null or "healthy" or "unavailable" or "unknown")
            || probe.ManifestStatus is not (null or "healthy" or "warning" or "critical" or "expired" or "missing" or "unavailable" or "unknown"))
            || probes.Select(probe => probe.Name).Distinct().Count() != probes.Length) return false;
        long previous = 0;
        foreach (var point in points)
        {
            if (point == null || point.TimestampUtcMs <= previous || !Time(point.TimestampUtcMs, now)
                || !Number(point.CpuPercent, 100) || !Number(point.MemoryPercent, 100) || !Number(point.RxBytesPerSec)
                || !Number(point.TxBytesPerSec) || !Text(point.NetworkInterface)) return false;
            previous = point.TimestampUtcMs;
        }
        return true;
    }
}
