using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Persistence.Entities;

namespace PortKiller.UpdateAdmin.Tests;

public sealed class CurrentReleaseTests
{
    [Fact]
    public async Task EmptySourceAndExternalCliReleaseHaveHonestEmptyObservationFields()
    {
        await using var fixture = await PublishFixture.StartAsync();
        Assert.Equal(JsonValueKind.Null, (await fixture.App.Client.GetFromJsonAsync<JsonElement>("/api/v1/releases/current"))
            .GetProperty("release").ValueKind);
        Assert.True((await fixture.ActivateOutsideApiAsync(fixture.Sign(1), "none")).Succeeded);
        // No reachable public host is needed for this local, read-only query.
        var current = await PublicVerificationTests.CurrentAsync(fixture);
        Assert.Equal(1, current.GetProperty("sequence").GetInt64());
        Assert.Equal(JsonValueKind.Null, current.GetProperty("activationReceipt").ValueKind);
        Assert.Equal(JsonValueKind.Null, current.GetProperty("downloadCheck").ValueKind);
        Assert.Empty(Directory.GetFiles(fixture.Options.ReleaseRoot, "*.tmp"));
    }

    [Theory]
    [InlineData(30, "healthy")]
    [InlineData(10, "expiring")]
    [InlineData(3, "critical")]
    [InlineData(-1, "expired")]
    public async Task TrustedExpiryIsReportedEvenWhenTheManifestHasExpired(int days, string status)
    {
        await using var fixture = await PublishFixture.StartAsync();
        byte[] manifest = fixture.Sign(1, expires: fixture.App.Clock.GetUtcNow().AddDays(days),
            published: fixture.App.Clock.GetUtcNow().AddDays(-31));
        await File.WriteAllBytesAsync(Path.Combine(fixture.Options.ReleaseRoot, "manifest.json"), manifest);
        var current = await PublicVerificationTests.CurrentAsync(fixture);
        Assert.Equal(status, current.GetProperty("expiryStatus").GetString());
        Assert.InRange(current.GetProperty("daysRemaining").GetDouble(), days - 0.01, days + 0.01);
    }

    [Fact]
    public async Task CorruptManifestAndUnavailableConfigReturnSanitizedUnavailableSource()
    {
        await using var fixture = await PublishFixture.StartAsync();
        string path = Path.Combine(fixture.Options.ReleaseRoot, "manifest.json");
        foreach (byte[] invalid in new byte[][] { [1, 2, 3], new byte[65_537] })
        {
            await File.WriteAllBytesAsync(path, invalid);
            using var response = await fixture.App.Client.GetAsync("/api/v1/releases/current");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            string body = await response.Content.ReadAsStringAsync();
            Assert.Contains("release_source_unavailable", body);
            Assert.DoesNotContain(fixture.App.Root, body);
        }
        File.Delete(path);
        File.Delete(fixture.Options.VerificationConfigPath);
        using var missingConfig = await fixture.App.Client.GetAsync("/api/v1/releases/current");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, missingConfig.StatusCode);
    }

    [Fact]
    public async Task HistoricalSuccessIsNotRelabeledAsALiveHealthProbeAndDoesNotRequireReceiptWriteAccess()
    {
        await using var distribution = await PublicDistributionFixture.StartAsync();
        PublishFixture fixture = distribution.Publication;
        await distribution.PublishAsync();
        Assert.True(await fixture.ExecutePublicCheckAsync());
        var first = await PublicVerificationTests.CurrentAsync(fixture);
        long at = first.GetProperty("downloadCheck").GetProperty("lastCompletedUtcMs").GetInt64();
        int hits = distribution.ManifestHits;
        distribution.Mode = "unavailable";
        // The API consumes the recorded Worker receipt, not its private writable state directory.
        string statePath = Path.Combine(fixture.App.Root, ".public-update-state");
        using var blocked = new FileStream(Path.Combine(statePath, "lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        File.Delete(Path.Combine(statePath, "receipt.json"));
        fixture.App.Clock.Advance(TimeSpan.FromMinutes(2));
        var second = await fixture.App.Client.GetFromJsonAsync<JsonElement>("/api/v1/releases/current");
        Assert.True(second.GetProperty("observedUtcMs").GetInt64() > at);
        Assert.Equal(at, second.GetProperty("release").GetProperty("downloadCheck").GetProperty("lastCompletedUtcMs").GetInt64());
        Assert.True(second.GetProperty("release").GetProperty("downloadCheck").GetProperty("lastSucceeded").GetBoolean());
        Assert.Equal(first.GetProperty("activationReceipt").GetProperty("sha256").GetString(),
            second.GetProperty("release").GetProperty("activationReceipt").GetProperty("sha256").GetString());
        Assert.Equal(hits, distribution.ManifestHits);
        Assert.False(File.Exists(Path.Combine(statePath, "receipt.json")));
    }

    [Theory]
    [InlineData("Viewer")]
    [InlineData("Uploader")]
    [InlineData("Publisher")]
    [InlineData("Administrator")]
    public async Task CurrentReleaseAllowsAuthenticatedReadersAndNeverAllowsAnonymousAccess(string role)
    {
        await using var fixture = await PublishFixture.StartAsync();
        await using var scope = fixture.App.App.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AdminUser>>();
        var user = (await users.FindByNameAsync("operator"))!;
        Assert.True((await users.RemoveFromRoleAsync(user, "Administrator")).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        using var authenticated = await fixture.App.Client.GetAsync("/api/v1/releases/current");
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
        Assert.True(authenticated.Headers.CacheControl?.NoStore);
        using var client = fixture.App.NewClient();
        using var anonymous = await client.GetAsync("/api/v1/releases/current");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }
}
