using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Persistence.Entities;
using PortKiller.UpdateManagement.Security;

namespace PortKiller.UpdateAdmin.Tests;

public sealed class AuditTests
{
    [Fact]
    public async Task AuthenticationAndDenialsHaveCorrelatedAuditWithoutSecretsInAuditOrLogs()
    {
        await using var app = await TestApplication.StartAsync(); var (key, codes, usedCode) = await MfaTests.EnrollAsync(app);
        var failed = await app.Client.PostAsJsonAsync("/api/v1/auth/reauthenticate", new { password = "Rejected-Secret!982", code = usedCode });
        Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        string correlation = (await failed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("correlationId").GetString()!;
        using var scope = app.App.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
        var events = await db.AuditEvents.AsNoTracking().ToArrayAsync();
        Assert.Contains(events, item => item.Action == "account.bootstrap");
        Assert.Contains(events, item => item.Action == "mfa.recovery_acknowledged" && item.Result == "success");
        Assert.Contains(events, item => item.Action == "http.Reauthenticate" && item.Result == "status_401" && item.CorrelationId == correlation);
        string serialized = JsonSerializer.Serialize(events) + app.Logs.Text;
        foreach (string secret in codes.Concat([key, usedCode, "Rejected-Secret!982", "Test-Only-Password!492"]))
            Assert.DoesNotContain(secret, serialized, StringComparison.Ordinal);
        foreach (string cookie in app.DefaultCookieHeader.Split(';'))
        {
            string value = cookie[(cookie.IndexOf('=') + 1)..].Trim();
            if (value.Length > 16) Assert.DoesNotContain(value, serialized, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AuditWriteFailureRollsBackSecurityChangeAndSessionRevocation()
    {
        await using var app = await TestApplication.StartAsync(); await MfaTests.EnrollAsync(app);
        var created = await app.Client.PostAsJsonAsync("/api/v1/accounts/", new { userName = "viewer", password = "Viewer-Only-Password!932", roles = new[] { "Viewer" } });
        created.EnsureSuccessStatusCode(); string id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        using var viewer = app.NewClient();
        (await viewer.PostAsJsonAsync("/api/v1/auth/login", new { userName = "viewer", password = "Viewer-Only-Password!932" })).EnsureSuccessStatusCode();
        using (var scope = app.App.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>().Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER reject_audit BEFORE INSERT ON AuditEvents WHEN NEW.Action = 'account.roles_changed' BEGIN SELECT RAISE(ABORT,'audit unavailable'); END;");
        Assert.Equal(HttpStatusCode.InternalServerError, (await app.Client.PutAsJsonAsync($"/api/v1/accounts/{id}/roles", new { roles = new[] { "Uploader" } })).StatusCode);
        using (var scope = app.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
            string role = await db.UserRoles.Where(x => x.UserId == id).Join(db.Roles, x => x.RoleId, x => x.Id, (_, r) => r.Name!).SingleAsync();
            Assert.Equal("Viewer", role);
            Assert.Null((await db.AdminSessions.SingleAsync(x => x.UserId == id)).RevokedUtcMs);
            Assert.Contains(await db.AuditEvents.ToListAsync(), item => item.Action == "http.ChangeAccountRoles" && item.Result == "status_500");
        }
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task AuditQueryRequiresPermissionAndHasBoundedPaginationAndFilters()
    {
        await using var app = await TestApplication.StartAsync(); await MfaTests.EnrollAsync(app);
        var page = await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/audit?page=1&pageSize=2&action=session.created");
        Assert.Equal(2, page.GetProperty("items").GetArrayLength());
        Assert.True(page.GetProperty("total").GetInt64() >= 2);
        Assert.All(page.GetProperty("items").EnumerateArray(), item => Assert.Equal("session.created", item.GetProperty("action").GetString()));
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.GetAsync("/api/v1/audit?pageSize=101")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.GetAsync("/api/v1/audit?action=invalid%20value")).StatusCode);
        using var scope = app.App.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<AdminUser>>();
        var user = (await users.FindByNameAsync("operator"))!;
        Assert.True((await users.RemoveFromRoleAsync(user, "Administrator")).Succeeded); Assert.True((await users.AddToRoleAsync(user, "Viewer")).Succeeded);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Client.GetAsync("/api/v1/audit")).StatusCode);
    }

    [Fact]
    public async Task RetentionRemovesExpiredAndExcessRowsInBoundedBatches()
    {
        await using var app = await TestApplication.StartAsync();
        using var scope = app.App.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<AdminOptions>(); options.AuditMaximumRows = 100; options.AuditRetentionDays = 7;
        var db = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
        long now = app.Clock.GetUtcNow().ToUnixTimeMilliseconds();
        db.AuditEvents.AddRange(Enumerable.Range(0, 105).Select(index => new AuditEvent { Action = "test.current", Result = "success", CorrelationId = "fixture", CreatedUtcMs = now + index }));
        db.AuditEvents.Add(new AuditEvent { Action = "test.expired", Result = "failure", CorrelationId = "fixture", CreatedUtcMs = now - (long)TimeSpan.FromDays(8).TotalMilliseconds });
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<AdminAuditService>().PruneAsync(CancellationToken.None);
        Assert.Equal(100, await db.AuditEvents.CountAsync());
        Assert.False(await db.AuditEvents.AnyAsync(x => x.Action == "test.expired"));
        Assert.Equal(now + 5, await db.AuditEvents.MinAsync(x => x.CreatedUtcMs));
    }
}
