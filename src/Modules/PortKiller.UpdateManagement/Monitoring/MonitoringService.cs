using System.Text.Json;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Contracts;
using PortKiller.UpdateManagement.Security;

namespace PortKiller.UpdateManagement.Monitoring;

/// <summary>Reads a bounded snapshot; never queries Docker or contacts application probes.</summary>
public sealed class MonitoringService(AdminOptions options, IPublishedReleaseReader releases, TimeProvider clock)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { MaxDepth = 16, RespectRequiredConstructorParameters = true };

    public async Task<MonitoringResponse> GetAsync(CancellationToken token)
    {
        long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        if (string.IsNullOrWhiteSpace(options.MonitoringSnapshotDirectory)) return Empty("unconfigured", now);
        MonitoringSnapshot snapshot;
        try
        {
            await using var input = new FileStream(Path.Combine(options.MonitoringSnapshotDirectory, "status.json"),
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
            if (input.Length > 524_288) return Empty("invalid_snapshot", now);
            // Bound the actual read too, including a file that grows after opening.
            byte[] bytes = new byte[524_289];
            int length = 0, read;
            while (length < bytes.Length && (read = await input.ReadAsync(bytes.AsMemory(length), token)) > 0) length += read;
            if (length > 524_288) return Empty("invalid_snapshot", now);
            snapshot = JsonSerializer.Deserialize<MonitoringSnapshot>(bytes.AsSpan(0, length), JsonOptions)
                ?? throw new JsonException();
            if (!MonitoringValidation.Valid(snapshot, now)) return Empty("invalid_snapshot", now);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (JsonException) { return Empty("invalid_snapshot", now); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return Empty("unavailable", now); }

        var alerts = new List<MonitoringAlert>();
        MonitoringManifest manifest;
        try
        {
            PublishedRelease? release = await releases.ReadAsync(token);
            now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            double? days = release == null ? null : (release.ExpiresUtcMs - now) / TimeSpan.FromDays(1).TotalMilliseconds;
            string state = days == null ? "missing" : days <= 0 ? "expired" : days <= 7 ? "critical" : days <= 14 ? "warning" : "healthy";
            manifest = new(state, release?.Version, days);
            if (state is "expired" or "critical" or "warning")
                alerts.Add(new("manifest_expiry", "manifest", state == "warning" ? "warning" : "critical",
                    state == "expired" ? "正式清单已过期。" : "正式清单即将到期。"));
            if (state == "missing") alerts.Add(new("manifest_missing", "manifest", "warning", "未检测到有效正式清单，发布状态待确认。"));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (AdminFailure) { manifest = new("unavailable", null, null); alerts.Add(new("manifest_unavailable", "manifest", "warning", "无法核对正式清单。")); }

        now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        int usable = 0, total = 0;
        bool collectorStale = now - snapshot.GeneratedAtUtcMs >= MonitoringPolicy.CollectorTtlMs;
        bool Fresh(string source, long? observed, string? error, int ttl)
        {
            total++;
            bool fresh = error == null && observed.HasValue && now - observed.Value < ttl;
            if (fresh) usable++;
            else alerts.Add(new("source_unavailable", source, "warning", $"{SourceLabel(source)}数据不可用或已过期。"));
            return fresh;
        }
        var resources = snapshot.Resources;
        bool cpu = Fresh("cpu", resources.Cpu.ObservedAtUtcMs, resources.Cpu.Error, MonitoringPolicy.ResourceTtlMs);
        bool memory = Fresh("memory", resources.Memory.ObservedAtUtcMs, resources.Memory.Error, MonitoringPolicy.ResourceTtlMs);
        bool network = Fresh("network", resources.Network.ObservedAtUtcMs, resources.Network.Error, MonitoringPolicy.ResourceTtlMs);
        var disks = resources.Disks.Select(disk =>
        {
            if (!Fresh(disk.Label, disk.ObservedAtUtcMs, disk.Error, MonitoringPolicy.DiskTtlMs))
                return disk with { TotalBytes = null, FreeBytes = null };
            if (disk.TotalBytes is > 0 && disk.FreeBytes is { } free)
            {
                double percent = free * 100d / disk.TotalBytes.Value;
                string? severity = percent < 5 || free < 536_870_912 ? "critical"
                    : percent < 10 || free < 1_073_741_824 ? "warning" : null;
                if (severity != null) alerts.Add(new("disk_space", disk.Label, severity, $"{disk.Label}剩余空间不足。"));
            }
            return disk;
        }).ToArray();
        if (disks.Length == 0) Fresh("disk", null, null, MonitoringPolicy.DiskTtlMs);
        bool docker = Fresh("docker", snapshot.Docker.ObservedAtUtcMs, snapshot.Docker.Error, MonitoringPolicy.DockerTtlMs);
        if (docker)
            foreach (var service in snapshot.Docker.Services)
                if (service.State != "running" || service.Health == "unhealthy")
                    alerts.Add(new("container_state", service.Service, "critical", $"{service.Service} 容器状态异常。"));
        var probes = snapshot.AppChecks.Select(probe =>
        {
            if (!Fresh(probe.Name, probe.ObservedAtUtcMs, probe.Error, MonitoringPolicy.ProbeTtlMs)) return probe with { Status = "unknown", Database = null, ManifestStatus = null, ManifestValid = null };
            if (probe.Status == "missing" && probe.Name == "download" && manifest.Status is "missing" or "unavailable")
                return probe with { Status = "unconfirmed" };
            if (probe.Status != "healthy") alerts.Add(new("application_failed", probe.Name, "critical", $"{SourceLabel(probe.Name)}检查失败。"));
            if (probe.Name == "notifier" && probe.ManifestValid != true && manifest.Version != null)
                alerts.Add(new("notifier_manifest", "notifier", "critical", "通知服务未加载有效正式清单。"));
            return probe;
        }).ToArray();
        foreach (string name in new[] { "api", "notifier", "download" }.Except(probes.Select(probe => probe.Name))) Fresh(name, null, null, MonitoringPolicy.ProbeTtlMs);

        snapshot = snapshot with
        {
            Resources = resources with
            {
                Cpu = cpu ? resources.Cpu : resources.Cpu with { Percent = null },
                Memory = memory ? resources.Memory : resources.Memory with { TotalBytes = null, UsedBytes = null },
                Network = network ? resources.Network : resources.Network with { RxBytesPerSec = null, TxBytesPerSec = null },
                Disks = disks
            },
            Docker = docker ? snapshot.Docker : snapshot.Docker with { Services = [] },
            AppChecks = probes
        };
        return new(collectorStale ? "stale" : usable < total ? "degraded" : "ok", now, snapshot, manifest, alerts.ToArray(), MonitoringPolicy.Freshness);
    }

    private static MonitoringResponse Empty(string state, long now) => new(state, now, null, new("unknown", null, null),
        state == "unconfigured" ? [] : [new("snapshot_unavailable", "collector", "warning", "监控快照不可用，请检查采集容器。")], MonitoringPolicy.Freshness);

    private static string SourceLabel(string source) => source switch
    {
        "cpu" => "CPU", "memory" => "内存", "network" => "网络", "disk" => "磁盘", "docker" => "Docker",
        "api" => "API", "notifier" => "通知服务", "download" => "清单入口", _ => source
    };
}
