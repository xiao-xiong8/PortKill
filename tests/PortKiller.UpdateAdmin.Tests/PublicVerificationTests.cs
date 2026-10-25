using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortKiller.UpdateManagement.Contracts;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Persistence.Entities;
using PortKiller.UpdateManagement.Security;

namespace PortKiller.UpdateAdmin.Tests;

public sealed class PublicVerificationTests
{
    [Fact]
    public async Task RealPublicationCompletesAfterFullDownloadAndRangeAndExposesSeparateObservations()
    {
        await using var distribution = await PublicDistributionFixture.StartAsync();
        PublishFixture fixture = distribution.Publication;
        string id = await distribution.PublishAsync();
        var before = await CurrentAsync(fixture);
        Assert.Equal("healthy", before.GetProperty("expiryStatus").GetString());
        Assert.Equal(id, before.GetProperty("activationReceipt").GetProperty("jobId").GetString());
        Assert.True(before.GetProperty("downloadCheck").GetProperty("pending").GetBoolean());
        Assert.Equal(JsonValueKind.Null, before.GetProperty("downloadCheck").GetProperty("lastSucceeded").ValueKind);
        Assert.Equal(0, distribution.ManifestHits);

        Assert.True(await fixture.ExecutePublicCheckAsync());
        Assert.False(await fixture.ExecutePublicCheckAsync());
        var job = await JobAsync(fixture, id);
        Assert.Equal("completed", job.GetProperty("state").GetString());
        var current = await CurrentAsync(fixture);
        Assert.Equal(job.GetProperty("manifestSha256").GetString(), current.GetProperty("manifestSha256").GetString());
        var check = current.GetProperty("downloadCheck");
        Assert.Equal(1, check.GetProperty("attempts").GetInt32());
        Assert.True(check.GetProperty("lastSucceeded").GetBoolean());
        Assert.False(check.GetProperty("pending").GetBoolean());
        Assert.True(check.GetProperty("lastCompletedUtcMs").GetInt64() > 0);
        Assert.Equal(1, distribution.ManifestHits);
        Assert.Equal(2, distribution.PackageHits);
    }

    [Theory]
    [InlineData("missing", "remote_check_failed")]
    [InlineData("unavailable", "remote_check_failed")]
    [InlineData("manifest-redirect", "remote_check_failed")]
    [InlineData("package-redirect", "remote_check_failed")]
    [InlineData("range-redirect", "validation_failed")]
    [InlineData("manifest-corrupt", "validation_failed")]
    [InlineData("manifest-large", "validation_failed")]
    [InlineData("package-corrupt", "validation_failed")]
    [InlineData("package-large", "validation_failed")]
    [InlineData("no-range", "validation_failed")]
    [InlineData("range-wrong", "validation_failed")]
    [InlineData("range-length", "validation_failed")]
    public async Task DownloadFailuresPreserveActivationAndNeverFollowRedirects(string mode, string code)
    {
        await using var distribution = await PublicDistributionFixture.StartAsync();
        PublishFixture fixture = distribution.Publication;
        string id = await distribution.PublishAsync();
        byte[] manifest = await File.ReadAllBytesAsync(Path.Combine(fixture.Options.ReleaseRoot, "manifest.json"));
        distribution.Mode = mode;
        Assert.True(await fixture.ExecutePublicCheckAsync());
        var job = await JobAsync(fixture, id);
        Assert.Equal("activeUnverified", job.GetProperty("state").GetString());
        Assert.Equal(code, job.GetProperty("errorCode").GetString());
        Assert.False(job.GetProperty("downloadCheck").GetProperty("lastSucceeded").GetBoolean());
        Assert.Equal(manifest, await File.ReadAllBytesAsync(Path.Combine(fixture.Options.ReleaseRoot, "manifest.json")));
        Assert.Equal(0, distribution.RedirectHits);
        Assert.Equal(1, (await ReadJobAsync(fixture, id)).Attempts);
    }

