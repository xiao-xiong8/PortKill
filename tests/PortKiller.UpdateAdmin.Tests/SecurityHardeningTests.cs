using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Contracts;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Persistence.Entities;
using PortKiller.UpdateManagement.Security;

namespace PortKiller.UpdateAdmin.Tests;

public sealed class SecurityHardeningTests
{
    [Fact]
    public void PasswordWindowsExpireAndDoNotExtendABlock()
    {
        var clock = new ManualClock(); var service = new LoginProtectionService(new AdminOptions(), clock);
        var address = IPAddress.Loopback;
        for (int i = 0; i < 4; i++) Assert.Equal(0, service.RecordFailure("operator", address));
        Assert.Equal(60, service.RecordFailure("operator", address));
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(30, service.RecordFailure("operator", address));
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.False(service.IsBlocked("operator", address, out _));
        Assert.Equal(0, service.RecordFailure("operator", address));
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(0, service.RecordFailure("operator", address));
    }

    [Fact]
    public void AccountFailuresAccumulateAcrossAddressesAndCapacityNeverEvictsABlock()
    {
        var clock = new ManualClock(); var options = new AdminOptions { LoginProtectionMaximumEntries = 100 };
        var service = new LoginProtectionService(options, clock);
        for (int i = 0; i < 10; i++)
            Assert.Equal(i == 9 ? 300 : 0, service.RecordFailure("operator", IPAddress.Parse($"198.51.100.{i + 1}")));
        Assert.True(service.IsBlocked("OPERATOR", IPAddress.Parse("203.0.113.1"), out int delay));
        Assert.Equal(300, delay);
        for (int i = 0; i < 200; i++) service.RecordFailure("spray" + i, IPAddress.Loopback);
        Assert.True(service.IsBlocked("operator", IPAddress.Loopback, out delay));
        Assert.Equal(300, delay);
        Assert.True(service.IsBlocked("new-account", IPAddress.Loopback, out _));
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.False(service.IsBlocked("new-account", IPAddress.Loopback, out _));
    }

    [Fact]
    public async Task PasswordSuccessDoesNotClearMfaFailuresAndPasswordFailuresDoNotConsumeThem()
    {
        await using var app = await TestApplication.StartAsync(); await app.SeedAdminAsync();
        using var scope = app.App.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AdminUser>>();
        var user = (await users.FindByNameAsync("operator"))!;
        await users.AccessFailedAsync(user); await users.AccessFailedAsync(user);
        var authentication = scope.ServiceProvider.GetRequiredService<PasswordAuthenticationService>();
        await authentication.LoginAsync("operator", "Test-Only-Password!492");
        await Assert.ThrowsAsync<AdminFailure>(() => authentication.LoginAsync("operator", "wrong-password"));
        await scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>().Entry(user).ReloadAsync();
        Assert.Equal(2, user.AccessFailedCount);
    }

