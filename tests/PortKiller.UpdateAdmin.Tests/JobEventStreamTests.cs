using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortKiller.UpdateAdmin.Api.Releases;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Persistence.Entities;
using PortKiller.UpdateManagement.Security;

namespace PortKiller.UpdateAdmin.Tests;

public sealed class JobEventStreamTests
{
    [Fact]
    public async Task RealHttpsStreamDeliversSnapshotAndCommittedTransitionsThenHonorsReconnectHeader()
    {
        await using var app = await JobEventTests.StartAsync(realHttp: true);
        string id = await JobEventTests.CreateTaskAsync(app);
        await using (var stream = await EventConnection.OpenAsync(app, id))
        {
            Assert.Equal("text/event-stream", stream.Response.Content.Headers.ContentType?.MediaType);
            Assert.True(stream.Response.Headers.CacheControl?.NoStore);
            Assert.Equal("no", stream.Response.Headers.GetValues("X-Accel-Buffering").Single());
            var snapshot = await stream.NextAsync();
            Assert.Equal("snapshot", snapshot.Kind); Assert.Equal(id + ":1", snapshot.Id);
            var watch = Stopwatch.StartNew();
            await using var scope = app.App.Services.CreateAsyncScope();
            var queue = scope.ServiceProvider.GetRequiredService<ValidationTaskQueue>();
            Assert.NotNull(await queue.ClaimAsync("stream-validator", default));
            var claimed = await stream.NextAsync();
            Assert.Equal("job", claimed.Kind); Assert.Equal(id + ":2", claimed.Id);
            Assert.Equal("claimed", claimed.Data.GetProperty("stage").GetString());
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
            Assert.Equal("validating", (await stream.NextAsync()).Data.GetProperty("validation").GetProperty("validationState").GetString());
            Assert.True(await queue.RejectAsync(id, "stream-validator", "validation_failed", default));
            Assert.Equal(id + ":3", (await stream.NextAsync()).Id);
            Assert.Equal("failed", (await stream.NextAsync()).Data.GetProperty("validation").GetProperty("state").GetString());
        }
        await ReleasedAsync(app);
        // The URL keeps its initial cursor while native EventSource sends a newer Last-Event-ID on reconnect.
        await using var reconnect = await EventConnection.OpenAsync(app, id, after: id + ":1", header: id + ":2");
        var replay = await reconnect.NextAsync();
        Assert.Equal("job", replay.Kind); Assert.Equal(id + ":3", replay.Id);
        Assert.Equal("rejected", replay.Data.GetProperty("stage").GetString());
    }

    [Fact]
    public async Task ReplaySnapshotNeverAdvancesTheBrowserPastUnsentBatches()
    {
        await using var app = await JobEventTests.StartAsync(); string id = await JobEventTests.CreateTaskAsync(app);
        for (int index = 0; index < 70; index++) await JobEventTests.AppendAsync(app, id);
        await using (var stream = await EventConnection.OpenAsync(app, id, after: id + ":0"))
        {
            for (int sequence = 1; sequence <= 32; sequence++)
                Assert.Equal(id + ":" + sequence, (await stream.NextAsync()).Id);
            var snapshot = await stream.NextAsync();
            Assert.Equal("snapshot", snapshot.Kind); Assert.Null(snapshot.Id);
            Assert.Equal(id + ":71", snapshot.Data.GetProperty("eventId").GetString());
        }
        await ReleasedAsync(app); await app.RestartAsync();
        await using var resumed = await EventConnection.OpenAsync(app, id, header: id + ":32");
        Assert.Equal(id + ":33", (await resumed.NextAsync()).Id);
    }

