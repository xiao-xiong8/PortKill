using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortKiller.UpdateManagement.Persistence;

namespace PortKiller.UpdateAdmin.Tests;

public sealed class MfaTests
{
    internal static string Code(string key, int offset = 0)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>(); int buffer = 0, bits = 0;
        foreach (char character in key)
        {
            int value = alphabet.IndexOf(character); if (value < 0) continue;
            buffer = (buffer << 5) | value; bits += 5;
            if (bits >= 8) { bits -= 8; bytes.Add((byte)(buffer >> bits)); }
        }
        byte[] counter = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(counter, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 + offset);
        byte[] hash = HMACSHA1.HashData(bytes.ToArray(), counter);
        int start = hash[^1] & 15;
        int number = BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(start, 4)) & int.MaxValue;
        return (number % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static async Task<(string Key, string[] RecoveryCodes, string UsedCode)> EnrollAsync(TestApplication app)
    {
        await app.SeedAdminAsync();
        await LoginAsync(app.Client);
        var start = await app.Client.PostAsJsonAsync("/api/v1/auth/mfa/enrollment/start", new { });
        start.EnsureSuccessStatusCode();
        var data = await start.Content.ReadFromJsonAsync<JsonElement>();
        string key = data.GetProperty("sharedKey").GetString()!;
        string usedCode = Code(key);
        var confirm = await app.Client.PostAsJsonAsync("/api/v1/auth/mfa/enrollment/confirm", new { code = usedCode });
        confirm.EnsureSuccessStatusCode();
        var codes = (await confirm.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recoveryCodes")
            .EnumerateArray().Select(value => value.GetString()!).ToArray();
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Client.GetAsync("/api/v1/system/status")).StatusCode);
        var acknowledge = await app.Client.PostAsJsonAsync("/api/v1/auth/mfa/enrollment/acknowledge", new { acknowledged = true });
        acknowledge.EnsureSuccessStatusCode();
        return (key, codes, usedCode);
    }

    internal static async Task LoginAsync(HttpClient client)
        => (await client.PostAsJsonAsync("/api/v1/auth/login", new { userName = "operator", password = "Test-Only-Password!492" })).EnsureSuccessStatusCode();

    [Fact]
    public async Task EnrollmentAndRecoveryAcknowledgementAreRequiredBeforeFullAccess()
    {
        await using var app = await TestApplication.StartAsync();
        var enrolled = await EnrollAsync(app);
        Assert.Equal(10, enrolled.RecoveryCodes.Distinct().Count());
        Assert.Equal(HttpStatusCode.OK, (await app.Client.GetAsync("/api/v1/system/status")).StatusCode);
        var forbidden = await app.Client.PostAsJsonAsync("/api/v1/auth/mfa/enrollment/start", new { });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task EnrollmentSecretsAreNotCacheableAndWrongCodesDoNotAuthenticate()
    {
        await using var app = await TestApplication.StartAsync();
        await app.SeedAdminAsync(); await LoginAsync(app.Client);
        var start = await app.Client.PostAsJsonAsync("/api/v1/auth/mfa/enrollment/start", new { });
        Assert.True(start.Headers.CacheControl?.NoStore);
        var wrong = await app.Client.PostAsJsonAsync("/api/v1/auth/mfa/enrollment/confirm", new { code = "invalid" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Client.GetAsync("/api/v1/system/status")).StatusCode);
    }

    [Fact]
    public async Task OneAuthenticatorCodeCannotCompleteTwoConcurrentLogins()
    {
        await using var app = await TestApplication.StartAsync();
        var (key, _, _) = await EnrollAsync(app);
        using var first = app.NewClient(); using var second = app.NewClient();
        await LoginAsync(first); await LoginAsync(second);
        string code = Code(key, 1);
        var responses = await Task.WhenAll(first.PostAsJsonAsync("/api/v1/auth/mfa/verify", new { code }),
            second.PostAsJsonAsync("/api/v1/auth/mfa/verify", new { code }));
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Unauthorized);
        using var scope = app.App.Services.CreateScope();
        Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>().TotpConsumptions.CountAsync());
    }

    [Fact]
    public async Task RecoveryCodeIsSingleUseAndDoesNotGrantRecentStrongAuthentication()
    {
        await using var app = await TestApplication.StartAsync();
        var (_, codes, _) = await EnrollAsync(app);
        using var first = app.NewClient(); using var second = app.NewClient();
        await LoginAsync(first); await LoginAsync(second);
        Assert.Equal(HttpStatusCode.OK, (await first.PostAsJsonAsync("/api/v1/auth/mfa/recovery", new { code = codes[0] })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.PostAsJsonAsync("/api/v1/auth/mfa/recovery", new { code = codes[0] })).StatusCode);
        using var scope = app.App.Services.CreateScope();
        var session = await scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>().AdminSessions.SingleAsync(x => x.AuthenticationMethod == "recovery");
        Assert.Null(session.StrongAuthenticationUtcMs);
    }

    [Fact]
    public async Task ReauthenticationRejectsPreviouslyConsumedCode()
    {
        await using var app = await TestApplication.StartAsync();
        var (_, _, usedCode) = await EnrollAsync(app);
        var response = await app.Client.PostAsJsonAsync("/api/v1/auth/reauthenticate", new { password = "Test-Only-Password!492", code = usedCode });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("mfa_replayed", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task AcknowledgementCannotSkipAuthenticatorConfirmation()
    {
        await using var app = await TestApplication.StartAsync();
        await app.SeedAdminAsync(); await LoginAsync(app.Client);
        var response = await app.Client.PostAsJsonAsync("/api/v1/auth/mfa/enrollment/acknowledge", new { acknowledged = true });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Client.GetAsync("/api/v1/system/status")).StatusCode);
    }

    [Fact]
    public async Task RepeatedAuthenticatorFailuresPersistTheAccountLockout()
    {
        await using var app = await TestApplication.StartAsync();
        string userId = await app.SeedAdminAsync(); await LoginAsync(app.Client);
        var start = await app.Client.PostAsJsonAsync("/api/v1/auth/mfa/enrollment/start", new { });
        string key = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sharedKey").GetString()!;
        for (int attempt = 0; attempt < 5; attempt++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client.PostAsJsonAsync("/api/v1/auth/mfa/enrollment/confirm", new { code = "invalid" })).StatusCode);
        using var scope = app.App.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<PortKiller.UpdateManagement.Persistence.Entities.AdminUser>>();
        Assert.True(await users.IsLockedOutAsync((await users.FindByIdAsync(userId))!));
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client.PostAsJsonAsync("/api/v1/auth/mfa/enrollment/confirm", new { code = Code(key, 1) })).StatusCode);
    }
}
