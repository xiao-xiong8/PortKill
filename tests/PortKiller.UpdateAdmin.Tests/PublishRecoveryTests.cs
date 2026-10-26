using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortKiller.UpdateManagement.Contracts;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Persistence.Entities;
using PortKiller.UpdateManagement.Security;
using PortKiller.Updates;

namespace PortKiller.UpdateAdmin.Tests;

public sealed class PublishRecoveryTests
{
    [Theory]
    [InlineData("staged", false)]
    [InlineData("package-published", false)]
    [InlineData("manifest-activated", true)]
    [InlineData("receipt-written", true)]
    public async Task KilledToolAtEachCommitBoundaryRecoversAfterApiRestart(string phase, bool committed)
    {
        await using var fixture = await PublishFixture.StartAsync();
        byte[] manifest = fixture.Sign(1);
        var upload = await fixture.UploadAsync(manifest);
        string jobId = await fixture.ConfirmAsync(upload.DraftId);
        PublishTaskContext task = await BeginInterruptedAttemptAsync(fixture);
        string signal = await ConfigureFaultAsync(fixture, phase);
        await using (var child = StartActivation(fixture, task, FindTestHost()))
        {
            await WaitForSignalAsync(signal, child);
            Assert.Equal(committed, File.Exists(CurrentPath(fixture)));
            child.Process.Kill(entireProcessTree: true);
            await child.ExitAsync();
        }

        fixture.App.Clock.Advance(TimeSpan.FromSeconds(65));
        await fixture.App.RestartAsync();
        Assert.True(await fixture.ExecuteAsync());
        ReleaseJob job = await JobAsync(fixture, jobId);
        Assert.Equal(PublishJobState.ManifestActivated, job.State);
        Assert.Equal(committed ? 1 : 2, job.Attempts);
        Assert.Equal(manifest, await File.ReadAllBytesAsync(CurrentPath(fixture)));
        string receipt = Path.Combine(StatePath(fixture), "receipt.json");
        Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(receipt))), job.ReceiptSha256);
        Assert.False(File.Exists(Path.Combine(StatePath(fixture), "transaction.json")));
        Assert.False(await fixture.ExecuteAsync());
    }

    [Fact]
    public async Task ExhaustedAttemptWithCommittedManifestIsRecoveredBeforeCheckingLocalArtifacts()
    {
        await using var fixture = await PublishFixture.StartAsync();
        byte[] manifest = fixture.Sign(1);
        var upload = await fixture.UploadAsync(manifest);
        string jobId = await fixture.ConfirmAsync(upload.DraftId);
        var task = await BeginInterruptedAttemptAsync(fixture);
        Assert.True((await fixture.ActivateOutsideApiAsync(manifest, "none")).Succeeded);
        await File.WriteAllBytesAsync(Path.Combine(fixture.App.Root, "archive", task.ArchiveId, "manifest.envelope"), [9]);
        await ExpireAsync(fixture, jobId, PublishTaskQueue.MaximumAttempts);

        Assert.True(await fixture.ExecuteAsync());
        var job = await JobAsync(fixture, jobId);
        Assert.Equal(PublishJobState.ManifestActivated, job.State);
        Assert.Equal(PublishTaskQueue.MaximumAttempts, job.Attempts);
        Assert.Equal(manifest, await File.ReadAllBytesAsync(CurrentPath(fixture)));
    }

    [Fact]
    public async Task UnavailableCommitSourceDefersRecoveryEvenWhenAttemptsAreExhausted()
    {
        await using var fixture = await PublishFixture.StartAsync();
        var upload = await fixture.UploadAsync(fixture.Sign(1));
        string jobId = await fixture.ConfirmAsync(upload.DraftId);
        await ExpireAsync(fixture, jobId, PublishTaskQueue.MaximumAttempts);
        Directory.CreateDirectory(StatePath(fixture));
        using (var toolLock = new FileStream(Path.Combine(StatePath(fixture), "lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            for (int check = 0; check < 2; check++)
            {
                Assert.True(await fixture.ExecuteAsync());
                var pending = await JobAsync(fixture, jobId);
                Assert.Equal(PublishJobState.Publishing, pending.State);
                Assert.Equal(PublishTaskQueue.MaximumAttempts, pending.Attempts);
                Assert.Equal("io_failure", pending.ErrorCode);
                fixture.App.Clock.Advance(TimeSpan.FromSeconds(6));
            }
        }
        Assert.True(await fixture.ExecuteAsync());
        var failed = await JobAsync(fixture, jobId);
        Assert.Equal(PublishJobState.Failed, failed.State);
        Assert.Equal("publish_retry_exhausted", failed.ErrorCode);
        Assert.False(File.Exists(CurrentPath(fixture)));
    }

    [Fact]
    public async Task InterruptedOldTaskBecomesSupersededWithoutRollingBackANewerManifest()
    {
        await using var fixture = await PublishFixture.StartAsync();
        byte[] old = fixture.Sign(1);
        var upload = await fixture.UploadAsync(old);
        string jobId = await fixture.ConfirmAsync(upload.DraftId);
        await BeginInterruptedAttemptAsync(fixture);
        Assert.True((await fixture.ActivateOutsideApiAsync(old, "none")).Succeeded);
        byte[] newer = fixture.Sign(2);
        Assert.True((await fixture.ActivateOutsideApiAsync(newer, Convert.ToHexString(SHA256.HashData(old)))).Succeeded);
        await ExpireAsync(fixture, jobId, PublishTaskQueue.MaximumAttempts);

        Assert.True(await fixture.ExecuteAsync());
        Assert.Equal(PublishJobState.Superseded, (await JobAsync(fixture, jobId)).State);
        Assert.Equal(newer, await File.ReadAllBytesAsync(CurrentPath(fixture)));
        Assert.False(await fixture.ExecuteAsync());
    }

    [Fact]
    public async Task DifferentManifestAtTheSameSequenceIsAConflict()
    {
        await using var fixture = await PublishFixture.StartAsync();
        var upload = await fixture.UploadAsync(fixture.Sign(1));
        string jobId = await fixture.ConfirmAsync(upload.DraftId);
        byte[] competing = fixture.Sign(1);
        Assert.True((await fixture.ActivateOutsideApiAsync(competing, "none")).Succeeded);

        Assert.True(await fixture.ExecuteAsync());
        var job = await JobAsync(fixture, jobId);
        Assert.Equal(PublishJobState.Conflict, job.State);
        Assert.Equal("publish_baseline_conflict", job.ErrorCode);
        Assert.Equal(0, job.Attempts);
        Assert.Equal(competing, await File.ReadAllBytesAsync(CurrentPath(fixture)));
    }

    [Fact]
    public async Task TrustedLegacySchemaOneManifestCanBeTheBaselineForANewPublication()
    {
        await using var fixture = await PublishFixture.StartAsync();
        byte[] legacy = fixture.Sign(0, schema: 1);
        await File.WriteAllBytesAsync(CurrentPath(fixture), legacy);
        await File.WriteAllBytesAsync(Path.Combine(fixture.Options.ReleaseRoot, $"PortKiller-{fixture.Version}-win-x64.zip"), fixture.Package);
        byte[] manifest = fixture.Sign(1);
        var upload = await fixture.UploadAsync(manifest);
        string jobId = await fixture.ConfirmAsync(upload.DraftId);

        Assert.True(await fixture.ExecuteAsync());
        Assert.Equal(PublishJobState.ManifestActivated, (await JobAsync(fixture, jobId)).State);
        Assert.Equal(manifest, await File.ReadAllBytesAsync(CurrentPath(fixture)));
    }

    [Fact]
    public async Task DatabaseWriteFailureAfterActivationIsRepairedOnTheNextRecovery()
    {
        await using var fixture = await PublishFixture.StartAsync();
        byte[] manifest = fixture.Sign(1);
        var upload = await fixture.UploadAsync(manifest);
        string jobId = await fixture.ConfirmAsync(upload.DraftId);
        await using (var scope = fixture.App.App.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>().Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER reject_activation BEFORE UPDATE OF State ON ReleaseJobs WHEN NEW.State = 'ManifestActivated' BEGIN SELECT RAISE(ABORT, 'injected database failure'); END;");

        Assert.True(await fixture.ExecuteAsync());
        Assert.Equal(PublishJobState.Publishing, (await JobAsync(fixture, jobId)).State);
        Assert.Equal(manifest, await File.ReadAllBytesAsync(CurrentPath(fixture)));
        await using (var scope = fixture.App.App.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>().Database.ExecuteSqlRawAsync("DROP TRIGGER reject_activation;");
        fixture.App.Clock.Advance(TimeSpan.FromSeconds(6));
        await fixture.App.RestartAsync();

        Assert.True(await fixture.ExecuteAsync());
        var job = await JobAsync(fixture, jobId);
        Assert.Equal(PublishJobState.ManifestActivated, job.State);
        Assert.Equal(1, job.Attempts);
        Assert.Null(job.ErrorCode);
        Assert.Equal(manifest, await File.ReadAllBytesAsync(CurrentPath(fixture)));
    }

    [Theory]
    [InlineData("package-published", false)]
    [InlineData("manifest-activated", true)]
    public async Task KillingOnlyTheWorkerStopsItsOrphanAndThenRecoversTheActualCommit(string phase, bool committed)
    {
        await using var fixture = await PublishFixture.StartAsync();
        byte[] manifest = fixture.Sign(1);
        var upload = await fixture.UploadAsync(manifest);
        string jobId = await fixture.ConfirmAsync(upload.DraftId);
        string signal = await ConfigureFaultAsync(fixture, phase);
        fixture.Options.ExecutablePath = FindTestHost();
        string config = Path.Combine(fixture.App.Root, "test-worker.json");
        await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new { DataRoot = fixture.App.Root, Tool = fixture.Options }));
        await using var worker = Child.Start(FindTestHost(), ["publish-worker", config]);
        int pid = await WaitForSignalAsync(signal, worker);
        using var tool = Process.GetProcessById(pid);
        try
        {
            fixture.Options.ExecutablePath = PublishFixture.FindTool();
            fixture.App.Clock.Advance(TimeSpan.FromSeconds(65));
            Assert.False(await fixture.ExecuteAsync()); // A live worker still owns the execution lock despite the expired DB lease.
            Assert.Equal(1, (await JobAsync(fixture, jobId)).Attempts);
            Assert.False(tool.HasExited);
            worker.Process.Kill(); // Deliberately leave the child alive; its production watchdog must stop it.
            await worker.ExitAsync();
            await tool.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal(committed, File.Exists(CurrentPath(fixture)));

            await fixture.App.RestartAsync();
            Assert.True(await fixture.ExecuteAsync());
            var job = await JobAsync(fixture, jobId);
            Assert.Equal(PublishJobState.ManifestActivated, job.State);
            Assert.Equal(committed ? 1 : 2, job.Attempts);
            Assert.Equal(manifest, await File.ReadAllBytesAsync(CurrentPath(fixture)));
        }
        finally { if (!tool.HasExited) { tool.Kill(entireProcessTree: true); await tool.WaitForExitAsync(); } }
    }

    [Theory]
    [InlineData("package-published", PublishJobState.Queued)]
    [InlineData("manifest-activated", PublishJobState.ManifestActivated)]
    public async Task ToolTimeoutUsesTheCommitRecordInsteadOfAssumingFailure(string phase, PublishJobState expectedState)
    {
        await using var fixture = await PublishFixture.StartAsync();
        var upload = await fixture.UploadAsync(fixture.Sign(1));
        string jobId = await fixture.ConfirmAsync(upload.DraftId);
        string signal = await ConfigureFaultAsync(fixture, phase);
        fixture.Options.ExecutablePath = FindTestHost();
        fixture.Options.TimeoutSeconds = 2;

        Assert.True(await fixture.ExecuteAsync());
        var job = await JobAsync(fixture, jobId);
        Assert.Equal(expectedState, job.State);
        Assert.Equal(1, job.Attempts);
        int pid = JsonNode.Parse(await File.ReadAllTextAsync(signal))!["pid"]!.GetValue<int>();
        AssertProcessExited(pid);
    }

    [Fact]
    public async Task LeaseLossCancelsTheActiveToolAndLeavesTheTaskForRecovery()
    {
        await using var fixture = await PublishFixture.StartAsync();
        var upload = await fixture.UploadAsync(fixture.Sign(1));
        string jobId = await fixture.ConfirmAsync(upload.DraftId);
        string signal = await ConfigureFaultAsync(fixture, "manifest-activated");
        fixture.Options.ExecutablePath = FindTestHost();
        using var cancellation = new CancellationTokenSource();
        Task<bool> executing = fixture.ExecuteAsync(cancellation.Token);
        try
        {
            int pid = await WaitForSignalAsync(signal);
            await using (var scope = fixture.App.App.Services.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>().ReleaseJobs.Where(job => job.Id == jobId)
                    .ExecuteUpdateAsync(update => update.SetProperty(job => job.LeaseOwner, "replacement-worker"));

            Assert.True(await executing.WaitAsync(TimeSpan.FromSeconds(25)));
            AssertProcessExited(pid);
        }
        finally
        {
            cancellation.Cancel();
            try { await executing; } catch (OperationCanceledException) { }
        }
        var interrupted = await JobAsync(fixture, jobId);
        Assert.Equal(PublishJobState.Publishing, interrupted.State);
        Assert.Equal("replacement-worker", interrupted.LeaseOwner);
        fixture.Options.ExecutablePath = PublishFixture.FindTool();
        await ExpireAsync(fixture, jobId, interrupted.Attempts);
        Assert.True(await fixture.ExecuteAsync());
        Assert.Equal(PublishJobState.ManifestActivated, (await JobAsync(fixture, jobId)).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionToolRejectsDeadOrReusedOwnerIdentityBeforeActivation(bool deadOwner)
    {
        await using var fixture = await PublishFixture.StartAsync();
        var upload = await fixture.UploadAsync(fixture.Sign(1));
        await fixture.ConfirmAsync(upload.DraftId);
        var task = await BeginInterruptedAttemptAsync(fixture);
        await using var owner = Child.Start(FindTestHost(), ["--portkill-test-child-hang"]);
        long ticks = PublisherProcessIdentity.ReadStartToken(owner.Process);
        if (deadOwner) { owner.Process.Kill(); await owner.ExitAsync(); }
        await using var child = StartActivation(fixture, task, PublishFixture.FindTool(), owner.Process.Id, deadOwner ? ticks : ticks + 1);

        string output = await child.ExitAsync();
        Assert.NotEqual(0, child.Process.ExitCode);
        Assert.Contains("operation_conflict", output);
        Assert.False(File.Exists(CurrentPath(fixture)));
    }

    private static async Task<PublishTaskContext> BeginInterruptedAttemptAsync(PublishFixture fixture)
    {
        await using var scope = fixture.App.App.Services.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<PublishTaskQueue>();
        var task = await queue.ClaimAsync("crashed-worker", default);
        Assert.NotNull(task);
        Assert.True(await queue.BeginActivationAsync(task.Id, "crashed-worker", default));
        return task;
    }

    private static async Task ExpireAsync(PublishFixture fixture, string id, int attempts)
    {
        await using var scope = fixture.App.App.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
        await database.ReleaseJobs.Where(job => job.Id == id).ExecuteUpdateAsync(update => update
            .SetProperty(job => job.State, PublishJobState.Publishing).SetProperty(job => job.Attempts, attempts)
            .SetProperty(job => job.LeaseOwner, "crashed-worker")
            .SetProperty(job => job.LeaseExpiresUtcMs, fixture.App.Clock.GetUtcNow().ToUnixTimeMilliseconds() - 1));
    }

    private static async Task<ReleaseJob> JobAsync(PublishFixture fixture, string id)
    {
        await using var scope = fixture.App.App.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>().ReleaseJobs.AsNoTracking().SingleAsync(job => job.Id == id);
    }

    private static async Task<string> ConfigureFaultAsync(PublishFixture fixture, string phase)
    {
        string signal = Path.Combine(fixture.App.Root, "fault-signal.json");
        var config = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Options.VerificationConfigPath))!.AsObject();
        config["testMode"] = "publish-fault"; config["faultPhase"] = phase; config["signalPath"] = signal;
        await File.WriteAllTextAsync(fixture.Options.VerificationConfigPath, config.ToJsonString());
        return signal;
    }

    private static Child StartActivation(PublishFixture fixture, PublishTaskContext task, string executable,
        int? ownerPid = null, long? ownerStart = null)
    {
        using var current = Process.GetCurrentProcess();
        return Child.Start(executable, ["activate", "--config", fixture.Options.VerificationConfigPath,
            "--root", fixture.Options.ReleaseRoot,
            "--manifest", Path.Combine(fixture.App.Root, "archive", task.ArchiveId, "manifest.envelope"),
            "--package", Path.Combine(fixture.App.Root, "archive", task.ArchiveId, "package.zip"),
            "--expected", task.ExpectedManifestSha256, "--manifest-sha256", task.ManifestSha256, "--package-sha256", task.PackageSha256,
            "--owner-pid", (ownerPid ?? current.Id).ToString(CultureInfo.InvariantCulture),
            "--owner-start-ticks", (ownerStart ?? PublisherProcessIdentity.ReadStartToken(current)).ToString(CultureInfo.InvariantCulture),
            "--max-runtime-seconds", "55", "--json-errors"]);
    }

    private static async Task<int> WaitForSignalAsync(string signal, Child? child = null)
    {
        var watch = Stopwatch.StartNew();
        while (!File.Exists(signal))
        {
            if (child?.Process.HasExited == true) throw new InvalidOperationException("Child exited before its fault point: " + await child.ExitAsync());
            if (watch.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Tool did not reach its fault point.");
            await Task.Delay(20);
        }
        return JsonNode.Parse(await File.ReadAllTextAsync(signal))!["pid"]!.GetValue<int>();
    }

    private static void AssertProcessExited(int pid)
    {
        try { using var process = Process.GetProcessById(pid); Assert.True(process.HasExited, "Publishing tool was left running."); }
        catch (ArgumentException) { }
    }

    private static string CurrentPath(PublishFixture fixture) => Path.Combine(fixture.Options.ReleaseRoot, "manifest.json");
    private static string StatePath(PublishFixture fixture) => Path.Combine(fixture.App.Root, ".public-update-state");
    private static string FindTestHost() => Path.Combine(Path.GetDirectoryName(PublishFixture.FindTool())!, "..", "..", "..", "..", "..", "..",
        "tests", "PortKiller.UpdateTool.TestHost", "bin", "Release", "net10.0", "PortKiller.UpdateTool.TestHost" + (OperatingSystem.IsWindows() ? ".exe" : ""));

    private sealed class Child : IAsyncDisposable
    {
        public Process Process { get; }
        private readonly Task<string> _output;
        private readonly Task<string> _error;
        private Child(Process process) { Process = process; _output = process.StandardOutput.ReadToEndAsync(); _error = process.StandardError.ReadToEndAsync(); }
        public static Child Start(string executable, IEnumerable<string> args)
        {
            var start = new ProcessStartInfo(Path.GetFullPath(executable))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string value in args) start.ArgumentList.Add(value);
            return new(Process.Start(start)!);
        }
        public async Task<string> ExitAsync()
        {
            await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            return await _output + await _error;
        }
        public async ValueTask DisposeAsync()
        {
            if (!Process.HasExited) Process.Kill(entireProcessTree: true);
            await ExitAsync();
            Process.Dispose();
        }
    }
}
