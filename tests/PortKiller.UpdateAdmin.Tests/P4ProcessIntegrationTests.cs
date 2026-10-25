using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortKiller.UpdateManagement.Contracts;
using PortKiller.UpdateManagement.Persistence;
using Xunit.Abstractions;

namespace PortKiller.UpdateAdmin.Tests;

public sealed class P4ProcessIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task TwoProductionWorkersCompleteAuthenticatedHttpsUploadPublicationAndReplayExactlyOnce()
    {
        await using var distribution = await PublicDistributionFixture.StartAsync(realHttp: true);
        var fixture = distribution.Publication;
        await using var first = P4ChildProcess.Worker(fixture);
        await using var second = P4ChildProcess.Worker(fixture);
        byte[] manifest = fixture.Sign(801);
        var upload = await fixture.UploadAsync(manifest, executeValidation: false);
        await WaitAsync(async () => (await SnapshotAsync(fixture, upload.JobId)).GetProperty("validation")
            .GetProperty("validationState").GetString() == "readyForApproval", first, second);

        using var challenge = await fixture.App.Client.PostAsJsonAsync($"/api/v1/releases/{upload.DraftId}/publish/challenge", new { });
        challenge.EnsureSuccessStatusCode();
        string challengeId = (await challenge.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        string key = Guid.NewGuid().ToString("N");
        async Task<string> ConfirmAsync()
        {
            using var response = await fixture.App.Client.PostAsJsonAsync($"/api/v1/releases/{upload.DraftId}/publish",
                new { ChallengeId = challengeId, IdempotencyKey = key });
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        }
        string[] confirmed = await Task.WhenAll(ConfirmAsync(), ConfirmAsync());
        Assert.Equal(confirmed[0], confirmed[1]); string id = confirmed[0];
        await using (var stream = await EventConnection.OpenAsync(fixture.App, id))
        {
            var snapshot = await stream.NextAsync(); Assert.Equal("snapshot", snapshot.Kind);
            while (snapshot.Kind != "snapshot" || snapshot.Data.GetProperty("publication").GetProperty("state").GetString() != "completed")
                snapshot = await stream.NextAsync();
        }
        Assert.Equal(manifest, await File.ReadAllBytesAsync(CurrentPath(fixture)));
        await using (var scope = fixture.App.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
            var job = await db.ReleaseJobs.SingleAsync(item => item.Id == id);
            Assert.Equal(1, job.Attempts); Assert.Equal(1, job.PublicCheckAttempts);
            Assert.Equal(1, await db.ReleaseJobs.CountAsync(item => item.Kind == "publish"));
            Assert.Equal(1, await db.JobEvents.CountAsync(item => item.JobId == id && item.Stage == "claimed"));
            Assert.Equal(new[] { "publish.manifest_activated", "publish.completed" },
                await db.AuditEvents.Where(item => item.ResourceId == id && item.CorrelationId == "job:" + id).OrderBy(item => item.Id).Select(item => item.Action).ToArrayAsync());
        }
        await JobEventStreamTests.ReleasedAsync(fixture.App);
        await fixture.App.RestartAsync();
        var replay = await JobEventTests.ReadAsync(fixture.App, id, id + ":0");
        Assert.Equal(new[] { "queued", "claimed", "activating", "manifest_activated", "public_verifying", "completed" },
            replay.GetProperty("events").EnumerateArray().Select(item => item.GetProperty("stage").GetString()));
        await RecordAsync(fixture, id);
    }

    [Fact]
    public async Task RestartedProductionWorkerRetriesOnlyTheFailedDownloadOnTheOriginalJob()
    {
        await using var distribution = await PublicDistributionFixture.StartAsync(realHttp: true);
        var fixture = distribution.Publication; distribution.Mode = "missing";
        string id;
        await using (var worker = P4ChildProcess.Worker(fixture))
        {
            var upload = await fixture.UploadAsync(fixture.Sign(802), executeValidation: false);
            await WaitAsync(async () => (await SnapshotAsync(fixture, upload.JobId)).GetProperty("validation")
                .GetProperty("validationState").GetString() == "readyForApproval", worker);
            id = await fixture.ConfirmAsync(upload.DraftId);
            await WaitAsync(async () => await StateAsync(fixture, id) == "activeUnverified", worker);
        }
        byte[] before = await File.ReadAllBytesAsync(CurrentPath(fixture));
        DateTime modified = File.GetLastWriteTimeUtc(CurrentPath(fixture));
        string receiptPath = Path.Combine(fixture.App.Root, ".public-update-state", "receipt.json");
        byte[] receipt = await File.ReadAllBytesAsync(receiptPath);
        await fixture.App.RestartAsync(); distribution.Mode = "healthy";
        using (var retry = await fixture.App.Client.PostAsJsonAsync($"/api/v1/publish-jobs/{id}/public-check/retry", new { }))
        { retry.EnsureSuccessStatusCode(); Assert.Equal(id, (await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()); }
        await using var restarted = P4ChildProcess.Worker(fixture);
        await WaitAsync(async () => await StateAsync(fixture, id) == "completed", restarted);
        Assert.Equal(before, await File.ReadAllBytesAsync(CurrentPath(fixture)));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(CurrentPath(fixture)));
        Assert.Equal(receipt, await File.ReadAllBytesAsync(receiptPath));
        await using var scope = fixture.App.App.Services.CreateAsyncScope();
        var job = await scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>().ReleaseJobs.SingleAsync(item => item.Id == id);
        Assert.Equal(1, job.Attempts); Assert.Equal(2, job.PublicCheckAttempts);
        await RecordAsync(fixture, id);
    }

    [Theory]
    [InlineData("package-published", false)]
    [InlineData("manifest-activated", true)]
    public async Task KillingProductionWorkerStopsItsOrphanAndRestoresTheActualCommitAfterRestart(string phase, bool committed)
    {
        await using var distribution = await PublicDistributionFixture.StartAsync(realHttp: true);
        var fixture = distribution.Publication; byte[] manifest = fixture.Sign(803);
        var upload = await fixture.UploadAsync(manifest); string id = await fixture.ConfirmAsync(upload.DraftId);
        string signal = await FaultAsync(fixture, phase);
        fixture.Options.ExecutablePath = TestTool;
        await using (var worker = P4ChildProcess.Worker(fixture))
        {
            await WaitAsync(() => Task.FromResult(File.Exists(signal)), worker);
            int pid = JsonNode.Parse(await File.ReadAllTextAsync(signal))!["pid"]!.GetValue<int>();
            using var tool = Process.GetProcessById(pid);
            try
            {
                Assert.Equal(committed, File.Exists(CurrentPath(fixture)));
                await worker.KillAsync(entireTree: false);
                await tool.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            }
            finally { if (!tool.HasExited) { tool.Kill(true); await tool.WaitForExitAsync(); } }
        }
        fixture.Options.ExecutablePath = PublishFixture.FindTool();
        await ExpireLeaseAsync(fixture, id); await fixture.App.RestartAsync();
        await using var restarted = P4ChildProcess.Worker(fixture);
        await WaitAsync(async () => await StateAsync(fixture, id) == "completed", restarted);
        Assert.Equal(manifest, await File.ReadAllBytesAsync(CurrentPath(fixture)));
        await using var scope = fixture.App.App.Services.CreateAsyncScope();
        var job = await scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>().ReleaseJobs.SingleAsync(item => item.Id == id);
        Assert.Equal(committed ? 1 : 2, job.Attempts);
        await RecordAsync(fixture, id);
    }

    [Fact]
    public async Task OriginalCliProcessLockDefersTheWorkerAndItsNewerCommitSupersedesTheOldJob()
    {
        await using var distribution = await PublicDistributionFixture.StartAsync(realHttp: true);
        var fixture = distribution.Publication;
        var upload = await fixture.UploadAsync(fixture.Sign(804)); string id = await fixture.ConfirmAsync(upload.DraftId);
        byte[] newer = fixture.Sign(805); string signal = await FaultAsync(fixture, "package-published", releaseGate: true);
        string manifestPath = Path.Combine(fixture.App.Root, "legacy.json"), packagePath = Path.Combine(fixture.App.Root, "legacy.zip");
        await File.WriteAllBytesAsync(manifestPath, newer); await File.WriteAllBytesAsync(packagePath, fixture.Package);
        // Deliberately omit parent identity flags, as the original CLI/SSH publisher does.
        await using var cli = P4ChildProcess.Start(TestTool, ["activate", "--root", fixture.Options.ReleaseRoot,
            "--config", fixture.Options.VerificationConfigPath, "--manifest", manifestPath, "--package", packagePath,
            "--expected", "none", "--manifest-sha256", Digest(newer), "--package-sha256", Digest(fixture.Package), "--json-errors"]);
        await WaitAsync(() => Task.FromResult(File.Exists(signal)));
        await using var worker = P4ChildProcess.Worker(fixture);
        await WaitAsync(async () =>
        {
            var row = (await SnapshotAsync(fixture, id)).GetProperty("publication");
            return row.GetProperty("state").GetString() == "publishing" && row.GetProperty("errorCode").GetString() == "io_failure";
        }, worker);
        Assert.False(File.Exists(CurrentPath(fixture)));
        await File.WriteAllTextAsync(signal + ".continue", "continue"); await cli.ExitAsync(); Assert.Equal(0, cli.Process.ExitCode);
        await WaitAsync(async () => await StateAsync(fixture, id) == "superseded", worker);
        Assert.Equal(newer, await File.ReadAllBytesAsync(CurrentPath(fixture)));
        await using var scope = fixture.App.App.Services.CreateAsyncScope();
        Assert.Equal(0, (await scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>().ReleaseJobs.SingleAsync(item => item.Id == id)).Attempts);
        await RecordAsync(fixture, id);
    }

    [Fact]
    public async Task DatabaseFailureAfterCommitIsRecoveredByARestartedProductionWorkerWithoutReactivation()
    {
        await using var distribution = await PublicDistributionFixture.StartAsync(realHttp: true);
        var fixture = distribution.Publication; byte[] manifest = fixture.Sign(806);
        var upload = await fixture.UploadAsync(manifest); string id = await fixture.ConfirmAsync(upload.DraftId);
        await SqlAsync(fixture, "CREATE TRIGGER reject_p4_commit BEFORE UPDATE OF State ON ReleaseJobs WHEN NEW.State = 'ManifestActivated' BEGIN SELECT RAISE(ABORT, 'injected storage failure'); END;");
        await using (var worker = P4ChildProcess.Worker(fixture))
        {
            await WaitAsync(async () =>
            {
                var row = (await SnapshotAsync(fixture, id)).GetProperty("publication");
                return File.Exists(CurrentPath(fixture)) && row.GetProperty("state").GetString() == "publishing"
                    && row.GetProperty("errorCode").GetString() == "publish_recovery_pending";
            }, worker);
        }
        DateTime modified = File.GetLastWriteTimeUtc(CurrentPath(fixture));
        Assert.Equal(manifest, await File.ReadAllBytesAsync(CurrentPath(fixture)));
        Assert.Equal(0, distribution.ManifestHits);
        await SqlAsync(fixture, "DROP TRIGGER reject_p4_commit;"); await ExpireLeaseAsync(fixture, id);
        await fixture.App.RestartAsync();
        await using var restarted = P4ChildProcess.Worker(fixture);
        await WaitAsync(async () => await StateAsync(fixture, id) == "completed", restarted);
        Assert.Equal(modified, File.GetLastWriteTimeUtc(CurrentPath(fixture)));
        await using var scope = fixture.App.App.Services.CreateAsyncScope();
        Assert.Equal(1, (await scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>().ReleaseJobs.SingleAsync(item => item.Id == id)).Attempts);
        await RecordAsync(fixture, id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedActivationOutputUsesTheCommitFactAndPersistsOnlySafeCodes(bool committed)
    {
        await using var distribution = await PublicDistributionFixture.StartAsync(realHttp: true);
        var fixture = distribution.Publication;
        var upload = await fixture.UploadAsync(fixture.Sign(807)); string id = await fixture.ConfirmAsync(upload.DraftId);
        var config = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Options.VerificationConfigPath))!.AsObject();
        config["testMode"] = "publish-output"; config["commitBeforeOutput"] = committed;
        config["diagnosticPath"] = Path.Combine(fixture.App.Root, "tool-diagnostic.txt");
        await File.WriteAllTextAsync(fixture.Options.VerificationConfigPath, config.ToJsonString());
        fixture.Options.ExecutablePath = TestTool; fixture.Options.MaximumOutputBytes = 1024;
        await using var worker = P4ChildProcess.Worker(fixture);
        try
        {
            await WaitAsync(async () =>
            {
                string state = await StateAsync(fixture, id);
                if (committed && state == "activeUnverified") throw new InvalidOperationException("Injected tool's download check failed.");
                return state == (committed ? "completed" : "failed");
            }, worker);
        }
        catch
        {
            await worker.KillAsync(true); output.WriteLine(await worker.ExitAsync());
            string diagnostic = Path.Combine(fixture.App.Root, "tool-diagnostic.txt");
            if (File.Exists(diagnostic)) output.WriteLine(await File.ReadAllTextAsync(diagnostic));
            await RecordAsync(fixture, id); throw;
        }
        Assert.Equal(committed, File.Exists(CurrentPath(fixture)));
        var snapshot = (await SnapshotAsync(fixture, id)).GetProperty("publication");
        if (!committed) Assert.Equal("tool_output_limit", snapshot.GetProperty("errorCode").GetString());
        Assert.True(snapshot.GetRawText().Length < 4096);
        await RecordAsync(fixture, id);
    }

    private async Task RecordAsync(PublishFixture fixture, string id)
    {
        await using var scope = fixture.App.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
        var job = await db.ReleaseJobs.AsNoTracking().SingleAsync(item => item.Id == id);
        output.WriteLine(JsonSerializer.Serialize(new { job.Id, job.State, job.Attempts, job.PublicCheckAttempts,
            job.ReceiptSha256, job.LastEventSequence, job.ExpectedManifestSha256,
            CurrentManifestSha256 = File.Exists(CurrentPath(fixture)) ? Digest(await File.ReadAllBytesAsync(CurrentPath(fixture))) : "none",
            Events = await db.JobEvents.Where(item => item.JobId == id).OrderBy(item => item.Sequence).Select(item => new { item.Sequence, item.Stage, item.Message }).ToArrayAsync(),
            Audit = await db.AuditEvents.Where(item => item.ResourceId == id).OrderBy(item => item.Id).Select(item => new { item.Action, item.Result, item.CorrelationId }).ToArrayAsync() }));
    }

    private static async Task WaitAsync(Func<Task<bool>> ready, params P4ChildProcess[] workers)
    {
        var watch = Stopwatch.StartNew();
        while (!await ready())
        {
            foreach (var worker in workers) await worker.EnsureRunningAsync();
            if (watch.Elapsed > TimeSpan.FromSeconds(35)) throw new TimeoutException("Production Worker did not reach the expected durable state.");
            await Task.Delay(250);
        }
    }
    private static Task<JsonElement> SnapshotAsync(PublishFixture fixture, string id)
        => fixture.App.Client.GetFromJsonAsync<JsonElement>($"/api/v1/jobs/{id}");
    private static async Task<string> StateAsync(PublishFixture fixture, string id)
        => (await SnapshotAsync(fixture, id)).GetProperty("publication").GetProperty("state").GetString()!;
    private static async Task SqlAsync(PublishFixture fixture, string sql)
    {
        await using var scope = fixture.App.App.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>().Database.ExecuteSqlRawAsync(sql);
    }
    private static async Task ExpireLeaseAsync(PublishFixture fixture, string id)
    {
        await using var scope = fixture.App.App.Services.CreateAsyncScope();
        // Simulate the elapsed lease after a crash without waiting a wall-clock minute in the test.
        long expired = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1;
        await scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>().ReleaseJobs.Where(item => item.Id == id)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.LeaseExpiresUtcMs, expired));
    }
    private static async Task<string> FaultAsync(PublishFixture fixture, string phase, bool releaseGate = false)
    {
        string signal = Path.Combine(fixture.App.Root, "process-signal.json");
        var config = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Options.VerificationConfigPath))!.AsObject();
        config["testMode"] = "publish-fault"; config["faultPhase"] = phase; config["signalPath"] = signal;
        if (releaseGate) config["continuePath"] = signal + ".continue";
        await File.WriteAllTextAsync(fixture.Options.VerificationConfigPath, config.ToJsonString()); return signal;
    }
    private static string CurrentPath(PublishFixture fixture) => Path.Combine(fixture.Options.ReleaseRoot, "manifest.json");
    private static string Digest(byte[] value) => Convert.ToHexString(SHA256.HashData(value));
    private static string TestTool => PublishFixture.FindExecutable("tests/PortKiller.UpdateTool.TestHost", "PortKiller.UpdateTool.TestHost");
}
