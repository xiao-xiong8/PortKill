using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Contracts;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Persistence.Entities;
using PortKiller.UpdateManagement.Security;

namespace PortKiller.UpdateAdmin.Tests;

public sealed class UploadCleanupTests
{
    [Fact]
    public async Task CleanupExpiresAndReclaimsIncompleteUploadsWithoutDeletingReferencedArchives()
    {
        await using var app = await TestApplication.StartAsync(); await MfaTests.EnrollAsync(app);
        var created = await app.Client.PostAsJsonAsync("/api/v1/uploads/", new CreateUploadRequest(4, 4));
        string uploadId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        string storageId;
        using (var scope = app.App.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
            var upload = await database.UploadSessions.SingleAsync(item => item.Id == uploadId);
            storageId = upload.StorageId;
            app.Clock.Advance(TimeSpan.FromDays(2));
            await File.WriteAllBytesAsync(Path.Combine(app.Root, "staging", storageId, "package.zip"), [1, 2]);
            var cleanup = scope.ServiceProvider.GetRequiredService<UploadCleanupService>();
            var result = await cleanup.CleanupAsync(CancellationToken.None);
            Assert.Equal(1, result.ExpiredUploads);
            Assert.True(result.StagingDirectoriesDeleted >= 1);
            Assert.False(Directory.Exists(Path.Combine(app.Root, "staging", storageId)));
            database.ChangeTracker.Clear();
            Assert.Equal(UploadState.Expired, (await database.UploadSessions.AsNoTracking().SingleAsync(item => item.Id == uploadId)).State);
        }

        string archiveId = Guid.NewGuid().ToString("N");
        using (var scope = app.App.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
            string userId = (await scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<AdminUser>>().FindByNameAsync("operator"))!.Id;
            var upload = new UploadSession { OwnerId = userId, StorageId = Guid.NewGuid().ToString("N"), State = UploadState.Uploaded,
                CreatedUtcMs = 1, LastActivityUtcMs = 1, ExpiresUtcMs = 2 };
            var draft = new ReleaseDraft { OwnerId = userId, UploadId = upload.Id, ArchiveId = archiveId, ValidationState = ValidationState.Rejected,
                CreatedUtcMs = 1, ValidationErrorCode = "validation_failed" };
            var job = new ReleaseJob { DraftId = draft.Id, RequestedById = userId, Kind = "validate", State = PublishJobState.Publishing, CreatedUtcMs = 1 };
            database.UploadSessions.Add(upload); database.ReleaseDrafts.Add(draft); database.ReleaseJobs.Add(job); await database.SaveChangesAsync();
            string archive = Path.Combine(app.Root, "archive", archiveId); Directory.CreateDirectory(archive); await File.WriteAllBytesAsync(Path.Combine(archive, "package.zip"), [1]);
            var result = await scope.ServiceProvider.GetRequiredService<UploadCleanupService>().CleanupAsync(CancellationToken.None);
            Assert.Equal(0, result.ArchivesDeleted);
            Assert.True(File.Exists(Path.Combine(archive, "package.zip")));
            job.State = PublishJobState.Failed; await database.SaveChangesAsync();
            app.Clock.Advance(TimeSpan.FromDays(31));
            result = await scope.ServiceProvider.GetRequiredService<UploadCleanupService>().CleanupAsync(CancellationToken.None);
            Assert.Equal(1, result.ArchivesDeleted);
            Assert.False(Directory.Exists(archive));
            database.ChangeTracker.Clear();
            Assert.NotNull((await database.ReleaseDrafts.AsNoTracking().SingleAsync(item => item.Id == draft.Id)).ArchiveCleanedUtcMs);
        }
    }
}