    [Fact]
    public async Task ConcurrentDifferentMfaCodesCannotActivateTwoSessionsOrResurrectPendingSessions()
    {
        await using var app = await TestApplication.StartAsync(); var (key, _, _) = await MfaTests.EnrollAsync(app);
        using var first = app.NewClient(); using var second = app.NewClient();
        await MfaTests.LoginAsync(first); await MfaTests.LoginAsync(second);
        var responses = await Task.WhenAll(
            Task.Run(() => first.PostAsJsonAsync("/api/v1/auth/mfa/verify", new { code = MfaTests.Code(key, 1) })),
            Task.Run(() => second.PostAsJsonAsync("/api/v1/auth/mfa/verify", new { code = MfaTests.Code(key, 2) })));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Unauthorized);
        using var scope = app.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
        Assert.Equal(1, await db.AdminSessions.CountAsync(x => x.Phase == SessionPhase.Full && x.RevokedUtcMs == null));
        Assert.Equal(0, await db.AdminSessions.CountAsync(x => x.Phase != SessionPhase.Full && x.RevokedUtcMs == null));
        var loser = responses[0].StatusCode == HttpStatusCode.OK ? second : first;
        Assert.Equal(HttpStatusCode.Unauthorized, (await loser.PostAsJsonAsync("/api/v1/auth/mfa/verify", new { code = MfaTests.Code(key, 3) })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task FailedMfaKeepsPreviousSessionAndSuccessfulReplacementSurvivesRestart()
    {
        await using var app = await TestApplication.StartAsync(); var (key, _, _) = await MfaTests.EnrollAsync(app);
        using var other = app.NewClient(); await MfaTests.LoginAsync(other);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.PostAsJsonAsync("/api/v1/auth/mfa/verify", new { code = "invalid" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.Client.GetAsync("/api/v1/auth/me")).StatusCode);
        (await other.PostAsJsonAsync("/api/v1/auth/mfa/verify", new { code = MfaTests.Code(key, 1) })).EnsureSuccessStatusCode();
        var rejected = await app.Client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.False(rejected.Headers.Contains("Set-Cookie"));
        await app.RestartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client.GetAsync("/api/v1/auth/me")).StatusCode);
        using var scope = app.App.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>().AdminSessions
            .CountAsync(x => x.Phase == SessionPhase.Full && x.RevokedUtcMs == null));
    }

    [Fact]
    public async Task ActivationRollbackLeavesPreviousSessionValidAndOfflineRevocationIsIdempotent()
    {
        await using var app = await TestApplication.StartAsync(); await MfaTests.EnrollAsync(app);
        using (var scope = app.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync();
            var user = await db.Users.SingleAsync();
            await scope.ServiceProvider.GetRequiredService<AdminSessionService>().CreateAsync(user, SessionPhase.Full);
            await tx.RollbackAsync();
        }
        Assert.Equal(HttpStatusCode.OK, (await app.Client.GetAsync("/api/v1/auth/me")).StatusCode);
        using (var scope = app.App.Services.CreateScope())
        {
            var sessions = scope.ServiceProvider.GetRequiredService<AdminSessionService>();
            Assert.Equal(1, await sessions.RevokeAllOfflineAsync());
            Assert.Equal(0, await sessions.RevokeAllOfflineAsync());
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task RevokedInFlightUploadCannotCommitBytesOrLeavePayload()
    {
        await using var app = await TestApplication.StartAsync(); await MfaTests.EnrollAsync(app);
        using var created = await app.Client.PostAsJsonAsync("/api/v1/uploads/", new { manifestBytes = 4, packageBytes = 4 });
        created.EnsureSuccessStatusCode();
        var upload = await created.Content.ReadFromJsonAsync<JsonElement>();
        string id = upload.GetProperty("id").GetString()!;
        var me = await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        await using var scope = app.App.Services.CreateAsyncScope();
        var sessions = scope.ServiceProvider.GetRequiredService<AdminSessionService>();
        var db = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
        var ticket = new SessionTicket(await db.Users.SingleAsync(), await db.AdminSessions.SingleAsync(s => s.Id == me.GetProperty("currentSessionId").GetString()));
        var principal = await sessions.PrincipalAsync(ticket);
        var stream = new GatedStream([1, 2, 3, 4]);
        var writing = scope.ServiceProvider.GetRequiredService<UploadManagementService>().WritePartAsync(principal, id, UploadPart.Manifest, stream, null, CancellationToken.None);
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await using (var revoke = app.App.Services.CreateAsyncScope())
            await revoke.ServiceProvider.GetRequiredService<AdminSessionService>().RevokeAllOfflineAsync();
        stream.Continue.TrySetResult();
        Assert.Equal("authentication_required", (await Assert.ThrowsAsync<AdminFailure>(() => writing)).Code);
        var state = await db.UploadSessions.AsNoTracking().SingleAsync(s => s.Id == id);
        Assert.Equal(0, state.ManifestReceivedBytes);
        Assert.Empty(Directory.GetFiles(Path.Combine(app.Root, "staging", state.StorageId), "*.part"));
        Assert.False(File.Exists(Path.Combine(app.Root, "staging", state.StorageId, "manifest.envelope")));
    }

    [Fact]
    public async Task SessionTrafficDoesNotRenewOrDeleteTheBrowserCookie()
    {
        await using var app = await TestApplication.StartAsync(); await MfaTests.EnrollAsync(app);
        for (int i = 0; i < 3; i++)
        {
            app.Clock.Advance(TimeSpan.FromDays(4));
            using var response = await app.Client.GetAsync("/api/v1/auth/me");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }
        app.Clock.Advance(TimeSpan.FromDays(3));
        using var expired = await app.Client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        Assert.False(expired.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task StrongReauthenticationUpdatesTheSameSessionWithoutWritingACookie()
    {
        await using var app = await TestApplication.StartAsync(); var (key, _, _) = await MfaTests.EnrollAsync(app);
        var before = await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        using var response = await app.Client.PostAsJsonAsync("/api/v1/auth/reauthenticate", new { password = "Test-Only-Password!492", code = MfaTests.Code(key, 1) });
        response.EnsureSuccessStatusCode();
        Assert.False(response.Headers.Contains("Set-Cookie"));
        var after = await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        Assert.Equal(before.GetProperty("currentSessionId").GetString(), after.GetProperty("currentSessionId").GetString());
        Assert.True(after.GetProperty("strongAuthenticationExpiresUtcMs").GetInt64() > app.Clock.GetUtcNow().ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task ApiErrorsExposeOneCorrelationIdentifierAndKestrelOmitsItsServerHeader()
    {
        await using var app = await TestApplication.StartAsync(realHttp: true);
        var response = await app.Client.GetAsync("/api/v1/auth/me");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(body.TryGetProperty("correlationId", out _));
        Assert.False(body.TryGetProperty("traceId", out _));
        Assert.False(response.Headers.Contains("Server"));
    }
}
