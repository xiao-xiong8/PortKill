using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortKiller.UpdateManagement.Contracts;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Persistence.Entities;

namespace PortKiller.UpdateAdmin.Tests;

public sealed class PublicCheckAuthorizationTests
{
    [Fact]
    public async Task RetryRequiresAuthenticationCsrfAndRecentMfaWithoutChangingTheOriginalJob()
    {
        await using var distribution = await PublicDistributionFixture.StartAsync();
        PublishFixture fixture = distribution.Publication;
        string id = await FailedJobAsync(distribution);
        string url = PublicVerificationTests.RetryUrl(id);
        using var anonymous = fixture.App.NewClient();
        using (var response = await anonymous.PostAsJsonAsync(url, new { }))
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var cookies = new CookieContainer();
        foreach (string cookie in fixture.App.DefaultCookieHeader.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            cookies.SetCookies(new Uri("https://localhost"), cookie + "; Path=/; Secure");
        using var noCsrf = fixture.App.NewClient(cookies, autoCsrf: false);
        using (var response = await noCsrf.PostAsJsonAsync(url, new { }))
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        fixture.App.Clock.Advance(TimeSpan.FromMinutes(6));
        using (var response = await fixture.App.Client.PostAsJsonAsync(url, new { }))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(PublishJobState.ActiveUnverified, (await PublicVerificationTests.ReadJobAsync(fixture, id)).State);
        Assert.Equal(1, (await PublicVerificationTests.ReadJobAsync(fixture, id)).PublicCheckAttempts);
    }

    [Theory]
    [InlineData("Viewer", false)]
    [InlineData("Uploader", false)]
    [InlineData("Publisher", true)]
    [InlineData("Administrator", true)]
    public async Task OnlyPublishersAndAdministratorsCanRetryTheOriginalJob(string role, bool allowed)
    {
        await using var distribution = await PublicDistributionFixture.StartAsync();
        PublishFixture fixture = distribution.Publication;
        string id = await FailedJobAsync(distribution);
        await using var scope = fixture.App.App.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AdminUser>>();
        var user = (await users.FindByNameAsync("operator"))!;
        Assert.True((await users.RemoveFromRoleAsync(user, "Administrator")).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        using var response = await fixture.App.Client.PostAsJsonAsync(PublicVerificationTests.RetryUrl(id), new { });
        Assert.Equal(allowed ? HttpStatusCode.Accepted : HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnUnrelatedPublisherCannotReadOrRetrySomeoneElsesJob()
    {
        await using var distribution = await PublicDistributionFixture.StartAsync();
        PublishFixture fixture = distribution.Publication;
        string id = await FailedJobAsync(distribution);
        using (var created = await fixture.App.Client.PostAsJsonAsync("/api/v1/accounts/", new
            { userName = "other-publisher", password = "Other-Publisher-Only!492", roles = new[] { "Publisher" } }))
            created.EnsureSuccessStatusCode();
        using var other = fixture.App.NewClient();
        using (var login = await other.PostAsJsonAsync("/api/v1/auth/login", new
            { userName = "other-publisher", password = "Other-Publisher-Only!492" })) login.EnsureSuccessStatusCode();
        using var enrollment = await other.PostAsJsonAsync("/api/v1/auth/mfa/enrollment/start", new { });
        enrollment.EnsureSuccessStatusCode();
        string key = (await enrollment.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sharedKey").GetString()!;
        using (var confirmed = await other.PostAsJsonAsync("/api/v1/auth/mfa/enrollment/confirm", new { code = MfaTests.Code(key) }))
            confirmed.EnsureSuccessStatusCode();
        using (var acknowledged = await other.PostAsJsonAsync("/api/v1/auth/mfa/enrollment/acknowledge", new { acknowledged = true }))
            acknowledged.EnsureSuccessStatusCode();
        using (var read = await other.GetAsync($"/api/v1/publish-jobs/{id}")) Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        using var retry = await other.PostAsJsonAsync(PublicVerificationTests.RetryUrl(id), new { });
        Assert.Equal(HttpStatusCode.NotFound, retry.StatusCode);
    }

    [Fact]
    public async Task ArbitraryUrlOverridesAreRejectedAndQueuedActivationCannotBeReclassifiedAsACheck()
    {
        await using var distribution = await PublicDistributionFixture.StartAsync();
        PublishFixture fixture = distribution.Publication;
        string id = await FailedJobAsync(distribution);
        using (var overrideRequest = await fixture.App.Client.PostAsJsonAsync(PublicVerificationTests.RetryUrl(id),
            new { manifestUrl = "http://127.0.0.1:1/override", packageUrl = "http://127.0.0.1:1/override" }))
            Assert.Equal(HttpStatusCode.BadRequest, overrideRequest.StatusCode);
        Assert.Equal(PublishJobState.ActiveUnverified, (await PublicVerificationTests.ReadJobAsync(fixture, id)).State);
        var upload = await fixture.UploadAsync(fixture.Sign(2));
        string queued = await fixture.ConfirmAsync(upload.DraftId);
        using var invalidState = await fixture.App.Client.PostAsJsonAsync(PublicVerificationTests.RetryUrl(queued), new { });
        Assert.Equal(HttpStatusCode.Conflict, invalidState.StatusCode);
        Assert.Equal("public_check_not_retryable", (await invalidState.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(PublishJobState.Queued, (await PublicVerificationTests.ReadJobAsync(fixture, queued)).State);
        Assert.Equal(1, distribution.ManifestHits);
    }

    [Fact]
    public async Task RevokingASessionPreventsTheRetryEndpointFromEnqueuingWork()
    {
        await using var distribution = await PublicDistributionFixture.StartAsync();
        PublishFixture fixture = distribution.Publication;
        string id = await FailedJobAsync(distribution);
        await using (var scope = fixture.App.App.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>().AdminSessions.ExecuteUpdateAsync(update => update
                .SetProperty(session => session.RevokedUtcMs, fixture.App.Clock.GetUtcNow().ToUnixTimeMilliseconds()));
        using var response = await fixture.App.Client.PostAsJsonAsync(PublicVerificationTests.RetryUrl(id), new { });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(PublishJobState.ActiveUnverified, (await PublicVerificationTests.ReadJobAsync(fixture, id)).State);
    }

    private static async Task<string> FailedJobAsync(PublicDistributionFixture distribution)
    {
        string id = await distribution.PublishAsync();
        distribution.Mode = "missing";
        Assert.True(await distribution.Publication.ExecutePublicCheckAsync());
        return id;
    }
}
