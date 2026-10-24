using System.Security.Claims;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PortKiller.UpdateManagement.Contracts;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Persistence.Entities;

namespace PortKiller.UpdateManagement.Security;

public sealed class ValidationTaskQueue(UpdateAdminDbContext database, TimeProvider clock)
{
    public const int MaximumAttempts = 5;
    private const int LeaseSeconds = 60;
    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();

    public sealed record Completion(string Version, long Sequence, string Runtime, long PackageSize,
        string ManifestSha256, string PackageSha256, string[] ReleaseNotes, long ValidatedUtcMs, string ArchiveId);

    public async Task<(string DraftId, string JobId)> EnqueueAsync(UploadSession upload, CancellationToken token)
    {
        var draft = new ReleaseDraft
        {
            OwnerId = upload.OwnerId, UploadId = upload.Id, ArchiveId = upload.StorageId, ValidationState = ValidationState.Pending,
            CreatedUtcMs = Now
        };
        var job = new ReleaseJob
        {
            DraftId = draft.Id, RequestedById = upload.OwnerId, Kind = "validate", Application = draft.Application,
            Channel = draft.Channel, State = PublishJobState.Queued, CreatedUtcMs = Now
        };
        database.ReleaseDrafts.Add(draft); database.ReleaseJobs.Add(job);
        database.JobEvents.Add(new JobEvent { JobId = job.Id, Sequence = 1, Stage = "queued", Message = "validation_queued", CreatedUtcMs = Now });
        await database.SaveChangesAsync(token);
        return (draft.Id, job.Id);
    }

    public async Task<ValidationTaskResponse> GetAsync(ClaimsPrincipal principal, string id, CancellationToken token)
    {
        string? ownerId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var row = await database.ReleaseJobs.AsNoTracking().Where(job => job.Id == id && job.Kind == "validate"
            && (job.RequestedById == ownerId || principal.IsInRole("Administrator")))
            .Join(database.ReleaseDrafts, job => job.DraftId, draft => draft.Id, (job, draft) => new { job, draft })
            .SingleOrDefaultAsync(token);
        if (row == null) throw new AdminFailure("resource_not_found", 404, "验证任务不存在。");
        return Response(row.job, row.draft);
    }

