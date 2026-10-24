using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Security;

namespace PortKiller.UpdateAdmin.Tests;

public sealed class PublishActivationTests
{
    [Fact]
    public async Task RealUploadValidationApprovalAndToolActivationPreserveTheExactManifest()
    {
        await using var fixture = await PublishFixture.StartAsync();
        byte[] manifest = fixture.Sign(1);
        var upload = await fixture.UploadAsync(manifest);
        string jobId = await fixture.ConfirmAsync(upload.DraftId);
        Assert.True(await fixture.ExecuteAsync());
        var job = await fixture.App.Client.GetFromJsonAsync<JsonElement>($"/api/v1/publish-jobs/{jobId}");
        Assert.Equal("manifestActivated", job.GetProperty("state").GetString());
        Assert.Equal(manifest, await File.ReadAllBytesAsync(Path.Combine(fixture.Options.ReleaseRoot, "manifest.json")));
        Assert.True((await fixture.Tool.InspectAsync(default)).Succeeded);
        Assert.False(await fixture.ExecuteAsync());
    }

    [Fact]
    public async Task AReplacedArchiveAndDraftMetadataCannotChangeTheApprovedPublication()
    {
        await using var fixture = await PublishFixture.StartAsync();
        var upload = await fixture.UploadAsync(fixture.Sign(1));
        string jobId = await fixture.ConfirmAsync(upload.DraftId);
        await using (var scope = fixture.App.App.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
            var draft = await database.ReleaseDrafts.SingleAsync(x => x.Id == upload.DraftId);
            byte[] replacement = fixture.Sign(2);
            await File.WriteAllBytesAsync(Path.Combine(fixture.App.Root, "archive", draft.ArchiveId, "manifest.envelope"), replacement);
            draft.ManifestSha256 = Convert.ToHexString(SHA256.HashData(replacement)); draft.Sequence = 2;
            await database.SaveChangesAsync();
        }
        Assert.True(await fixture.ExecuteAsync());
        var job = await fixture.App.Client.GetFromJsonAsync<JsonElement>($"/api/v1/publish-jobs/{jobId}");
        Assert.Equal("conflict", job.GetProperty("state").GetString());
        Assert.Equal(1, job.GetProperty("sequence").GetInt64());
        Assert.False(File.Exists(Path.Combine(fixture.Options.ReleaseRoot, "manifest.json")));
    }

    [Fact]
    public async Task ExternalPublisherWinsCasAndTheQueuedJobCannotOverwriteIt()
    {
        await using var fixture = await PublishFixture.StartAsync();
        var upload = await fixture.UploadAsync(fixture.Sign(1));
        string jobId = await fixture.ConfirmAsync(upload.DraftId);
        byte[] external = fixture.Sign(2);
        Assert.True((await fixture.ActivateOutsideApiAsync(external, "none")).Succeeded);
        Assert.True(await fixture.ExecuteAsync());
        var job = await fixture.App.Client.GetFromJsonAsync<JsonElement>($"/api/v1/publish-jobs/{jobId}");
        Assert.Equal("conflict", job.GetProperty("state").GetString());
        Assert.Equal(external, await File.ReadAllBytesAsync(Path.Combine(fixture.Options.ReleaseRoot, "manifest.json")));
    }

    [Fact]
    public async Task CompetingWorkersLeaseAtMostOnceAndTheOriginalToolLockBlocksActivation()
    {
        await using var fixture = await PublishFixture.StartAsync();
        var upload = await fixture.UploadAsync(fixture.Sign(1));
        await fixture.ConfirmAsync(upload.DraftId);
        await using (var first = fixture.App.App.Services.CreateAsyncScope())
        await using (var second = fixture.App.App.Services.CreateAsyncScope())
        {
            var claims = await Task.WhenAll(first.ServiceProvider.GetRequiredService<PublishTaskQueue>().ClaimAsync("one", default),
                second.ServiceProvider.GetRequiredService<PublishTaskQueue>().ClaimAsync("two", default));
            var claimed = Assert.Single(claims, value => value != null)!;
            string owner = claims[0] != null ? "one" : "two";
            Assert.True(await first.ServiceProvider.GetRequiredService<PublishTaskQueue>().ReleaseAsync(claimed.Id, owner, "io_failure", default));
        }
        string statePath = Path.Combine(fixture.App.Root, ".public-update-state"); Directory.CreateDirectory(statePath);
        using (var sharedLock = new FileStream(Path.Combine(statePath, "lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(await fixture.ExecuteAsync());
            Assert.False(File.Exists(Path.Combine(fixture.Options.ReleaseRoot, "manifest.json")));
        }
        Assert.True(await fixture.ExecuteAsync());
        Assert.True(File.Exists(Path.Combine(fixture.Options.ReleaseRoot, "manifest.json")));
    }

    [Fact]
    public async Task PublishJobStatusIsHiddenFromUnrelatedUsers()
    {
        await using var fixture = await PublishFixture.StartAsync();
        var upload = await fixture.UploadAsync(fixture.Sign(1));
        string jobId = await fixture.ConfirmAsync(upload.DraftId);
        var outsider = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString("N"))], "test"));
        await using var scope = fixture.App.App.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<PublishManagementService>();

        var failure = await Assert.ThrowsAsync<AdminFailure>(() => service.GetJobAsync(outsider, jobId, default));
        Assert.Equal("resource_not_found", failure.Code);
    }

    [Fact]
    public async Task CorruptCurrentManifestIsReportedAsUnavailableReleaseSource()
    {
        await using var fixture = await PublishFixture.StartAsync();
        var upload = await fixture.UploadAsync(fixture.Sign(1));
        await File.WriteAllBytesAsync(Path.Combine(fixture.Options.ReleaseRoot, "manifest.json"), [1, 2, 3]);

        using var response = await fixture.App.Client.PostAsJsonAsync(
            $"/api/v1/releases/{upload.DraftId}/publish/challenge", new { });
        Assert.Equal(503, (int)response.StatusCode);
        Assert.Equal("release_source_unavailable",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }
}
