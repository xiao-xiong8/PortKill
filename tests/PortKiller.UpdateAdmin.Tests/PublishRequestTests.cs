using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortKiller.UpdateManagement.Persistence;

namespace PortKiller.UpdateAdmin.Tests;
public sealed class PublishRequestTests
{
    [Fact]
    public async Task ReadOnlyConfirmationLookupSurvivesStrongAuthenticationExpiryWithoutCreatingAJob()
    {
        await using var fixture = await PublishFixture.StartAsync();
        var upload = await fixture.UploadAsync(fixture.Sign(1)); string id = await fixture.ConfirmAsync(upload.DraftId);
        using var scope = fixture.App.App.Services.CreateScope(); var database = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
        string key = await database.IdempotencyRequests.Select(row => row.Key).SingleAsync();
        fixture.App.Clock.Advance(TimeSpan.FromMinutes(6));
        for (int index = 0; index < 2; index++)
        {
            var result = await fixture.App.Client.GetFromJsonAsync<JsonElement>("/api/v1/publish-requests/" + key);
            Assert.Equal(id, result.GetProperty("id").GetString());
        }
        Assert.Equal(1, await database.ReleaseJobs.CountAsync(row => row.Kind == "publish"));
    }
    [Fact]
    public async Task EvenAdministratorCannotDiscoverAnotherActorsConfirmationKey()
    {
        await using var fixture = await PublishFixture.StartAsync();
        var upload = await fixture.UploadAsync(fixture.Sign(1)); await fixture.ConfirmAsync(upload.DraftId);
        string foreign = await JobEventTests.CreateTaskAsync(fixture.App, foreign: true);
        using var scope = fixture.App.App.Services.CreateScope(); var database = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
        var record = await database.IdempotencyRequests.SingleAsync(); string key = record.Key;
        string owner = await database.ReleaseJobs.Where(row => row.Id == foreign).Select(row => row.RequestedById).SingleAsync();
        await database.IdempotencyRequests.Where(row => row.Key == key).ExecuteUpdateAsync(update => update.SetProperty(row => row.UserId, owner));
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.App.Client.GetAsync("/api/v1/publish-requests/" + key)).StatusCode);
    }
    [Fact]
    public async Task MissingAndAnonymousRequestsHaveNoPublishSideEffects()
    {
        await using var app = await JobEventTests.StartAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/api/v1/publish-requests/missing")).StatusCode);
        using var anonymous = app.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/publish-requests/missing")).StatusCode);
    }
}