    public async Task<ValidationTaskResponse?> ClaimAsync(string workerId, CancellationToken token)
    {
        if (workerId.Length is < 1 or > 128 || workerId.Any(c => !char.IsAsciiLetterOrDigit(c) && "._-".IndexOf(c) < 0))
            throw new ArgumentException("Worker identity is invalid.", nameof(workerId));
        long now = Now;
        await FailExhaustedLeasesAsync(now, token);
        for (int conflict = 0; conflict < 8; conflict++)
        {
            try
            {
                await using var transaction = await database.Database.BeginTransactionAsync(token);
                var candidate = await database.ReleaseJobs.AsNoTracking()
                    .Where(job => job.Kind == "validate" && job.Attempts < MaximumAttempts
                        && (job.State == PublishJobState.Queued
                            || job.State == PublishJobState.Publishing && job.LeaseExpiresUtcMs <= now))
                    .OrderBy(job => job.CreatedUtcMs).ThenBy(job => job.Id)
                    .Select(job => new { job.Id, job.DraftId }).FirstOrDefaultAsync(token);
                if (candidate == null)
                {
                    await transaction.CommitAsync(token);
                    return null;
                }
                long until = now + LeaseSeconds * 1000L;
                int claimed = await database.ReleaseJobs.Where(job => job.Id == candidate.Id && job.Kind == "validate"
                        && job.Attempts < MaximumAttempts
                        && (job.State == PublishJobState.Queued
                            || job.State == PublishJobState.Publishing && job.LeaseExpiresUtcMs <= now))
                    .ExecuteUpdateAsync(update => update.SetProperty(job => job.State, PublishJobState.Publishing)
                        .SetProperty(job => job.LeaseOwner, workerId).SetProperty(job => job.LeaseExpiresUtcMs, until)
                        .SetProperty(job => job.Attempts, job => job.Attempts + 1)
                        .SetProperty(job => job.Revision, job => job.Revision + 1), token);
                if (claimed == 0)
                {
                    await transaction.CommitAsync(token);
                    continue;
                }
                await database.ReleaseDrafts.Where(draft => draft.Id == candidate.DraftId
                        && draft.ValidationState != ValidationState.ReadyForApproval)
                    .ExecuteUpdateAsync(update => update.SetProperty(draft => draft.ValidationState, ValidationState.Validating)
                        .SetProperty(draft => draft.ValidationErrorCode, (string?)null)
                        .SetProperty(draft => draft.Revision, draft => draft.Revision + 1), token);
                await AddEventAsync(candidate.Id, "claimed", "validation_claimed", token);
                var row = await database.ReleaseJobs.AsNoTracking().Where(job => job.Id == candidate.Id)
                    .Join(database.ReleaseDrafts, job => job.DraftId, draft => draft.Id, (job, draft) => new { job, draft })
                    .SingleAsync(token);
                await transaction.CommitAsync(token);
                return Response(row.job, row.draft);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10 * (conflict + 1)), token);
            }
        }
        return null;
    }

    public async Task<bool> RenewAsync(string id, string workerId, CancellationToken token)
    {
        long now = Now;
        int changed = await database.ReleaseJobs.Where(job => job.Id == id && job.Kind == "validate"
                && job.State == PublishJobState.Publishing && job.LeaseOwner == workerId && job.LeaseExpiresUtcMs > now)
            .ExecuteUpdateAsync(update => update.SetProperty(job => job.LeaseExpiresUtcMs, now + LeaseSeconds * 1000L)
                .SetProperty(job => job.Revision, job => job.Revision + 1), token);
        return changed == 1;
    }

    public async Task<bool> ReleaseAsync(string id, string workerId, string code, CancellationToken token)
    {
        if (code.Length is < 1 or > 80 || code.Any(c => !char.IsAsciiLetterOrDigit(c) && "._-".IndexOf(c) < 0))
            throw new ArgumentException("Error code must be a bounded identifier.", nameof(code));
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        long now = Now;
        var task = await database.ReleaseJobs.Where(job => job.Id == id && job.Kind == "validate"
                && job.State == PublishJobState.Publishing && job.LeaseOwner == workerId && job.LeaseExpiresUtcMs > now)
            .Select(job => new { job.Attempts, job.DraftId }).SingleOrDefaultAsync(token);
        if (task == null) return false;
        PublishJobState state = task.Attempts >= MaximumAttempts ? PublishJobState.Failed : PublishJobState.Queued;
        string? error = state == PublishJobState.Failed ? "validation_retry_exhausted" : null;
        int changed = await database.ReleaseJobs.Where(job => job.Id == id && job.Kind == "validate"
                && job.State == PublishJobState.Publishing && job.LeaseOwner == workerId && job.LeaseExpiresUtcMs > now)
            .ExecuteUpdateAsync(update => update.SetProperty(job => job.State, state).SetProperty(job => job.ErrorCode, error)
                .SetProperty(job => job.LeaseOwner, (string?)null).SetProperty(job => job.LeaseExpiresUtcMs, (long?)null)
                .SetProperty(job => job.Revision, job => job.Revision + 1), token);
        if (changed != 1) return false;
        if (state == PublishJobState.Failed)
            await database.ReleaseDrafts.Where(draft => draft.Id == task.DraftId
                    && draft.ValidationState != ValidationState.Rejected)
                .ExecuteUpdateAsync(update => update.SetProperty(draft => draft.ValidationState, ValidationState.Rejected)
                    .SetProperty(draft => draft.ValidationErrorCode, error)
                    .SetProperty(draft => draft.Revision, draft => draft.Revision + 1), token);
        await AddEventAsync(id, state == PublishJobState.Queued ? "retry" : "failed", code, token);
        await transaction.CommitAsync(token);
        return true;
    }

    public async Task<bool> RejectAsync(string id, string workerId, string code, CancellationToken token)
    {
        if (code.Length is < 1 or > 80 || code.Any(c => !char.IsAsciiLetterOrDigit(c) && "._-".IndexOf(c) < 0))
            throw new ArgumentException("Error code must be a bounded identifier.", nameof(code));
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        var task = await database.ReleaseJobs.Where(job => job.Id == id && job.Kind == "validate"
                && job.State == PublishJobState.Publishing && job.LeaseOwner == workerId && job.LeaseExpiresUtcMs > Now)
            .Select(job => new { job.DraftId }).SingleOrDefaultAsync(token);
        if (task == null) return false;
        int changed = await database.ReleaseJobs.Where(job => job.Id == id && job.Kind == "validate"
                && job.State == PublishJobState.Publishing && job.LeaseOwner == workerId && job.LeaseExpiresUtcMs > Now)
            .ExecuteUpdateAsync(update => update.SetProperty(job => job.State, PublishJobState.Failed)
                .SetProperty(job => job.ErrorCode, code).SetProperty(job => job.LeaseOwner, (string?)null)
                .SetProperty(job => job.LeaseExpiresUtcMs, (long?)null).SetProperty(job => job.Revision, job => job.Revision + 1), token);
        if (changed != 1) return false;
        await database.ReleaseDrafts.Where(draft => draft.Id == task.DraftId)
            .ExecuteUpdateAsync(update => update.SetProperty(draft => draft.ValidationState, ValidationState.Rejected)
                .SetProperty(draft => draft.ValidationErrorCode, code).SetProperty(draft => draft.Revision, draft => draft.Revision + 1), token);
        await AddEventAsync(id, "rejected", code, token);
        await transaction.CommitAsync(token);
        return true;
    }

    public async Task<bool> CompleteAsync(string id, string workerId, Completion completion, CancellationToken token)
    {
        if (completion.ArchiveId.Length is < 1 or > 64 || completion.ArchiveId.Any(c => !char.IsAsciiLetterOrDigit(c))
            || completion.Version.Length is < 1 or > 32 || completion.Sequence <= 0 || completion.Runtime != "win-x64"
            || completion.PackageSize <= 0 || completion.ManifestSha256.Length != 64 || completion.PackageSha256.Length != 64
            || completion.ReleaseNotes.Length > 20)
            throw new ArgumentException("Validation completion is invalid.", nameof(completion));
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        var task = await database.ReleaseJobs.Where(job => job.Id == id && job.Kind == "validate"
                && job.State == PublishJobState.Publishing && job.LeaseOwner == workerId && job.LeaseExpiresUtcMs > Now)
            .Join(database.ReleaseDrafts, job => job.DraftId, draft => draft.Id, (job, draft) => new { job.DraftId, draft.ArchiveId })
            .SingleOrDefaultAsync(token);
        if (task == null) return false;
        if (!task.ArchiveId.Equals(completion.ArchiveId, StringComparison.Ordinal)) return false;
        int changed = await database.ReleaseJobs.Where(job => job.Id == id && job.Kind == "validate"
                && job.State == PublishJobState.Publishing && job.LeaseOwner == workerId && job.LeaseExpiresUtcMs > Now)
            .ExecuteUpdateAsync(update => update.SetProperty(job => job.State, PublishJobState.Completed)
                .SetProperty(job => job.ErrorCode, (string?)null).SetProperty(job => job.LeaseOwner, (string?)null)
                .SetProperty(job => job.LeaseExpiresUtcMs, (long?)null).SetProperty(job => job.Revision, job => job.Revision + 1), token);
        if (changed != 1) return false;
        await database.ReleaseDrafts.Where(draft => draft.Id == task.DraftId)
            .ExecuteUpdateAsync(update => update.SetProperty(draft => draft.ValidationState, ValidationState.ReadyForApproval)
                .SetProperty(draft => draft.ValidationErrorCode, (string?)null).SetProperty(draft => draft.Version, completion.Version)
                .SetProperty(draft => draft.Sequence, completion.Sequence).SetProperty(draft => draft.Runtime, completion.Runtime)
                .SetProperty(draft => draft.ManifestSha256, completion.ManifestSha256).SetProperty(draft => draft.PackageSha256, completion.PackageSha256)
                .SetProperty(draft => draft.ReleaseNotesJson, System.Text.Json.JsonSerializer.Serialize(completion.ReleaseNotes))
                .SetProperty(draft => draft.ValidatedUtcMs, completion.ValidatedUtcMs)
                .SetProperty(draft => draft.Revision, draft => draft.Revision + 1), token);
        await AddEventAsync(id, "validated", "validation_ready_for_approval", token);
        await transaction.CommitAsync(token);
        return true;
    }

    private async Task AddEventAsync(string id, string stage, string message, CancellationToken token)
    {
        long sequence = (await database.JobEvents.Where(item => item.JobId == id).MaxAsync(item => (long?)item.Sequence, token) ?? 0) + 1;
        database.JobEvents.Add(new JobEvent { JobId = id, Sequence = sequence, Stage = stage, Message = message, CreatedUtcMs = Now });
        await database.SaveChangesAsync(token);
    }

    private static ValidationTaskResponse Response(ReleaseJob job, ReleaseDraft draft)
        => new(job.Id, job.DraftId, job.State, job.Attempts, job.CreatedUtcMs, job.LeaseExpiresUtcMs, job.ErrorCode,
            draft.ValidationState, draft.Version, draft.Sequence, draft.ManifestSha256, draft.PackageSha256,
            JsonSerializer.Deserialize<string[]>(draft.ReleaseNotesJson) ?? [], draft.ValidatedUtcMs);


    private async Task FailExhaustedLeasesAsync(long now, CancellationToken token)
    {
        var expired = await database.ReleaseJobs.AsNoTracking()
            .Where(job => job.Kind == "validate" && job.State == PublishJobState.Publishing
                && job.LeaseExpiresUtcMs <= now && job.Attempts >= MaximumAttempts)
            .Select(job => new { job.Id, job.DraftId }).Take(32).ToListAsync(token);
        foreach (var item in expired)
        {
            await using var transaction = await database.Database.BeginTransactionAsync(token);
            int changed = await database.ReleaseJobs.Where(job => job.Id == item.Id && job.Kind == "validate"
                    && job.State == PublishJobState.Publishing && job.LeaseExpiresUtcMs <= now
                    && job.Attempts >= MaximumAttempts)
                .ExecuteUpdateAsync(update => update.SetProperty(job => job.State, PublishJobState.Failed)
                    .SetProperty(job => job.ErrorCode, "validation_retry_exhausted")
                    .SetProperty(job => job.LeaseOwner, (string?)null).SetProperty(job => job.LeaseExpiresUtcMs, (long?)null)
                    .SetProperty(job => job.Revision, job => job.Revision + 1), token);
            if (changed == 1)
            {
                await database.ReleaseDrafts.Where(draft => draft.Id == item.DraftId
                        && draft.ValidationState != ValidationState.Rejected)
                    .ExecuteUpdateAsync(update => update.SetProperty(draft => draft.ValidationState, ValidationState.Rejected)
                        .SetProperty(draft => draft.ValidationErrorCode, "validation_retry_exhausted")
                        .SetProperty(draft => draft.Revision, draft => draft.Revision + 1), token);
                await AddEventAsync(item.Id, "failed", "validation_retry_exhausted", token);
            }
            await transaction.CommitAsync(token);
        }
    }
}