    [Fact]
    public async Task PrunedStreamCursorReceivesResetSnapshotAndThenFreshEvents()
    {
        await using var app = await JobEventTests.StartAsync(); string id = await JobEventTests.CreateTaskAsync(app);
        app.App.Services.GetRequiredService<AdminOptions>().JobEventMaximumRowsPerJob = 2;
        for (int index = 0; index < 3; index++) await JobEventTests.AppendAsync(app, id);
        await using var stream = await EventConnection.OpenAsync(app, id, header: id + ":0");
        var reset = await stream.NextAsync();
        Assert.Equal("reset", reset.Kind); Assert.Equal(id + ":4", reset.Id);
        await JobEventTests.AppendAsync(app, id, "new_progress");
        var next = await stream.NextAsync(); Assert.Equal(id + ":5", next.Id);
        Assert.Equal("new_progress", next.Data.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("logout", "authentication_required")]
    [InlineData("revoke", "authentication_required")]
    [InlineData("disable", "authentication_required")]
    [InlineData("stamp", "authentication_required")]
    [InlineData("phase", "authentication_required")]
    [InlineData("idle", "authentication_required")]
    [InlineData("absolute", "authentication_required")]
    [InlineData("roles", "permission_denied")]
    public async Task OpenStreamRechecksPersistedSessionAndRoles(string action, string expected)
    {
        await using var app = await JobEventTests.StartAsync(realHttp: true); string id = await JobEventTests.CreateTaskAsync(app);
        await using var stream = await EventConnection.OpenAsync(app, id); await stream.NextAsync();
        await using (var scope = app.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AdminUser>>();
            var user = (await users.FindByNameAsync("operator"))!;
            switch (action)
            {
                case "logout": (await app.Client.PostAsJsonAsync("/api/v1/auth/logout", new { })).EnsureSuccessStatusCode(); break;
                case "revoke": await db.AdminSessions.ExecuteUpdateAsync(update => update.SetProperty(session => session.RevokedUtcMs, 1L)); break;
                case "disable": user.Disabled = true; Assert.True((await users.UpdateAsync(user)).Succeeded); break;
                case "stamp": Assert.True((await users.UpdateSecurityStampAsync(user)).Succeeded); break;
                case "phase": await db.AdminSessions.ExecuteUpdateAsync(update => update.SetProperty(session => session.Phase, SessionPhase.MfaRequired)); break;
                case "idle": app.Clock.Advance(TimeSpan.FromMinutes(31)); break;
                case "absolute": await db.AdminSessions.ExecuteUpdateAsync(update => update.SetProperty(session => session.AbsoluteExpiresUtcMs, 1L)); break;
                case "roles": Assert.True((await users.RemoveFromRolesAsync(user, await users.GetRolesAsync(user))).Succeeded); break;
            }
        }
        var closed = await stream.NextAsync();
        Assert.Equal("closed", closed.Kind); Assert.Equal(expected, closed.Data.GetProperty("code").GetString());
        Assert.Null(closed.Id); Assert.Null(await stream.Reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)));
        await ReleasedAsync(app);
    }

    [Fact]
    public async Task OpenStreamRechecksTaskOwnershipEvenWhenItsCookieStillHasReadPermission()
    {
        await using var app = await JobEventTests.StartAsync(); string id = await JobEventTests.CreateTaskAsync(app);
        await JobEventTests.SetRoleAsync(app, "Viewer");
        await using var stream = await EventConnection.OpenAsync(app, id); await stream.NextAsync();
        await using (var scope = app.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
            db.Users.Add(new AdminUser { Id = "new-owner", UserName = "new-owner" }); await db.SaveChangesAsync();
            string draftId = await db.ReleaseJobs.Where(job => job.Id == id).Select(job => job.DraftId).SingleAsync();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.ReleaseJobs.Where(job => job.Id == id).ExecuteUpdateAsync(update => update.SetProperty(job => job.RequestedById, "new-owner"));
            await db.ReleaseDrafts.Where(draft => draft.Id == draftId).ExecuteUpdateAsync(update => update.SetProperty(draft => draft.OwnerId, "new-owner"));
            await transaction.CommitAsync();
        }
        Assert.Equal("resource_not_found", (await stream.NextAsync()).Data.GetProperty("code").GetString());
        await ReleasedAsync(app);
    }

    [Fact]
    public async Task HeartbeatsDoNotExtendTheIdleSessionAndClientDisconnectReleasesItsSlot()
    {
        await using var app = await JobEventTests.StartAsync(realHttp: true); string id = await JobEventTests.CreateTaskAsync(app);
        app.App.Services.GetRequiredService<AdminOptions>().JobStreamHeartbeatSeconds = 1;
        await using (var stream = await EventConnection.OpenAsync(app, id))
        {
            await stream.NextAsync(); app.Clock.Advance(TimeSpan.FromMinutes(29));
            Assert.Equal("snapshot", (await stream.NextAsync()).Kind);
            Assert.Equal("heartbeat", (await stream.NextAsync(includeComments: true)).Kind);
            await using var scope = app.App.Services.CreateAsyncScope();
            var session = await scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>().AdminSessions.SingleAsync(item => item.Phase == SessionPhase.Full);
            Assert.True(app.Clock.GetUtcNow().ToUnixTimeMilliseconds() - session.LastSeenUtcMs >= TimeSpan.FromMinutes(29).TotalMilliseconds);
            app.Clock.Advance(TimeSpan.FromMinutes(2));
            Assert.Equal("closed", (await stream.NextAsync()).Kind);
        }
        await ReleasedAsync(app);
    }

    [Fact]
    public async Task ConnectionLimitAppliesAcrossSessionsOfTheSameAccountAndDisconnectReclaimsCapacity()
    {
        await using var app = await JobEventTests.StartAsync(realHttp: true); string id = await JobEventTests.CreateTaskAsync(app);
        app.App.Services.GetRequiredService<AdminOptions>().JobStreamMaximumConnectionsPerUser = 1;
        using var second = app.NewClient(); await MfaTests.LoginAsync(second);
        await using (var scope = app.App.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AdminUser>>();
            string key = (await users.GetAuthenticatorKeyAsync((await users.FindByNameAsync("operator"))!))!;
            (await second.PostAsJsonAsync("/api/v1/auth/mfa/verify", new { code = MfaTests.Code(key, 1) })).EnsureSuccessStatusCode();
        }
        await using (var stream = await EventConnection.OpenAsync(app, id))
        {
            await stream.NextAsync();
            using var limited = await second.GetAsync($"/api/v1/jobs/{id}/events/stream", HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(3), limited.Headers.RetryAfter?.Delta);
        }
        await ReleasedAsync(app);
        await using var next = await EventConnection.OpenAsync(app, id);
        Assert.Equal("snapshot", (await next.NextAsync()).Kind);
    }

    [Fact]
    public void GlobalLimitAndDoubleDisposeCannotLeakOrOverReleaseSlots()
    {
        var connections = new JobStreamConnections(new AdminOptions { JobStreamMaximumConnections = 2, JobStreamMaximumConnectionsPerUser = 2 });
        using var first = connections.Acquire("a"); using var second = connections.Acquire("b");
        Assert.Equal(429, Assert.Throws<AdminFailure>(() => connections.Acquire("c")).Status);
        first.Dispose(); first.Dispose(); Assert.Equal(1, connections.ActiveConnections);
        using var replacement = connections.Acquire("c"); Assert.Equal(2, connections.ActiveConnections);
    }

    [Fact]
    public async Task MalformedReconnectHeaderIsRejectedBeforeStartingAStream()
    {
        await using var app = await JobEventTests.StartAsync(); string id = await JobEventTests.CreateTaskAsync(app);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/jobs/{id}/events/stream");
        request.Headers.TryAddWithoutValidation("Last-Event-ID", "another-job:9");
        using var response = await app.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, app.App.Services.GetRequiredService<JobStreamConnections>().ActiveConnections);
    }

    [Fact]
    public async Task ApiShutdownAndConnectionRotationBothReleaseStreams()
    {
        await using var app = await JobEventTests.StartAsync(realHttp: true); string id = await JobEventTests.CreateTaskAsync(app);
        app.App.Services.GetRequiredService<AdminOptions>().JobStreamLifetimeSeconds = 1;
        await using (var stream = await EventConnection.OpenAsync(app, id))
        {
            await stream.NextAsync();
            Assert.Null(await stream.Reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)));
        }
        await ReleasedAsync(app);
        app.App.Services.GetRequiredService<AdminOptions>().JobStreamLifetimeSeconds = 600;
        await using var active = await EventConnection.OpenAsync(app, id); await active.NextAsync();
        await app.App.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, app.App.Services.GetRequiredService<JobStreamConnections>().ActiveConnections);
    }

    [Fact]
    public async Task SlowWriteHasADeadlineAndHoldsNoDatabaseTransactionOrEventQueue()
    {
        await using var app = await JobEventTests.StartAsync(); string id = await JobEventTests.CreateTaskAsync(app);
        app.App.Services.GetRequiredService<AdminOptions>().JobStreamWriteTimeoutSeconds = 1;
        var body = new BlockedWriteStream(); var requestLifetime = new AbortFeature(body);
        var context = new DefaultHttpContext { User = await JobEventTests.PrincipalAsync(app), RequestServices = app.App.Services };
        context.Response.Body = body; context.Features.Set<IHttpRequestLifetimeFeature>(requestLifetime);
        var watch = Stopwatch.StartNew();
        Task writing = app.App.Services.GetRequiredService<JobEventStream>().WriteAsync(context, id, null);
        await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, app.App.Services.GetRequiredService<JobStreamConnections>().ActiveConnections);
        await JobEventTests.AppendAsync(app, id).WaitAsync(TimeSpan.FromMilliseconds(900));
        await writing.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(requestLifetime.Aborted); Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
        Assert.Equal(0, app.App.Services.GetRequiredService<JobStreamConnections>().ActiveConnections);
        Assert.Equal(id + ":2", (await JobEventTests.ReadAsync(app, id)).GetProperty("cursor").GetString());
    }

    internal static async Task ReleasedAsync(TestApplication app)
    {
        var connections = app.App.Services.GetRequiredService<JobStreamConnections>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (connections.ActiveConnections != 0) await Task.Delay(25, timeout.Token);
    }

    private sealed class AbortFeature(BlockedWriteStream body) : IHttpRequestLifetimeFeature
    {
        public bool Aborted { get; private set; }
        public CancellationToken RequestAborted { get; set; }
        public void Abort() { Aborted = true; body.Unblock.TrySetResult(); }
    }

    private sealed class BlockedWriteStream : Stream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Unblock { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { Entered.TrySetResult(); await Unblock.Task; }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

internal sealed class EventConnection : IAsyncDisposable
{
    private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(20));
    public HttpResponseMessage Response { get; private set; } = null!;
    public StreamReader Reader { get; private set; } = null!;
    public static async Task<EventConnection> OpenAsync(TestApplication app, string id, string? after = null, string? header = null)
    {
        var connection = new EventConnection();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/jobs/{id}/events/stream"
            + (after == null ? "" : "?after=" + Uri.EscapeDataString(after)));
        if (header != null) request.Headers.Add("Last-Event-ID", header);
        connection.Response = await app.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connection._cancellation.Token);
        connection.Response.EnsureSuccessStatusCode();
        connection.Reader = new StreamReader(await connection.Response.Content.ReadAsStreamAsync()); return connection;
    }

    public async Task<(string Kind, string? Id, JsonElement Data)> NextAsync(bool includeComments = false)
    {
        while (true)
        {
            string? kind = null, id = null, data = null; bool heartbeat = false;
            while (true)
            {
                string line = await Reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) ?? throw new EndOfStreamException();
                if (line.Length == 0) break;
                if (line.StartsWith("event: ")) kind = line[7..];
                if (line.StartsWith("id: ")) id = line[4..];
                if (line.StartsWith("data: ")) data = line[6..];
                if (line == ": heartbeat") heartbeat = true;
            }
            if (kind != null) return (kind, id, JsonSerializer.Deserialize<JsonElement>(data!));
            if (includeComments && heartbeat) return ("heartbeat", null, default);
        }
    }
    public async ValueTask DisposeAsync()
    { await _cancellation.CancelAsync(); Reader?.Dispose(); Response?.Dispose(); _cancellation.Dispose(); }
}
