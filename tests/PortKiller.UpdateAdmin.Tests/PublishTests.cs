using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortKiller.UpdateManagement.Contracts;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Persistence.Entities;
using PortKiller.UpdateManagement.Security;

namespace PortKiller.UpdateAdmin.Tests;

public sealed class PublishTests
{
    [Fact]
    public async Task ChallengeBindsVerifiedArtifactAndConfirmationIsIdempotent()
    {
        await using var app = await TestApplication.StartAsync();
        await MfaTests.EnrollAsync(app);
        string draftId = await SeedReadyDraftAsync(app);

        using var challenge = await app.Client.PostAsync($"/api/v1/releases/{draftId}/publish/challenge", JsonContent.Create(new { }));
        Assert.Equal(HttpStatusCode.OK, challenge.StatusCode);
        var challengeBody = await challenge.Content.ReadFromJsonAsync<JsonElement>();
        string challengeId = challengeBody.GetProperty("id").GetString()!;
        Assert.Equal(draftId, challengeBody.GetProperty("draftId").GetString());
        Assert.Equal("none", challengeBody.GetProperty("expectedCurrentManifestSha256").GetString());

        var request = new PublishConfirmationRequest(challengeId, "publish-test-1");
        using var first = await app.Client.PostAsJsonAsync($"/api/v1/releases/{draftId}/publish", request);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        string jobId = firstBody.GetProperty("id").GetString()!;

        using var repeated = await app.Client.PostAsJsonAsync($"/api/v1/releases/{draftId}/publish", request);
        Assert.Equal(HttpStatusCode.Accepted, repeated.StatusCode);
        Assert.Equal(jobId, (await repeated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString());

        using var scope = app.App.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
        Assert.Equal(1, await database.ReleaseJobs.CountAsync(job => job.Kind == "publish"));
        Assert.Equal(1, await database.PublishApprovals.CountAsync(approval => approval.ConsumedUtcMs != null));
        Assert.Equal("publish_queued", await database.JobEvents.Where(item => item.JobId == jobId).Select(item => item.Message).SingleAsync());
    }

    [Fact]
    public async Task ChangedArtifactExpiredChallengeAndBaselineAreRejectedDeterministically()
    {
        await using var app = await TestApplication.StartAsync();
        await MfaTests.EnrollAsync(app);
        string draftId = await SeedReadyDraftAsync(app);

        string challengeId = await CreateChallengeAsync(app, draftId);
        await using (var scope = app.App.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
            var draft = await database.ReleaseDrafts.SingleAsync(item => item.Id == draftId);
            draft.PackageSha256 = new string('B', 64);
            await database.SaveChangesAsync();
        }
        using (var changed = await app.Client.PostAsJsonAsync($"/api/v1/releases/{draftId}/publish",
            new PublishConfirmationRequest(challengeId, "changed-artifact")))
        {
            Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
            Assert.Equal("publish_artifact_changed", (await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        string secondDraft = await SeedReadyDraftAsync(app, "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB");
        string secondChallenge = await CreateChallengeAsync(app, secondDraft);
        await using (var scope = app.App.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
            var upload = new UploadSession { OwnerId = await UserIdAsync(scope.ServiceProvider), State = UploadState.Uploaded };
            var oldDraft = new ReleaseDraft
            {
                OwnerId = upload.OwnerId, UploadId = upload.Id, ValidationState = ValidationState.ReadyForApproval,
                Version = "0.1.0", Sequence = 1, ManifestSha256 = new string('A', 64), PackageSha256 = new string('C', 64),
                ManifestExpiresUtcMs = app.Clock.GetUtcNow().AddDays(1).ToUnixTimeMilliseconds(), CreatedUtcMs = app.Clock.GetUtcNow().ToUnixTimeMilliseconds()
            };
            database.UploadSessions.Add(upload); database.ReleaseDrafts.Add(oldDraft);
            database.ReleaseJobs.Add(new ReleaseJob
            {
                DraftId = oldDraft.Id, RequestedById = upload.OwnerId, Kind = "publish", Application = oldDraft.Application,
                Channel = oldDraft.Channel, State = PublishJobState.Completed, CreatedUtcMs = app.Clock.GetUtcNow().ToUnixTimeMilliseconds()
            });
            await database.SaveChangesAsync();
        }
        using (var stale = await app.Client.PostAsJsonAsync($"/api/v1/releases/{secondDraft}/publish",
            new PublishConfirmationRequest(secondChallenge, "stale-baseline")))
        {
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            Assert.Equal("publish_baseline_changed", (await stale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        string thirdDraft = await SeedReadyDraftAsync(app, "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC");
        string thirdChallenge = await CreateChallengeAsync(app, thirdDraft);
        await using (var scope = app.App.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
            var approval = await database.PublishApprovals.SingleAsync(item => item.Id == thirdChallenge);
            approval.ExpiresUtcMs = app.Clock.GetUtcNow().AddMilliseconds(-1).ToUnixTimeMilliseconds();
            await database.SaveChangesAsync();
        }
        using var expired = await app.Client.PostAsJsonAsync($"/api/v1/releases/{thirdDraft}/publish",
            new PublishConfirmationRequest(thirdChallenge, "expired-challenge"));
        Assert.Equal(HttpStatusCode.Conflict, expired.StatusCode);
        Assert.Equal("publish_challenge_expired", (await expired.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task ConcurrentSameKeyConfirmationReturnsOneDurableJob()
    {
        await using var app = await TestApplication.StartAsync();
        await MfaTests.EnrollAsync(app);
        string draftId = await SeedReadyDraftAsync(app);
        string challengeId = await CreateChallengeAsync(app, draftId);
        ClaimsPrincipal principal;
        await using (var scope = app.App.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AdminUser>>();
            var user = (await users.FindByNameAsync("operator"))!;
            var database = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
            var session = await database.AdminSessions.SingleAsync(item => item.UserId == user.Id && item.Phase == SessionPhase.Full);
            principal = await scope.ServiceProvider.GetRequiredService<AdminSessionService>().PrincipalAsync(new SessionTicket(user, session));
        }

        async Task<PublishJobResponse> ConfirmAsync()
        {
            await using var scope = app.App.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<PublishManagementService>().ConfirmAsync(principal, draftId,
                new PublishConfirmationRequest(challengeId, "concurrent-key"), null, CancellationToken.None);
        }

        var results = await Task.WhenAll(ConfirmAsync(), ConfirmAsync());
        Assert.Equal(results[0].Id, results[1].Id);
        using var verify = app.App.Services.CreateScope();
        Assert.Equal(1, await verify.ServiceProvider.GetRequiredService<UpdateAdminDbContext>().ReleaseJobs.CountAsync(item => item.Kind == "publish"));
    }

    private static async Task<string> CreateChallengeAsync(TestApplication app, string draftId)
    {
        using var response = await app.Client.PostAsync($"/api/v1/releases/{draftId}/publish/challenge", JsonContent.Create(new { }));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task<string> SeedReadyDraftAsync(TestApplication app, string? packageSha = null)
    {
        await using var scope = app.App.Services.CreateAsyncScope();
        string ownerId = await UserIdAsync(scope.ServiceProvider);
        var database = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
        var upload = new UploadSession { OwnerId = ownerId, State = UploadState.Uploaded, CreatedUtcMs = app.Clock.GetUtcNow().ToUnixTimeMilliseconds() };
        var draft = new ReleaseDraft
        {
            OwnerId = ownerId, UploadId = upload.Id, ValidationState = ValidationState.ReadyForApproval,
            Version = "0.2.0", Sequence = 2, ManifestSha256 = new string('D', 64), PackageSha256 = packageSha ?? new string('E', 64),
            ManifestExpiresUtcMs = app.Clock.GetUtcNow().AddDays(1).ToUnixTimeMilliseconds(), CreatedUtcMs = app.Clock.GetUtcNow().ToUnixTimeMilliseconds()
        };
        database.UploadSessions.Add(upload); database.ReleaseDrafts.Add(draft); await database.SaveChangesAsync();
        return draft.Id;
    }

    private static async Task<string> UserIdAsync(IServiceProvider services)
        => (await services.GetRequiredService<UserManager<AdminUser>>().FindByNameAsync("operator"))!.Id;
}
