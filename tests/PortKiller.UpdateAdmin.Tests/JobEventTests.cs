using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Contracts;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Persistence.Entities;
using PortKiller.UpdateManagement.Security;

namespace PortKiller.UpdateAdmin.Tests;

public sealed class JobEventTests
{
    [Fact]
    public async Task MigrationBackfillsExistingEventWatermarks()
    {
        await using var app = await StartAsync(); string id = await CreateTaskAsync(app);
        await AppendAsync(app, id); await AppendAsync(app, id);
        await using var scope = app.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261025052017_P4PublicVerification");
        await migrator.MigrateAsync();
        Assert.Equal(3, await db.ReleaseJobs.Where(job => job.Id == id).Select(job => job.LastEventSequence).SingleAsync());
        await using var transaction = await db.Database.BeginTransactionAsync();
        await scope.ServiceProvider.GetRequiredService<JobEventJournal>().AppendAsync(id, "progress", "after_upgrade", default);
        await transaction.CommitAsync();
        Assert.Equal(new long[] { 1, 2, 3, 4 }, await db.JobEvents.Where(item => item.JobId == id).OrderBy(item => item.Sequence).Select(item => item.Sequence).ToArrayAsync());
    }

    [Fact]
    public async Task ConcurrentWritersProduceDurableContiguousIdsAcrossRestart()
    {
        await using var app = await StartAsync(); string id = await CreateTaskAsync(app);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(index => Task.Run(() => AppendAsync(app, id, "progress_" + index))));
        var before = await ReadAsync(app, id, id + ":0");
        Assert.Equal(13, before.GetProperty("events").GetArrayLength());
        Assert.Equal(Enumerable.Range(1, 13).Select(sequence => id + ":" + sequence),
            before.GetProperty("events").EnumerateArray().Select(item => item.GetProperty("id").GetString()));
        await app.RestartAsync(); await AppendAsync(app, id, "after_restart");
        var after = await ReadAsync(app, id, id + ":13");
        Assert.Equal(id + ":14", after.GetProperty("cursor").GetString());
        Assert.Equal("after_restart", after.GetProperty("events")[0].GetProperty("code").GetString());
        Assert.False(after.GetProperty("reset").GetBoolean());
    }

    [Fact]
    public async Task EventWriteFailureRollsBackClaimStateAndCounter()
    {
        await using var app = await StartAsync(); string id = await CreateTaskAsync(app);
        await using (var scope = app.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER reject_claim_event BEFORE INSERT ON JobEvents WHEN NEW.Stage = 'claimed'
                BEGIN SELECT RAISE(ABORT, 'event write refused'); END;
                """);
            await Assert.ThrowsAsync<DbUpdateException>(() => scope.ServiceProvider.GetRequiredService<ValidationTaskQueue>().ClaimAsync("writer", default));
        }
        await using var check = app.App.Services.CreateAsyncScope();
        var database = check.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
        var job = await database.ReleaseJobs.AsNoTracking().SingleAsync(item => item.Id == id);
        Assert.Equal(PublishJobState.Queued, job.State); Assert.Equal(0, job.Attempts);
        Assert.Equal(1, job.LastEventSequence); Assert.Null(job.LeaseOwner);
        Assert.Equal(1, await database.JobEvents.CountAsync(item => item.JobId == id));
    }

    [Fact]
    public async Task UncommittedStateAndEventsStayInvisibleWithoutBlockingSnapshotReads()
    {
        await using var app = await StartAsync(); string id = await CreateTaskAsync(app);
        await using var scope = app.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.ReleaseJobs.Where(job => job.Id == id).ExecuteUpdateAsync(update => update.SetProperty(job => job.State, PublishJobState.Failed));
            await scope.ServiceProvider.GetRequiredService<JobEventJournal>().AppendAsync(id, "failed", "validation_failed", default);
            var before = await ReadAsync(app, id).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(id + ":1", before.GetProperty("snapshot").GetProperty("eventId").GetString());
            Assert.Equal("queued", before.GetProperty("snapshot").GetProperty("validation").GetProperty("state").GetString());
            await transaction.CommitAsync();
        }
        var after = await ReadAsync(app, id, id + ":1");
        Assert.Equal(id + ":2", after.GetProperty("snapshot").GetProperty("eventId").GetString());
        Assert.Equal("failed", after.GetProperty("snapshot").GetProperty("validation").GetProperty("state").GetString());
        Assert.Equal("failed", after.GetProperty("events")[0].GetProperty("stage").GetString());
    }

    [Fact]
    public async Task EventIdsAboveJavaScriptIntegerPrecisionRoundTripAsOpaqueStrings()
    {
        await using var app = await StartAsync(); string id = await CreateTaskAsync(app);
        const long previous = 9_007_199_254_740_992;
        await using (var scope = app.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
            await db.JobEvents.ExecuteDeleteAsync();
            await db.ReleaseJobs.Where(job => job.Id == id).ExecuteUpdateAsync(update => update.SetProperty(job => job.LastEventSequence, previous));
        }
        await AppendAsync(app, id);
        var page = await ReadAsync(app, id, id + ":9007199254740992");
        Assert.False(page.GetProperty("reset").GetBoolean());
        Assert.Equal(id + ":9007199254740993", page.GetProperty("events")[0].GetProperty("id").GetString());
        Assert.Equal(id + ":9007199254740993", page.GetProperty("cursor").GetString());
    }

    [Fact]
    public async Task LowerRetentionCapAppliesToIdleTasksBeforeAndDuringMaintenance()
    {
        await using var app = await StartAsync(); string id = await CreateTaskAsync(app);
        for (int index = 0; index < 4; index++) await AppendAsync(app, id);
        app.App.Services.GetRequiredService<AdminOptions>().JobEventMaximumRowsPerJob = 2;
        Assert.True((await ReadAsync(app, id, id + ":0")).GetProperty("reset").GetBoolean());
        await using var scope = app.App.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<JobEventJournal>().PruneAsync(default);
        var db = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
        Assert.Equal(new long[] { 4, 5 }, await db.JobEvents.OrderBy(item => item.Sequence).Select(item => item.Sequence).ToArrayAsync());
    }

    [Fact]
    public async Task PerJobAndGlobalCapsAreAppliedInsideAppendAndOldCursorsReset()
    {
        await using var app = await StartAsync();
        var options = app.App.Services.GetRequiredService<AdminOptions>();
        options.JobEventMaximumRowsPerJob = 3; options.JobEventMaximumRows = 4;
        string first = await CreateTaskAsync(app), second = await CreateTaskAsync(app);
        foreach (string id in new[] { first, second })
            for (int index = 0; index < 5; index++) { app.Clock.Advance(TimeSpan.FromMilliseconds(1)); await AppendAsync(app, id); }
        await using var scope = app.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
        Assert.Equal(4, await db.JobEvents.CountAsync());
        Assert.Equal(3, await db.JobEvents.CountAsync(item => item.JobId == second));
        var page = await ReadAsync(app, second, second + ":0");
        Assert.True(page.GetProperty("reset").GetBoolean());
        Assert.Equal(second + ":6", page.GetProperty("cursor").GetString());
        Assert.Empty(page.GetProperty("events").EnumerateArray());
    }

    [Fact]
    public async Task DeletingEveryRetainedEventNeverReusesAnId()
    {
        await using var app = await StartAsync(); string id = await CreateTaskAsync(app);
        await AppendAsync(app, id);
        await using (var scope = app.App.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>().JobEvents.ExecuteDeleteAsync();
        await AppendAsync(app, id);
        var expired = await ReadAsync(app, id, id + ":1");
        Assert.True(expired.GetProperty("reset").GetBoolean());
        var current = await ReadAsync(app, id, id + ":2");
        Assert.False(current.GetProperty("reset").GetBoolean());
        Assert.Equal(id + ":3", current.GetProperty("events")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task RetentionDeadlineIsEnforcedBeforeMaintenanceAndCounterSurvivesPruning()
    {
        await using var app = await StartAsync(); string id = await CreateTaskAsync(app);
        app.Clock.Advance(TimeSpan.FromDays(31));
        await using var scope = app.App.Services.CreateAsyncScope();
        var sessions = scope.ServiceProvider.GetRequiredService<AdminSessionService>();
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<AdminUser>>().FindByNameAsync("operator");
        var ticket = await sessions.CreateAsync(user!, SessionPhase.Full, "totp");
        var principal = await sessions.PrincipalAsync(ticket);
        var query = scope.ServiceProvider.GetRequiredService<JobEventQuery>();
        Assert.True((await query.ReadAsync(principal, id, id + ":0", 10, default)).Reset);
        var journal = scope.ServiceProvider.GetRequiredService<JobEventJournal>();
        await journal.PruneAsync(default);
        var db = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
        Assert.Equal(0, await db.JobEvents.CountAsync());
        Assert.Equal(1, await db.ReleaseJobs.Where(job => job.Id == id).Select(job => job.LastEventSequence).SingleAsync());
        await using (var transaction = await db.Database.BeginTransactionAsync())
        { await journal.AppendAsync(id, "progress", "after_retention", default); await transaction.CommitAsync(); }
        Assert.Equal(id + ":2", (await query.ReadAsync(principal, id, id + ":1", 10, default)).Events.Single().Id);
    }

    [Fact]
    public async Task MissingMiddleEventResetsInsteadOfSilentlySkippingHistory()
    {
        await using var app = await StartAsync(); string id = await CreateTaskAsync(app);
        await AppendAsync(app, id); await AppendAsync(app, id);
        await using (var scope = app.App.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>().JobEvents.Where(item => item.JobId == id && item.Sequence == 2).ExecuteDeleteAsync();
        Assert.True((await ReadAsync(app, id, id + ":1")).GetProperty("reset").GetBoolean());
    }

    [Fact]
    public async Task ReplayPaginationAndSnapshotWatermarkRemainSeparate()
    {
        await using var app = await StartAsync(); string id = await CreateTaskAsync(app);
        for (int index = 0; index < 16; index++) await AppendAsync(app, id);
        string cursor = id + ":0"; int count = 0;
        do
        {
            var page = await app.Client.GetFromJsonAsync<JsonElement>($"/api/v1/jobs/{id}/events?after={Uri.EscapeDataString(cursor)}&limit=3");
            Assert.False(page.GetProperty("reset").GetBoolean());
            Assert.InRange(page.GetProperty("events").GetArrayLength(), 1, 3);
            Assert.Equal(id + ":17", page.GetProperty("snapshot").GetProperty("eventId").GetString());
            count += page.GetProperty("events").GetArrayLength(); cursor = page.GetProperty("cursor").GetString()!;
            if (!page.GetProperty("hasMore").GetBoolean()) break;
        } while (count < 100);
        Assert.Equal(17, count); Assert.Equal(id + ":17", cursor);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.GetAsync($"/api/v1/jobs/{id}/events?limit=101")).StatusCode);
    }

    [Theory]
    [InlineData("other:1")]
    [InlineData("{id}:-1")]
    [InlineData("{id}:01")]
    [InlineData("{id}:9223372036854775808")]
    [InlineData("{id}:1\nevent: forged")]
    public async Task InvalidOrForeignCursorsAreRejected(string value)
    {
        await using var app = await StartAsync(); string id = await CreateTaskAsync(app);
        string cursor = value.Replace("{id}", id);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.GetAsync($"/api/v1/jobs/{id}/events?after={Uri.EscapeDataString(cursor)}")).StatusCode);
    }

    [Fact]
    public async Task InitialAndFutureCursorsReturnAnAuthoritativeSnapshot()
    {
        await using var app = await StartAsync(); string id = await CreateTaskAsync(app);
        foreach (string suffix in new[] { "", "?after=" + Uri.EscapeDataString(id + ":99") })
        {
            var page = await app.Client.GetFromJsonAsync<JsonElement>($"/api/v1/jobs/{id}/events{suffix}");
            Assert.True(page.GetProperty("reset").GetBoolean());
            Assert.Equal(id + ":1", page.GetProperty("cursor").GetString());
            Assert.Equal("queued", page.GetProperty("snapshot").GetProperty("validation").GetProperty("state").GetString());
        }
    }

    [Theory]
    [InlineData("Viewer", false, HttpStatusCode.OK)]
    [InlineData("Viewer", true, HttpStatusCode.NotFound)]
    [InlineData("Uploader", false, HttpStatusCode.OK)]
    [InlineData("Uploader", true, HttpStatusCode.NotFound)]
    [InlineData("Publisher", false, HttpStatusCode.OK)]
    [InlineData("Publisher", true, HttpStatusCode.NotFound)]
    [InlineData("Administrator", true, HttpStatusCode.OK)]
    public async Task SnapshotAndReplayApplyCurrentRolesAndResourceOwnership(string role, bool foreign, HttpStatusCode expected)
    {
        await using var app = await StartAsync(); string id = await CreateTaskAsync(app, foreign);
        await SetRoleAsync(app, role);
        foreach (string suffix in new[] { "", "/events", "/events/stream" })
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var response = await app.Client.GetAsync($"/api/v1/jobs/{id}{suffix}", HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
            Assert.Equal(expected, response.StatusCode);
        }
    }

    [Fact]
    public async Task AnonymousAndPendingSessionsCannotAccessAnyJobEndpoint()
    {
        await using var app = await TestApplication.StartAsync(); await app.SeedAdminAsync();
        using var anonymous = app.NewClient(); await MfaTests.LoginAsync(app.Client);
        foreach (string suffix in new[] { "", "/events", "/events/stream" })
        {
            string url = "/api/v1/jobs/" + Guid.NewGuid().ToString("N") + suffix;
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await app.Client.GetAsync(url)).StatusCode);
        }
    }

    [Theory]
    [InlineData("line\nsecret")]
    [InlineData("/private/key.pem")]
    public async Task EventMessagesRejectRawOutputAndPaths(string code)
    {
        await using var app = await StartAsync(); string id = await CreateTaskAsync(app);
        await Assert.ThrowsAsync<ArgumentException>(() => AppendAsync(app, id, code));
        Assert.Equal(id + ":1", (await ReadAsync(app, id)).GetProperty("cursor").GetString());
    }

    [Fact]
    public async Task PublicationLifecycleUsesTheSameEventJournalAndSnapshot()
    {
        await using var distribution = await PublicDistributionFixture.StartAsync();
        var fixture = distribution.Publication;
        var upload = await fixture.UploadAsync(fixture.Sign(701));
        string id = await fixture.ConfirmAsync(upload.DraftId);
        Assert.True(await fixture.ExecuteAsync()); Assert.True(await fixture.ExecutePublicCheckAsync());
        var page = await ReadAsync(fixture.App, id, id + ":0");
        Assert.Equal("completed", page.GetProperty("snapshot").GetProperty("publication").GetProperty("state").GetString());
        Assert.Equal(new[] { "queued", "claimed", "activating", "manifest_activated", "public_verifying", "completed" },
            page.GetProperty("events").EnumerateArray().Select(item => item.GetProperty("stage").GetString()));
        Assert.True(page.GetProperty("snapshot").GetProperty("publication").GetProperty("downloadCheck").GetProperty("lastSucceeded").GetBoolean());
        var validation = await ReadAsync(fixture.App, upload.JobId, upload.JobId + ":0");
        Assert.Equal("readyForApproval", validation.GetProperty("snapshot").GetProperty("validation").GetProperty("validationState").GetString());
        Assert.Equal(new[] { "queued", "claimed", "validated" }, validation.GetProperty("events").EnumerateArray().Select(item => item.GetProperty("stage").GetString()));
    }

    internal static async Task<TestApplication> StartAsync(bool realHttp = false)
    {
        var app = await TestApplication.StartAsync(realHttp: realHttp);
        try
        {
            await MfaTests.EnrollAsync(app);
            app.App.Services.GetRequiredService<AdminOptions>().JobStreamPollMilliseconds = 100;
            return app;
        }
        catch { await app.DisposeAsync(); throw; }
    }

    internal static async Task<string> CreateTaskAsync(TestApplication app, bool foreign = false)
    {
        await using var scope = app.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
        string owner = await db.Users.Where(user => user.UserName == "operator").Select(user => user.Id).SingleAsync();
        if (foreign)
        { owner = Guid.NewGuid().ToString("N"); db.Users.Add(new AdminUser { Id = owner, UserName = owner }); }
        var upload = new UploadSession { OwnerId = owner, State = UploadState.Uploaded };
        var draft = new ReleaseDraft { OwnerId = owner, UploadId = upload.Id };
        var job = new ReleaseJob { DraftId = draft.Id, RequestedById = owner, CreatedUtcMs = app.Clock.GetUtcNow().ToUnixTimeMilliseconds() };
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.UploadSessions.Add(upload); db.ReleaseDrafts.Add(draft); db.ReleaseJobs.Add(job);
        await scope.ServiceProvider.GetRequiredService<JobEventJournal>().AppendAsync(job.Id, "queued", "validation_queued", default);
        await transaction.CommitAsync(); return job.Id;
    }

    internal static async Task AppendAsync(TestApplication app, string id, string code = "progress")
    {
        await using var scope = app.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await scope.ServiceProvider.GetRequiredService<JobEventJournal>().AppendAsync(id, "progress", code, default);
        await transaction.CommitAsync();
    }

    internal static async Task<JsonElement> ReadAsync(TestApplication app, string id, string? after = null)
        => await app.Client.GetFromJsonAsync<JsonElement>($"/api/v1/jobs/{id}/events"
            + (after == null ? "" : "?after=" + Uri.EscapeDataString(after)));

    internal static async Task SetRoleAsync(TestApplication app, string role)
    {
        await using var scope = app.App.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AdminUser>>();
        var user = (await users.FindByNameAsync("operator"))!;
        Assert.True((await users.RemoveFromRolesAsync(user, await users.GetRolesAsync(user))).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
    }

    internal static async Task<ClaimsPrincipal> PrincipalAsync(TestApplication app)
    {
        await using var scope = app.App.Services.CreateAsyncScope();
        var user = (await scope.ServiceProvider.GetRequiredService<UserManager<AdminUser>>().FindByNameAsync("operator"))!;
        var db = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
        var session = await db.AdminSessions.Where(item => item.Phase == SessionPhase.Full && item.UserId == user.Id)
            .OrderByDescending(item => item.CreatedUtcMs).FirstAsync();
        return await scope.ServiceProvider.GetRequiredService<AdminSessionService>().PrincipalAsync(new(user, session));
    }
}
