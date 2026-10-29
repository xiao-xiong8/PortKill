using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Identity;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Contracts;
using PortKiller.UpdateManagement.Monitoring;
using PortKiller.UpdateManagement.Security;
using PortKiller.UpdateManagement.Persistence.Entities;

namespace PortKiller.UpdateAdmin.Tests;

public sealed class MonitoringTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed class Source : IPublishedReleaseReader
    {
        public PublishedRelease? Release { get; set; }
        public Task<PublishedRelease?> ReadAsync(CancellationToken token) => Task.FromResult(Release);
    }

    private sealed class Fixture : IDisposable
    {
        public ManualClock Clock { get; } = new();
        public Source Source { get; } = new();
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "PortKillMonitoring-" + Guid.NewGuid().ToString("N"));
        public AdminOptions Options { get; }
        public MonitoringService Service { get; }
        public long Now => Clock.GetUtcNow().ToUnixTimeMilliseconds();
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Options = new() { MonitoringSnapshotDirectory = Root };
            Source.Release = new("1.2.3", 1, "manifest", "package", Now + 30 * 86_400_000L);
            Service = new(Options, Source, Clock);
        }
        public MonitoringSnapshot Snapshot() => new(1, Now - 5000, Now,
            new(new(Now, null, 25), new(Now, null, 8_000_000_000, 2_000_000_000),
                new(Now, null, "eth0", 100, 200), [new("管理数据 / 更新包", Now, null, 100_000_000_000, 50_000_000_000)]),
            [new(Now, 25, 25, 100, 200, "eth0")],
            new(Now, null, new[] { "api", "worker", "notifier", "caddy" }.Select(name => new MonitoringContainer(name, "abc", "running", "not_configured", 0)).ToArray()),
            [new("api", Now, null, "healthy", 200, "healthy", "healthy", null),
                new("notifier", Now, null, "healthy", 200, null, null, true), new("download", Now, null, "healthy", 200, null, null, null)]);
        public Task Write(MonitoringSnapshot snapshot) => File.WriteAllTextAsync(Path.Combine(Root, "status.json"), JsonSerializer.Serialize(snapshot, Json));
        public void Dispose() => Directory.Delete(Root, true);
    }

    [Fact]
    public async Task SnapshotReadIsBoundedAndFailuresDoNotBecomeServerErrors()
    {
        using var f = new Fixture();
        f.Options.MonitoringSnapshotDirectory = "";
        Assert.Equal("unconfigured", (await f.Service.GetAsync(default)).Status);
        f.Options.MonitoringSnapshotDirectory = f.Root;
        Assert.Equal("unavailable", (await f.Service.GetAsync(default)).Status);
        foreach (string text in new[] { "{broken", "{}", "null", new string(' ', 524_289) })
        {
            await File.WriteAllTextAsync(Path.Combine(f.Root, "status.json"), text);
            Assert.Equal("invalid_snapshot", (await f.Service.GetAsync(default)).Status);
        }
        var valid = f.Snapshot();
        await f.Write(valid with { SchemaVersion = 2 });
        Assert.Equal("invalid_snapshot", (await f.Service.GetAsync(default)).Status);
        await f.Write(valid with { Resources = valid.Resources with { Cpu = valid.Resources.Cpu with { Percent = 101 } } });
        Assert.Equal("invalid_snapshot", (await f.Service.GetAsync(default)).Status);
        await f.Write(valid with { Resources = valid.Resources with { Memory = valid.Resources.Memory with { ObservedAtUtcMs = f.Now + 30_001 } } });
        Assert.Equal("invalid_snapshot", (await f.Service.GetAsync(default)).Status);
        await f.Write(valid);
        Assert.Equal("ok", (await f.Service.GetAsync(default)).Status);
        await File.WriteAllTextAsync(Path.Combine(f.Root, "status.json"), JsonSerializer.Serialize(valid, Json).Replace("\"percent\":25", "\"percent\":\"25\""));
        Assert.Equal("invalid_snapshot", (await f.Service.GetAsync(default)).Status);
    }

    [Theory]
    [InlineData(19_999, "ok")]
    [InlineData(20_000, "stale")]
    public async Task StoppedCollectorCannotKeepReportingHealthyResources(int elapsed, string state)
    {
        using var f = new Fixture(); await f.Write(f.Snapshot()); f.Clock.Advance(TimeSpan.FromMilliseconds(elapsed));
        var result = await f.Service.GetAsync(default);
        Assert.Equal(state, result.Status);
        if (state == "stale") {
            Assert.Null(result.Snapshot!.Resources.Cpu.Percent);
            // Last Docker observation is still within its independent 45s observation window.
            Assert.Equal(4, result.Snapshot.Docker.Services.Length);
        }
        Assert.Equal(MonitoringPolicy.Freshness, result.Freshness);
    }

    [Fact]
    public async Task FreshCollectorWithFailedSourcesIsDegradedInsteadOfStopped()
    {
        using var f = new Fixture(); var snapshot = f.Snapshot();
        await f.Write(snapshot with {
            Resources = snapshot.Resources with {
                Cpu = snapshot.Resources.Cpu with { Error = "cpu_unavailable" },
                Memory = snapshot.Resources.Memory with { Error = "memory_unavailable" },
                Network = snapshot.Resources.Network with { Error = "network_unavailable" },
                Disks = [] },
            Docker = snapshot.Docker with { Error = "docker_unavailable" }, AppChecks = [] });
        var result = await f.Service.GetAsync(default);
        Assert.Equal("degraded", result.Status);
        Assert.Null(result.Snapshot!.Resources.Cpu.Percent);
    }

    [Theory]
    [InlineData(44_999, true)]
    [InlineData(45_000, false)]
    public async Task DockerHasIndependentExpiryEvenWhenSnapshotKeepsUpdating(int age, bool usable)
    {
        using var f = new Fixture(); var snapshot = f.Snapshot();
        await f.Write(snapshot with { Docker = snapshot.Docker with { ObservedAtUtcMs = f.Now - age } });
        var result = await f.Service.GetAsync(default);
        Assert.Equal(usable ? 4 : 0, result.Snapshot!.Docker.Services.Length);
        Assert.NotNull(result.Snapshot.Resources.Cpu.Percent);
    }

    [Theory]
    [InlineData(89_999, true)]
    [InlineData(90_000, false)]
    public async Task DiskAndApplicationHaveIndependentExpiry(int age, bool usable)
    {
        using var f = new Fixture(); var snapshot = f.Snapshot();
        await f.Write(snapshot with { Resources = snapshot.Resources with { Disks = [snapshot.Resources.Disks[0] with { ObservedAtUtcMs = f.Now - age }] },
            AppChecks = snapshot.AppChecks.Select(p => p with { ObservedAtUtcMs = f.Now - age }).ToArray() });
        var result = await f.Service.GetAsync(default);
        Assert.Equal(usable, result.Snapshot!.Resources.Disks[0].FreeBytes != null);
        Assert.Equal(usable ? "healthy" : "unknown", result.Snapshot.AppChecks[0].Status);
    }

    [Fact]
    public async Task NegativeBusinessObservationIsFreshButDockerFailureIsUnknown()
    {
        using var f = new Fixture(); var snapshot = f.Snapshot();
        var containers = snapshot.Docker.Services.Select(c => c.Service == "worker" ? c with { State = "exited" } : c).ToArray();
        await f.Write(snapshot with { Docker = snapshot.Docker with { Services = containers } });
        var result = await f.Service.GetAsync(default);
        Assert.Equal("ok", result.Status);
        Assert.Contains(result.Alerts, alert => alert.Code == "container_state" && alert.Source == "worker");
        await f.Write(snapshot with { Docker = snapshot.Docker with { Error = "docker_unavailable", Services = containers } });
        result = await f.Service.GetAsync(default);
        Assert.Empty(result.Snapshot!.Docker.Services);
        Assert.DoesNotContain(result.Alerts, alert => alert.Code == "container_state");
    }

    [Theory]
    [InlineData(15, "healthy")]
    [InlineData(14, "warning")]
    [InlineData(7, "critical")]
    [InlineData(0, "expired")]
    public async Task ExpiryUsesTrustedSourceAndInclusiveThresholds(int days, string status)
    {
        using var f = new Fixture(); await f.Write(f.Snapshot());
        f.Source.Release = f.Source.Release! with { ExpiresUtcMs = f.Now + days * 86_400_000L };
        Assert.Equal(status, (await f.Service.GetAsync(default)).Manifest.Status);
    }

    [Fact]
    public async Task MissingManifestDoesNotInventPublishHistoryOrReportAllServicesDown()
    {
        using var f = new Fixture(); var snapshot = f.Snapshot();
        await f.Write(snapshot with { AppChecks = snapshot.AppChecks.Select(p => p.Name == "download" ? p with { Status = "missing", HttpStatus = 404 } : p).ToArray() });
        Assert.Contains((await f.Service.GetAsync(default)).Alerts, a => a.Code == "application_failed" && a.Source == "download");
        f.Source.Release = null;
        var result = await f.Service.GetAsync(default);
        Assert.Equal("unconfirmed", result.Snapshot!.AppChecks.Single(p => p.Name == "download").Status);
        Assert.DoesNotContain(result.Alerts, a => a.Code == "application_failed");
    }

    [Theory]
    [InlineData(10_000_000_000, 1_073_741_824, null)]
    [InlineData(10_000_000_000, 1_073_741_823, "warning")]
    [InlineData(10_000_000_000, 536_870_911, "critical")]
    [InlineData(100_000_000_000, 5_000_000_000, "warning")]
    [InlineData(100_000_000_000, 4_999_999_999, "critical")]
    public async Task DiskThresholdsUseActualAvailableBytesOrPercentage(long total, long free, string? severity)
    {
        using var f = new Fixture(); var snapshot = f.Snapshot();
        await f.Write(snapshot with { Resources = snapshot.Resources with { Disks = [new("业务磁盘", f.Now, null, total, free)] } });
        Assert.Equal(severity, (await f.Service.GetAsync(default)).Alerts.SingleOrDefault(a => a.Code == "disk_space")?.Severity);
    }

    [Fact]
    public async Task EndpointRequiresFullSessionAndReturnsNoStore()
    {
        await using var app = await TestApplication.StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client.GetAsync("/api/v1/system/monitoring")).StatusCode);
        await app.SeedAdminAsync(); await MfaTests.LoginAsync(app.Client);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Client.GetAsync("/api/v1/system/monitoring")).StatusCode);
        // Complete enrollment using the existing helper on a separate fresh fixture.
        await using var full = await TestApplication.StartAsync(); await MfaTests.EnrollAsync(full);
        using var response = await full.Client.GetAsync("/api/v1/system/monitoring");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("unconfigured", (await response.Content.ReadFromJsonAsync<MonitoringResponse>())!.Status);
        using var scope = full.App.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AdminUser>>();
        var user = (await users.FindByNameAsync("operator"))!;
        Assert.True((await users.RemoveFromRoleAsync(user, "Administrator")).Succeeded);
        Assert.Equal(HttpStatusCode.Forbidden, (await full.Client.GetAsync("/api/v1/system/monitoring")).StatusCode);
    }
}
