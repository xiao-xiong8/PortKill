using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Persistence.Entities;
using PortKiller.UpdateManagement.Security;

namespace PortKiller.UpdateAdmin.Tests;

public sealed class SessionTests
{
    [Fact]
    public async Task CookieAndSessionRemainValidAfterApiRestart()
    {
        await using var app = await TestApplication.StartAsync(); await MfaTests.EnrollAsync(app);
        await app.RestartAsync();
        Assert.Equal(HttpStatusCode.OK, (await app.Client.GetAsync("/api/v1/system/status")).StatusCode);
    }

    [Fact]
    public async Task IdleSessionExpiresAndRecentActivityExtendsOnlyTheIdleWindow()
    {
        await using var app = await TestApplication.StartAsync(); await MfaTests.EnrollAsync(app);
        app.Clock.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(HttpStatusCode.OK, (await app.Client.GetAsync("/api/v1/auth/me")).StatusCode);
        app.Clock.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(HttpStatusCode.OK, (await app.Client.GetAsync("/api/v1/auth/me")).StatusCode);
        app.Clock.Advance(TimeSpan.FromDays(7).Add(TimeSpan.FromMinutes(1)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task AbsoluteDeadlineCannotBeExtendedByTraffic()
    {
        await using var app = await TestApplication.StartAsync(); await MfaTests.EnrollAsync(app);
        using var scope = app.App.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
        var session = await database.AdminSessions.SingleAsync(x => x.Phase == SessionPhase.Full);
        var sessions = scope.ServiceProvider.GetRequiredService<AdminSessionService>();
        for (int day = 0; day < 4; day++)
        {
            app.Clock.Advance(TimeSpan.FromDays(6));
            Assert.True(await sessions.IsActiveAsync(session.Id, session.UserId, SessionPhase.Full, default));
            await database.AdminSessions.Where(x => x.Id == session.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.LastSeenUtcMs, app.Clock.GetUtcNow().ToUnixTimeMilliseconds()));
        }
        app.Clock.Advance(TimeSpan.FromDays(7));
        Assert.False(await sessions.IsActiveAsync(session.Id, session.UserId, SessionPhase.Full, default));
    }

    [Fact]
    public async Task LogoutRevokesTheServerSession()
    {
        await using var app = await TestApplication.StartAsync(); await MfaTests.EnrollAsync(app);
        string cookie = app.DefaultCookieHeader;
        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.PostAsJsonAsync("/api/v1/auth/logout", new { })).StatusCode);
        using var replay = app.NewClient(); replay.DefaultRequestHeaders.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task PasswordSecurityVersionChangeInvalidatesExistingCookies()
    {
        await using var app = await TestApplication.StartAsync(); await MfaTests.EnrollAsync(app);
        using (var scope = app.App.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AdminUser>>();
            await users.UpdateSecurityStampAsync((await users.FindByNameAsync("operator"))!);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task CompletingAnotherLoginRevokesThePreviousSession()
    {
        await using var app = await TestApplication.StartAsync(); var (key, _, _) = await MfaTests.EnrollAsync(app);
        using var second = app.NewClient(); await MfaTests.LoginAsync(second);
        Assert.Equal(HttpStatusCode.OK, (await app.Client.GetAsync("/api/v1/auth/me")).StatusCode);
        (await second.PostAsJsonAsync("/api/v1/auth/mfa/verify", new { code = MfaTests.Code(key, 1) })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/v1/auth/me")).StatusCode);
        var response = await second.PostAsJsonAsync("/api/v1/auth/logout", new { });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task SessionOwnershipIsCheckedEvenWithAnExistingId()
    {
        await using var app = await TestApplication.StartAsync(); await MfaTests.EnrollAsync(app);
        string foreignId = "foreign-session";
        using (var scope = app.App.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
            database.Users.Add(new AdminUser { Id = "foreign-user", UserName = "foreign" });
            database.AdminSessions.Add(new AdminSession { Id = foreignId, UserId = "foreign-user" });
            await database.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync("/api/v1/auth/sessions/" + foreignId)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.GetAsync("/api/v1/auth/sessions?pageSize=1000")).StatusCode);
    }

    [Fact]
    public async Task ExpiredAuthenticationRecordsCanBeCleanedWithoutDeletingUsers()
    {
        await using var app = await TestApplication.StartAsync(); await MfaTests.EnrollAsync(app);
        app.Clock.Advance(TimeSpan.FromDays(38));
        using var scope = app.App.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AdminSessionService>().CleanupAsync(default);
        var database = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
        Assert.Equal(0, await database.AdminSessions.CountAsync());
        Assert.Equal(0, await database.TotpConsumptions.CountAsync());
        Assert.Equal(1, await database.Users.CountAsync());
    }

    [Fact]
    public async Task PendingAuthenticationHasAnAbsoluteDeadline()
    {
        await using var app = await TestApplication.StartAsync(); await app.SeedAdminAsync();
        await MfaTests.LoginAsync(app.Client); app.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task LongConnectionValidationSeesRevocationFromAnotherScope()
    {
        await using var app = await TestApplication.StartAsync(); await MfaTests.EnrollAsync(app);
        var me = await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        string sessionId = me.GetProperty("currentSessionId").GetString()!;
        string userId = me.GetProperty("userId").GetString()!;
        using var longScope = app.App.Services.CreateScope();
        var sessions = longScope.ServiceProvider.GetRequiredService<AdminSessionService>();
        Assert.True(await sessions.IsActiveAsync(sessionId, userId, SessionPhase.Full, default));
        using (var other = app.App.Services.CreateScope())
            await other.ServiceProvider.GetRequiredService<UpdateAdminDbContext>().AdminSessions.Where(x => x.Id == sessionId)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.RevokedUtcMs, app.Clock.GetUtcNow().ToUnixTimeMilliseconds()));
        Assert.False(await sessions.IsActiveAsync(sessionId, userId, SessionPhase.Full, default));
    }
}
