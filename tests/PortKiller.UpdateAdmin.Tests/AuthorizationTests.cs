using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Persistence.Entities;
using PortKiller.UpdateManagement.Security;

namespace PortKiller.UpdateAdmin.Tests;

public sealed class AuthorizationTests
{
    [Theory]
    [InlineData("Viewer", false, false, false)]
    [InlineData("Uploader", true, false, false)]
    [InlineData("Publisher", false, true, false)]
    [InlineData("Administrator", true, true, true)]
    public async Task RolePoliciesApplyThePermissionMatrix(string role, bool upload, bool publish, bool accounts)
    {
        await using var app = await TestApplication.StartAsync(); await MfaTests.EnrollAsync(app);
        using var scope = app.App.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AdminUser>>(); var user = (await users.FindByNameAsync("operator"))!;
        Assert.True((await users.RemoveFromRoleAsync(user, "Administrator")).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        var sessions = scope.ServiceProvider.GetRequiredService<AdminSessionService>();
        var db = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
        var ticket = new SessionTicket(user, await db.AdminSessions.SingleAsync(x => x.Phase == SessionPhase.Full));
        var principal = await sessions.PrincipalAsync(ticket);
        var auth = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        Assert.True((await auth.AuthorizeAsync(principal, null, AdminPermissions.Read)).Succeeded);
        Assert.Equal(upload, (await auth.AuthorizeAsync(principal, null, AdminPermissions.Upload)).Succeeded);
        Assert.Equal(publish, (await auth.AuthorizeAsync(principal, null, AdminPermissions.Publish)).Succeeded);
        Assert.Equal(accounts, (await auth.AuthorizeAsync(principal, null, AdminPermissions.Accounts)).Succeeded);
        Assert.Equal(accounts ? HttpStatusCode.OK : HttpStatusCode.Forbidden, (await app.Client.GetAsync("/api/v1/accounts/")).StatusCode);
    }

    [Fact]
    public async Task MissingOrForeignCsrfCannotLoginOrModifyState()
    {
        await using var app = await TestApplication.StartAsync(); await MfaTests.EnrollAsync(app);
        using var anonymous = app.NewClient(autoCsrf: false);
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.PostAsJsonAsync("/api/v1/auth/login", new { userName = "operator", password = "Test-Only-Password!492" })).StatusCode);
        var jar = new CookieContainer();
        foreach (string cookie in app.DefaultCookieHeader.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            jar.SetCookies(new Uri("https://localhost"), cookie + "; Path=/; Secure");
        using var authenticated = app.NewClient(jar, autoCsrf: false);
        Assert.Equal(HttpStatusCode.BadRequest, (await authenticated.PostAsJsonAsync("/api/v1/auth/logout", new { })).StatusCode);
        string foreign = (await anonymous.GetFromJsonAsync<JsonElement>("/api/v1/auth/csrf")).GetProperty("requestToken").GetString()!;
        authenticated.DefaultRequestHeaders.Add("X-PortKill-CSRF", foreign);
        Assert.Equal(HttpStatusCode.BadRequest, (await authenticated.PostAsJsonAsync("/api/v1/auth/logout", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.Client.GetAsync("/api/v1/system/status")).StatusCode);
    }

    [Fact]
    public async Task LastEnabledMfaAdministratorCannotBeRemovedDisabledOrReset()
    {
        await using var app = await TestApplication.StartAsync(); await MfaTests.EnrollAsync(app);
        string id = (await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me")).GetProperty("userId").GetString()!;
        Assert.Equal(HttpStatusCode.Conflict, (await app.Client.PutAsJsonAsync($"/api/v1/accounts/{id}/roles", new { roles = new[] { "Viewer" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await app.Client.PutAsJsonAsync($"/api/v1/accounts/{id}/state", new { disabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await app.Client.PostAsJsonAsync($"/api/v1/accounts/{id}/reset-mfa", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.Client.GetAsync("/api/v1/system/status")).StatusCode);
    }

    [Fact]
    public async Task RoleAndDisableChangesInvalidateTargetCookies()
    {
        await using var app = await TestApplication.StartAsync(); await MfaTests.EnrollAsync(app);
        var created = await app.Client.PostAsJsonAsync("/api/v1/accounts/", new { userName = "viewer", password = "Viewer-Only-Password!932", roles = new[] { "Viewer" } });
        created.EnsureSuccessStatusCode(); string id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        using var viewer = app.NewClient();
        (await viewer.PostAsJsonAsync("/api/v1/auth/login", new { userName = "viewer", password = "Viewer-Only-Password!932" })).EnsureSuccessStatusCode();
        var start = await viewer.PostAsJsonAsync("/api/v1/auth/mfa/enrollment/start", new { });
        string key = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sharedKey").GetString()!;
        (await viewer.PostAsJsonAsync("/api/v1/auth/mfa/enrollment/confirm", new { code = MfaTests.Code(key) })).EnsureSuccessStatusCode();
        (await viewer.PostAsJsonAsync("/api/v1/auth/mfa/enrollment/acknowledge", new { acknowledged = true })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync($"/api/v1/accounts/{id}/roles", new { roles = new[] { "Administrator" } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/system/status")).StatusCode);
        (await app.Client.PutAsJsonAsync($"/api/v1/accounts/{id}/roles", new { roles = new[] { "Uploader" } })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await viewer.GetAsync("/api/v1/system/status")).StatusCode);
        (await app.Client.PutAsJsonAsync($"/api/v1/accounts/{id}/state", new { disabled = true })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await viewer.PostAsJsonAsync("/api/v1/auth/login", new { userName = "viewer", password = "Viewer-Only-Password!932" })).StatusCode);
    }

    [Fact]
    public async Task SensitiveChangesRequireFreshStrongAuthenticationAndPasswordChangeRevokesSelf()
    {
        await using var app = await TestApplication.StartAsync(); var (key, _, _) = await MfaTests.EnrollAsync(app);
        app.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Client.GetAsync("/api/v1/accounts/")).StatusCode);
        (await app.Client.PostAsJsonAsync("/api/v1/auth/reauthenticate", new { password = "Test-Only-Password!492", code = MfaTests.Code(key, 1) })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await app.Client.GetAsync("/api/v1/accounts/")).StatusCode);
        (await app.Client.PostAsJsonAsync("/api/v1/auth/password", new { currentPassword = "Test-Only-Password!492", newPassword = "Changed-Password!92837" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client.PostAsJsonAsync("/api/v1/auth/login", new { userName = "operator", password = "Test-Only-Password!492" })).StatusCode);
    }

    [Fact]
    public async Task RegeneratedRecoveryCodesReplaceOldCodesAndKeepOnlyCurrentSession()
    {
        await using var app = await TestApplication.StartAsync(); var (_, codes, _) = await MfaTests.EnrollAsync(app);
        using var other = app.NewClient(); await MfaTests.LoginAsync(other);
        (await other.PostAsJsonAsync("/api/v1/auth/mfa/recovery", new { code = codes[0] })).EnsureSuccessStatusCode();
        var result = await app.Client.PostAsJsonAsync("/api/v1/auth/recovery-codes", new { }); result.EnsureSuccessStatusCode();
        Assert.Equal(10, (await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recoveryCodes").GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await app.Client.GetAsync("/api/v1/system/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/v1/system/status")).StatusCode);
        await MfaTests.LoginAsync(other);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.PostAsJsonAsync("/api/v1/auth/mfa/recovery", new { code = codes[1] })).StatusCode);
    }

    [Fact]
    public async Task ForgedForwardedAddressesFromUntrustedPeerCannotBypassLoginRateLimit()
    {
        await using var app = await TestApplication.StartAsync();
        for (int attempt = 0; attempt < 21; attempt++)
        {
            var context = await app.App.GetTestServer().SendAsync(context =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.5");
                context.Request.Method = "POST"; context.Request.Scheme = "https"; context.Request.Host = new("localhost");
                context.Request.Path = "/api/v1/auth/login"; context.Request.Headers["X-Forwarded-For"] = $"203.0.113.{attempt + 1}";
            });
            Assert.Equal(attempt < 20 ? 400 : 429, context.Response.StatusCode);
            if (attempt == 20) Assert.Equal("60", context.Response.Headers.RetryAfter);
        }
    }
}
