using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortKiller.UpdateManagement.Contracts;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Security;

namespace PortKiller.UpdateAdmin.Tests;

public sealed class PublishAuditTests
{
    [Fact]
    public async Task TamperedApprovedArchiveCreatesADurableActorBoundSecurityAudit()
    {
        await using var fixture = await PublishFixture.StartAsync();
        var upload = await fixture.UploadAsync(fixture.Sign(901)); string id = await fixture.ConfirmAsync(upload.DraftId);
        await using var scope = fixture.App.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
        var draft = await db.ReleaseDrafts.AsNoTracking().SingleAsync(item => item.Id == upload.DraftId);
        await File.WriteAllBytesAsync(Path.Combine(fixture.App.Root, "archive", draft.ArchiveId, "package.zip"), [9, 8, 7]);
        Assert.True(await fixture.ExecuteAsync());
        var audit = await db.AuditEvents.AsNoTracking().SingleAsync(item => item.ResourceId == id && item.Action == "publish.conflict");
        Assert.Equal(draft.OwnerId, audit.ActorUserId); Assert.Equal("archive_changed", audit.Result);
        Assert.Equal("job:" + id, audit.CorrelationId);
        Assert.DoesNotContain(fixture.App.Root, JsonSerializer.Serialize(audit));
        await db.JobEvents.Where(item => item.JobId == id).ExecuteDeleteAsync();
        Assert.Equal(1, await db.AuditEvents.CountAsync(item => item.Id == audit.Id));
        Assert.False(File.Exists(Path.Combine(fixture.Options.ReleaseRoot, "manifest.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedAuditInsertRollsBackTheEventAndStateThenRecoversWithoutReactivation(bool committed)
    {
        await using var fixture = await PublishFixture.StartAsync(); byte[] manifest = fixture.Sign(902);
        var upload = await fixture.UploadAsync(manifest); string id = await fixture.ConfirmAsync(upload.DraftId);
        string action = committed ? "publish.manifest_activated" : "publish.conflict";
        await using (var scope = fixture.App.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
            if (!committed)
            {
                var draft = await db.ReleaseDrafts.SingleAsync(item => item.Id == upload.DraftId);
                await File.WriteAllBytesAsync(Path.Combine(fixture.App.Root, "archive", draft.ArchiveId, "package.zip"), [1]);
            }
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER reject_publish_audit BEFORE INSERT ON AuditEvents
                WHEN NEW.Action IN ('publish.manifest_activated','publish.conflict')
                BEGIN SELECT RAISE(ABORT, 'injected audit write failure'); END;
                """);
        }
        Assert.True(await fixture.ExecuteAsync());
        await using (var scope = fixture.App.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
            var job = await db.ReleaseJobs.SingleAsync(item => item.Id == id);
            Assert.Equal(PublishJobState.Publishing, job.State); Assert.Equal(committed ? 1 : 0, job.Attempts);
            Assert.Equal(0, await db.AuditEvents.CountAsync(item => item.ResourceId == id && item.Action == action));
            Assert.False(await db.JobEvents.AnyAsync(item => item.JobId == id && (item.Stage == "manifest_activated" || item.Stage == "conflict")));
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_publish_audit;");
        }
        Assert.Equal(committed, File.Exists(Path.Combine(fixture.Options.ReleaseRoot, "manifest.json")));
        fixture.App.Clock.Advance(TimeSpan.FromSeconds(6)); await fixture.App.RestartAsync();
        Assert.True(await fixture.ExecuteAsync());
        var response = await fixture.App.Client.GetFromJsonAsync<JsonElement>($"/api/v1/publish-jobs/{id}");
        Assert.Equal(committed ? "manifestActivated" : "conflict", response.GetProperty("state").GetString());
        if (committed) Assert.Equal(manifest, await File.ReadAllBytesAsync(Path.Combine(fixture.Options.ReleaseRoot, "manifest.json")));
        await using var check = fixture.App.App.Services.CreateAsyncScope();
        var database = check.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
        Assert.Equal(committed ? 1 : 0, (await database.ReleaseJobs.SingleAsync(item => item.Id == id)).Attempts);
        Assert.Equal(1, await database.AuditEvents.CountAsync(item => item.ResourceId == id && item.Action == action));
    }
}