    [Theory]
    [InlineData("manifest-stall")]
    [InlineData("package-stall")]
    [InlineData("range-stall")]
    public async Task TotalDeadlineCoversBodiesAfterHeadersHaveArrived(string mode)
    {
        await using var distribution = await PublicDistributionFixture.StartAsync();
        PublishFixture fixture = distribution.Publication;
        string id = await distribution.PublishAsync();
        fixture.Options.PublicCheckTimeoutSeconds = 2;
        distribution.Mode = mode;
        var watch = Stopwatch.StartNew();
        Assert.True(await fixture.ExecutePublicCheckAsync().WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.True(distribution.Entered.Task.IsCompleted);
        Assert.InRange(watch.Elapsed.TotalSeconds, 1, 14);
        var job = await JobAsync(fixture, id);
        Assert.Equal("activeUnverified", job.GetProperty("state").GetString());
        Assert.Equal("remote_check_timeout", job.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task OriginalJobRetryIsConcurrentIdempotentAndDoesNotRewriteAnyPublicationArtifacts()
    {
        await using var distribution = await PublicDistributionFixture.StartAsync();
        PublishFixture fixture = distribution.Publication;
        string id = await distribution.PublishAsync();
        distribution.Mode = "missing";
        Assert.True(await fixture.ExecutePublicCheckAsync());
        var failed = await ReadJobAsync(fixture, id);
        var files = Directory.GetFiles(fixture.Options.ReleaseRoot).Append(Path.Combine(fixture.App.Root, ".public-update-state", "receipt.json"))
            .ToDictionary(path => path, path => (File.GetLastWriteTimeUtc(path), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
        fixture.App.Clock.Advance(TimeSpan.FromSeconds(1));
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => fixture.App.Client.PostAsJsonAsync(RetryUrl(id), new { })));
        foreach (var response in responses)
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
                var body = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(id, body.GetProperty("id").GetString());
                Assert.False(body.GetProperty("downloadCheck").GetProperty("lastSucceeded").GetBoolean());
                Assert.True(body.GetProperty("downloadCheck").GetProperty("pending").GetBoolean());
            }
        }
        Assert.False(await fixture.ExecuteAsync());
        distribution.Mode = "healthy";
        Assert.True(await fixture.ExecutePublicCheckAsync());
        var completed = await ReadJobAsync(fixture, id);
        Assert.Equal(PublishJobState.Completed, completed.State);
        Assert.Equal(2, completed.PublicCheckAttempts);
        Assert.Equal(failed.Attempts, completed.Attempts);
        Assert.Equal(failed.ActivatedUtcMs, completed.ActivatedUtcMs);
        Assert.True(completed.PublicCheckCompletedUtcMs > failed.PublicCheckCompletedUtcMs);
        await using var scope = fixture.App.App.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
        Assert.Single(await database.ReleaseJobs.Where(job => job.Kind == "publish").ToListAsync());
        Assert.Single(await database.AuditEvents.Where(item => item.Action == "publish.public_check_retried").ToListAsync());
        foreach (var (path, expected) in files)
            Assert.Equal(expected, (File.GetLastWriteTimeUtc(path), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
        using var again = await fixture.App.Client.PostAsJsonAsync(RetryUrl(id), new { });
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        Assert.False(await fixture.ExecutePublicCheckAsync());
    }

    [Fact]
    public async Task AValidButStalePublicManifestCannotCompleteTheCurrentJob()
    {
        await using var distribution = await PublicDistributionFixture.StartAsync();
        string id = await distribution.PublishAsync(2);
        distribution.ManifestOverride = distribution.Publication.Sign(1);
        Assert.True(await distribution.Publication.ExecutePublicCheckAsync());
        Assert.Equal(PublishJobState.ActiveUnverified, (await ReadJobAsync(distribution.Publication, id)).State);
        Assert.Equal(0, distribution.PackageHits);
    }

    [Fact]
    public async Task RetryCannotCompeteWithANewerQueuedPublicationOrReplaceItsManifest()
    {
        await using var distribution = await PublicDistributionFixture.StartAsync();
        PublishFixture fixture = distribution.Publication;
        string oldId = await distribution.PublishAsync();
        distribution.Mode = "missing";
        Assert.True(await fixture.ExecutePublicCheckAsync());
        var next = await fixture.UploadAsync(fixture.Sign(2));
        string newId = await fixture.ConfirmAsync(next.DraftId);
        using (var busy = await fixture.App.Client.PostAsJsonAsync(RetryUrl(oldId), new { }))
        {
            Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
            Assert.Equal("publish_in_progress", (await busy.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        Assert.True(await fixture.ExecuteAsync());
        using (var changed = await fixture.App.Client.PostAsJsonAsync(RetryUrl(oldId), new { }))
        {
            Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
            Assert.Equal("public_check_baseline_changed", (await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        distribution.Mode = "healthy";
        Assert.True(await fixture.ExecutePublicCheckAsync());
        Assert.Equal(PublishJobState.Completed, (await ReadJobAsync(fixture, newId)).State);
        Assert.Equal(2, (await CurrentAsync(fixture)).GetProperty("sequence").GetInt64());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewerExternalPublicationBeforeOrDuringTheCheckSupersedesTheOldJob(bool during)
    {
        await using var distribution = await PublicDistributionFixture.StartAsync();
        PublishFixture fixture = distribution.Publication;
        string id = await distribution.PublishAsync();
        byte[] old = await File.ReadAllBytesAsync(Path.Combine(fixture.Options.ReleaseRoot, "manifest.json"));
        string expected = Convert.ToHexString(SHA256.HashData(old));
        Task<bool>? running = null;
        if (during)
        {
            distribution.Mode = "hold-range";
            running = fixture.ExecutePublicCheckAsync();
            await distribution.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        byte[] newer = fixture.Sign(2);
        Assert.True((await fixture.ActivateOutsideApiAsync(newer, expected)).Succeeded);
        distribution.Continue.TrySetResult();
        Assert.True(await (running ?? fixture.ExecutePublicCheckAsync()));
        var job = await ReadJobAsync(fixture, id);
        Assert.Equal(PublishJobState.Superseded, job.State);
        Assert.Null(job.PublicCheckSucceeded);
        var current = await CurrentAsync(fixture);
        Assert.Equal(2, current.GetProperty("sequence").GetInt64());
        Assert.Equal(JsonValueKind.Null, current.GetProperty("downloadCheck").ValueKind);
        Assert.Equal(JsonValueKind.Null, current.GetProperty("activationReceipt").ValueKind);
        Assert.Equal(newer, await File.ReadAllBytesAsync(Path.Combine(fixture.Options.ReleaseRoot, "manifest.json")));
    }

    [Fact]
    public async Task ExpiredCheckLeaseAfterRestartResumesOnlyTheCheck()
    {
        await using var distribution = await PublicDistributionFixture.StartAsync();
        PublishFixture fixture = distribution.Publication;
        string id = await distribution.PublishAsync();
        await using (var scope = fixture.App.App.Services.CreateAsyncScope())
            Assert.NotNull(await scope.ServiceProvider.GetRequiredService<PublicVerificationTaskQueue>().ClaimAsync("dead-checker", default));
        Assert.True((await CurrentAsync(fixture)).GetProperty("downloadCheck").GetProperty("running").GetBoolean());
        Assert.False(await fixture.ExecutePublicCheckAsync());
        await fixture.App.RestartAsync();
        fixture.App.Clock.Advance(TimeSpan.FromSeconds(61));
        var waiting = (await CurrentAsync(fixture)).GetProperty("downloadCheck");
        Assert.True(waiting.GetProperty("pending").GetBoolean());
        Assert.False(waiting.GetProperty("running").GetBoolean());
        Assert.False((await JobAsync(fixture, id)).GetProperty("downloadCheck").GetProperty("running").GetBoolean());
        Assert.True(await fixture.ExecutePublicCheckAsync());
        var job = await ReadJobAsync(fixture, id);
        Assert.Equal(PublishJobState.Completed, job.State);
        Assert.Equal(2, job.PublicCheckAttempts);
        Assert.Equal(1, job.Attempts);
        Assert.False(await fixture.ExecuteAsync());
    }

    [Fact]
    public async Task CancellationLeavesAReclaimableCheckWithoutReactivation()
    {
        await using var distribution = await PublicDistributionFixture.StartAsync();
        PublishFixture fixture = distribution.Publication;
        string id = await distribution.PublishAsync();
        distribution.Mode = "package-stall";
        using var cancellation = new CancellationTokenSource();
        Task<bool> running = fixture.ExecutePublicCheckAsync(cancellation.Token);
        await distribution.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(PublishJobState.PublicVerifying, (await ReadJobAsync(fixture, id)).State);
        fixture.App.Clock.Advance(TimeSpan.FromSeconds(61));
        distribution.Mode = "healthy";
        Assert.True(await fixture.ExecutePublicCheckAsync());
        Assert.Equal(1, (await ReadJobAsync(fixture, id)).Attempts);
    }

    [Fact]
    public async Task LongDownloadRenewsItsLeaseAndCannotBeClaimedByAnotherChecker()
    {
        await using var distribution = await PublicDistributionFixture.StartAsync();
        PublishFixture fixture = distribution.Publication;
        string id = await distribution.PublishAsync();
        fixture.Options.PublicCheckTimeoutSeconds = 60;
        distribution.Mode = "hold-range";
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        Task<bool> running = fixture.ExecutePublicCheckAsync(cancellation.Token);
        try
        {
            await distribution.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            ReleaseJob original = await ReadJobAsync(fixture, id);
            fixture.App.Clock.Advance(TimeSpan.FromSeconds(30));
            var watch = Stopwatch.StartNew();
            while ((await ReadJobAsync(fixture, id)).LeaseExpiresUtcMs <= original.LeaseExpiresUtcMs)
            {
                if (watch.Elapsed > TimeSpan.FromSeconds(25)) throw new TimeoutException("The active download did not renew its lease.");
                await Task.Delay(100, cancellation.Token);
            }
            // The original lease is now expired; only the real heartbeat keeps the job owned.
            fixture.App.Clock.Advance(TimeSpan.FromSeconds(31));
            await using (var scope = fixture.App.App.Services.CreateAsyncScope())
                Assert.Null(await scope.ServiceProvider.GetRequiredService<PublicVerificationTaskQueue>()
                    .ClaimAsync("competing-checker", cancellation.Token));
            Assert.False(running.IsCompleted);
            distribution.Continue.TrySetResult();
            Assert.True(await running.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            cancellation.Cancel(); distribution.Continue.TrySetResult();
            try { await running; } catch (OperationCanceledException) { }
        }
        ReleaseJob completed = await ReadJobAsync(fixture, id);
        Assert.Equal(PublishJobState.Completed, completed.State);
        Assert.Equal(1, completed.PublicCheckAttempts);
        Assert.Equal(1, completed.Attempts);
    }

    [Fact]
    public async Task LosingTheCheckLeaseStopsItsDownloadAndOnlyTheCheckCanRecover()
    {
        await using var distribution = await PublicDistributionFixture.StartAsync();
        PublishFixture fixture = distribution.Publication;
        string id = await distribution.PublishAsync();
        fixture.Options.PublicCheckTimeoutSeconds = 60;
        distribution.Mode = "package-stall";
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        Task<bool> running = fixture.ExecutePublicCheckAsync(cancellation.Token);
        try
        {
            await distribution.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using (var scope = fixture.App.App.Services.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>().ReleaseJobs.Where(job => job.Id == id)
                    .ExecuteUpdateAsync(update => update.SetProperty(job => job.LeaseOwner, "replacement-checker"));
            Assert.True(await running.WaitAsync(TimeSpan.FromSeconds(25)));
            await distribution.Disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            cancellation.Cancel();
            try { await running; } catch (OperationCanceledException) { }
        }
        ReleaseJob interrupted = await ReadJobAsync(fixture, id);
        Assert.Equal(PublishJobState.PublicVerifying, interrupted.State);
        Assert.Equal("replacement-checker", interrupted.LeaseOwner);
        Assert.Null(interrupted.PublicCheckCompletedUtcMs);
        Assert.Null(interrupted.PublicCheckSucceeded);
        fixture.App.Clock.Advance(TimeSpan.FromSeconds(61));
        distribution.Mode = "healthy";
        Assert.True(await fixture.ExecutePublicCheckAsync());
        ReleaseJob completed = await ReadJobAsync(fixture, id);
        Assert.Equal(PublishJobState.Completed, completed.State);
        Assert.Equal(2, completed.PublicCheckAttempts);
        Assert.Equal(1, completed.Attempts);
        Assert.False(await fixture.ExecuteAsync());
    }

    internal static string RetryUrl(string id) => $"/api/v1/publish-jobs/{id}/public-check/retry";
    internal static Task<JsonElement> JobAsync(PublishFixture fixture, string id)
        => fixture.App.Client.GetFromJsonAsync<JsonElement>($"/api/v1/publish-jobs/{id}");
    internal static async Task<JsonElement> CurrentAsync(PublishFixture fixture)
        => (await fixture.App.Client.GetFromJsonAsync<JsonElement>("/api/v1/releases/current")).GetProperty("release");
    internal static async Task<ReleaseJob> ReadJobAsync(PublishFixture fixture, string id)
    {
        await using var scope = fixture.App.App.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>().ReleaseJobs.AsNoTracking().SingleAsync(job => job.Id == id);
    }
}
