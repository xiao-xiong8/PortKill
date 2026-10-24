using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Security;

namespace PortKiller.UpdateAdmin.Tests;

public sealed class PasswordAuthenticationTests
{
    [Fact]
    public async Task FreshDatabaseHasNoDefaultAccountsAndBootstrapRunsOnce()
    {
        await using var app = await TestApplication.StartAsync();
        using var scope = app.App.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>().Users.AnyAsync());
        await app.SeedAdminAsync();
        var failure = await Assert.ThrowsAsync<AdminFailure>(() => app.SeedAdminAsync("second"));
        Assert.Equal("bootstrap_exists", failure.Code);
    }

    [Fact]
    public async Task PasswordLoginCannotBypassMfa()
    {
        await using var app = await TestApplication.StartAsync();
        await app.SeedAdminAsync();
        var response = await app.Client.PostAsJsonAsync("/api/v1/auth/login", new { userName = "operator", password = "Test-Only-Password!492" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("enrollMfa", json.GetProperty("next").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Client.GetAsync("/api/v1/system/status")).StatusCode);
        string cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownAccountAndWrongPasswordHaveTheSamePublicError()
    {
        await using var app = await TestApplication.StartAsync();
        await app.SeedAdminAsync();
        foreach (string name in new[] { "operator", "unknown" })
        {
            var response = await app.Client.PostAsJsonAsync("/api/v1/auth/login", new { userName = name, password = "incorrect" });
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("invalid_credentials", json.GetProperty("code").GetString());
            Assert.Equal("账号或凭据无效。", json.GetProperty("detail").GetString());
        }
    }

    [Fact]
    public async Task ConcurrentBootstrapCreatesOnlyOneAdministrator()
    {
        await using var app = await TestApplication.StartAsync();
        async Task<bool> Attempt(string name)
        {
            try { await app.SeedAdminAsync(name); return true; }
            catch (AdminFailure error) when (error.Code == "bootstrap_exists") { return false; }
        }
        var results = await Task.WhenAll(Task.Run(() => Attempt("first-operator")), Task.Run(() => Attempt("second-operator")));
        Assert.Single(results.Where(created => created));
        using var scope = app.App.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>().Users.CountAsync());
    }
}
