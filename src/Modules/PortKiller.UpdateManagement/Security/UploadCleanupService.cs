using Microsoft.EntityFrameworkCore;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Contracts;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Persistence.Entities;

namespace PortKiller.UpdateManagement.Security;

public sealed record UploadCleanupResult(int ExpiredUploads, int StagingDirectoriesDeleted, int ArchivesDeleted);

public sealed class UploadCleanupService(UpdateAdminDbContext database, AdminOptions options, TimeProvider clock)
{
    private const int ArchiveCleanupLeaseMinutes = 15;
    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();

    public async Task<UploadCleanupResult> CleanupAsync(CancellationToken token)
    {
        long now = Now;
        int expired = 0, stagingDeleted = 0, archivesDeleted = 0;
        var stagingCandidates = await database.UploadSessions.AsNoTracking()
            .Where(upload => upload.StorageCleanedUtcMs == null &&
                ((upload.State == UploadState.Uploading && upload.ExpiresUtcMs <= now) || upload.State == UploadState.Cancelled
                    || upload.State == UploadState.Expired))
            .Select(upload => new { upload.Id, upload.StorageId, upload.State, upload.ExpiresUtcMs }).Take(128).ToListAsync(token);
        foreach (var candidate in stagingCandidates)
        {
            if (candidate.State == UploadState.Uploading)
            {
                int changed = await database.UploadSessions.Where(upload => upload.Id == candidate.Id
                        && upload.State == UploadState.Uploading && upload.ExpiresUtcMs <= now && upload.StorageCleanedUtcMs == null)
                    .ExecuteUpdateAsync(update => update.SetProperty(upload => upload.State, UploadState.Expired)
                        .SetProperty(upload => upload.LastActivityUtcMs, now).SetProperty(upload => upload.Revision, upload => upload.Revision + 1), token);
                expired += changed;
                if (changed == 0) continue;
            }
            if (TryDeleteDirectory(Path.Combine(options.StagingPath, candidate.StorageId)))
            {
                stagingDeleted++;
                await database.UploadSessions.Where(upload => upload.Id == candidate.Id && upload.StorageCleanedUtcMs == null)
                    .ExecuteUpdateAsync(update => update.SetProperty(upload => upload.StorageCleanedUtcMs, now)
                        .SetProperty(upload => upload.Revision, upload => upload.Revision + 1), token);
            }
        }

        var uploadedCandidates = await database.UploadSessions.AsNoTracking()
            .Where(upload => upload.State == UploadState.Uploaded && upload.StorageCleanedUtcMs == null)
            .Join(database.ReleaseDrafts, upload => upload.Id, draft => draft.UploadId, (upload, draft) => new { upload, draft })
            .Where(item => !database.ReleaseJobs.Any(job => job.DraftId == item.draft.Id && job.Kind == "validate"
                && (job.State == PublishJobState.Queued || job.State == PublishJobState.Publishing)))
            .Select(item => new { item.upload.Id, item.upload.StorageId }).Take(128).ToListAsync(token);
        foreach (var candidate in uploadedCandidates)
        {
            if (!TryDeleteDirectory(Path.Combine(options.StagingPath, candidate.StorageId))) continue;
            stagingDeleted++;
            await database.UploadSessions.Where(upload => upload.Id == candidate.Id && upload.StorageCleanedUtcMs == null)
                .ExecuteUpdateAsync(update => update.SetProperty(upload => upload.StorageCleanedUtcMs, now)
                    .SetProperty(upload => upload.Revision, upload => upload.Revision + 1), token);
        }

        long rejectedArchiveCutoff = now - (long)TimeSpan.FromDays(options.RejectedArchiveRetentionDays).TotalMilliseconds;
        long unpublishedArchiveCutoff = now - (long)TimeSpan.FromDays(options.UnpublishedArchiveRetentionDays).TotalMilliseconds;
        var archiveCandidates = await database.ReleaseDrafts.AsNoTracking()
            .Where(draft => (draft.ValidationState == ValidationState.Rejected && draft.CreatedUtcMs <= rejectedArchiveCutoff
                    || draft.ValidationState == ValidationState.ReadyForApproval && draft.CreatedUtcMs <= unpublishedArchiveCutoff)
                && draft.ArchiveCleanedUtcMs == null
                && (draft.ArchiveCleanupLeaseUtcMs == null || draft.ArchiveCleanupLeaseUtcMs <= now)
                && !database.PublishApprovals.Any(approval => approval.DraftId == draft.Id)
                && !database.ReleaseJobs.Any(job => job.DraftId == draft.Id &&
                    ((job.Kind == "publish" && job.State != PublishJobState.Failed && job.State != PublishJobState.Superseded)
                        || (job.Kind == "validate" && (job.State == PublishJobState.Queued || job.State == PublishJobState.Publishing)))))
            .Select(draft => new { draft.Id, draft.ArchiveId }).Take(64).ToListAsync(token);
        foreach (var candidate in archiveCandidates)
        {
            long leaseUntil = now + (long)TimeSpan.FromMinutes(ArchiveCleanupLeaseMinutes).TotalMilliseconds;
            await using var transaction = await database.Database.BeginTransactionAsync(token);
            int claimed = await database.ReleaseDrafts
                .Where(draft => draft.Id == candidate.Id && draft.ArchiveCleanedUtcMs == null
                    && (draft.ArchiveCleanupLeaseUtcMs == null || draft.ArchiveCleanupLeaseUtcMs <= now)
                    && !database.PublishApprovals.Any(approval => approval.DraftId == draft.Id)
                    && !database.ReleaseJobs.Any(job => job.DraftId == draft.Id &&
                        ((job.Kind == "publish" && job.State != PublishJobState.Failed && job.State != PublishJobState.Superseded)
                            || (job.Kind == "validate" && (job.State == PublishJobState.Queued || job.State == PublishJobState.Publishing)))))
                .ExecuteUpdateAsync(update => update.SetProperty(draft => draft.ArchiveCleanupLeaseUtcMs, leaseUntil)
                    .SetProperty(draft => draft.Revision, draft => draft.Revision + 1), token);
            if (claimed != 1)
            {
                await transaction.CommitAsync(token);
                continue;
            }

            bool deleted = TryDeleteDirectory(Path.Combine(options.ArchivePath, candidate.ArchiveId));
            if (deleted)
            {
                archivesDeleted++;
                await database.ReleaseDrafts.Where(draft => draft.Id == candidate.Id
                        && draft.ArchiveCleanedUtcMs == null && draft.ArchiveCleanupLeaseUtcMs == leaseUntil)
                    .ExecuteUpdateAsync(update => update.SetProperty(draft => draft.ArchiveCleanedUtcMs, now)
                        .SetProperty(draft => draft.ArchiveCleanupLeaseUtcMs, (long?)null)
                        .SetProperty(draft => draft.Revision, draft => draft.Revision + 1), token);
            }
            else
            {
                await database.ReleaseDrafts.Where(draft => draft.Id == candidate.Id
                        && draft.ArchiveCleanupLeaseUtcMs == leaseUntil)
                    .ExecuteUpdateAsync(update => update.SetProperty(draft => draft.ArchiveCleanupLeaseUtcMs, (long?)null)
                        .SetProperty(draft => draft.Revision, draft => draft.Revision + 1), token);
            }
            await transaction.CommitAsync(token);
        }
        return new(expired, stagingDeleted, archivesDeleted);
    }

    private bool TryDeleteDirectory(string path)
    {
        AdminRuntimeLease.RequireLocalPath(path);
        if (!Directory.Exists(path)) return true;
        try { Directory.Delete(path, recursive: true); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
