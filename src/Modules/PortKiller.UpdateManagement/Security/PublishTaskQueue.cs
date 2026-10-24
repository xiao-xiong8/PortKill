using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PortKiller.UpdateManagement.Contracts;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Persistence.Entities;

namespace PortKiller.UpdateManagement.Security;

public sealed record PublishTaskContext(string Id, string DraftId, string ArchiveId, string Application, string Channel,
    string Version, long Sequence, string ManifestSha256, string PackageSha256, string ExpectedManifestSha256,
    int Attempts, long LeaseExpiresUtcMs);

/// <summary>Leases publish jobs and records the durable activation boundary.</summary>
public sealed class PublishTaskQueue(UpdateAdminDbContext database, TimeProvider clock)
{
    public const int MaximumAttempts = 3;
    private const int LeaseSeconds = 60;
    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();

    public async Task<PublishTaskContext?> ClaimAsync(string workerId, CancellationToken token)
    {
        ValidateWorkerId(workerId);
        long now = Now;
        await FailExhaustedAsync(now, token);
        for (int conflict = 0; conflict < 8; conflict++)
        {
            try
            {
                await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
                var candidate = await database.ReleaseJobs.AsNoTracking()
                    .Where(job => job.Kind == "publish" && job.Attempts < MaximumAttempts
                        && (job.State == PublishJobState.Queued
                            || job.State == PublishJobState.Publishing && job.LeaseExpiresUtcMs <= now))
                    .OrderBy(job => job.CreatedUtcMs).ThenBy(job => job.Id)
                    .Select(job => new { job.Id, job.DraftId }).FirstOrDefaultAsync(token);
                if (candidate == null)
                {
                    await transaction.CommitAsync(token);
                    return null;
                }

                long until = checked(now + LeaseSeconds * 1000L);
                int claimed = await database.ReleaseJobs.Where(job => job.Id == candidate.Id && job.Kind == "publish"
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
                await AddEventAsync(candidate.Id, "claimed", "publish_claimed", token);
                var row = await database.ReleaseJobs.AsNoTracking().Where(job => job.Id == candidate.Id)
                    .Join(database.PublishApprovals, job => job.PublishApprovalId, approval => approval.Id, (job, approval) => new { job, approval })
                    .SingleAsync(token);
                await transaction.CommitAsync(token);
                return Context(row.job, row.approval);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
            { await Task.Delay(TimeSpan.FromMilliseconds(10 * (conflict + 1)), token); }
        }
        return null;
    }

    public async Task<bool> RenewAsync(string id, string workerId, CancellationToken token)
    {
        long now = Now;
        int changed = await database.ReleaseJobs.Where(job => job.Id == id && job.Kind == "publish"
                && job.State == PublishJobState.Publishing && job.LeaseOwner == workerId && job.LeaseExpiresUtcMs > now)
            .ExecuteUpdateAsync(update => update.SetProperty(job => job.LeaseExpiresUtcMs, checked(now + LeaseSeconds * 1000L))
                .SetProperty(job => job.Revision, job => job.Revision + 1), token);
        return changed == 1;
    }

    public async Task<bool> CompleteAsync(string id, string workerId, UpdateToolActivationResult result, CancellationToken token)
    {
        if (!result.Succeeded || result.Version is null || result.Sequence is not > 0
            || !IsDigest(result.ManifestSha256) || !IsDigest(result.PackageSha256)) return false;
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        long now = Now;
        int changed = await database.ReleaseJobs.Where(job => job.Id == id && job.Kind == "publish"
                && job.State == PublishJobState.Publishing && job.LeaseOwner == workerId && job.LeaseExpiresUtcMs > now)
            .ExecuteUpdateAsync(update => update.SetProperty(job => job.State, PublishJobState.ManifestActivated)
                .SetProperty(job => job.ErrorCode, (string?)null).SetProperty(job => job.ReceiptSha256, result.ReceiptSha256)
                .SetProperty(job => job.LeaseOwner, (string?)null).SetProperty(job => job.LeaseExpiresUtcMs, (long?)null)
                .SetProperty(job => job.Revision, job => job.Revision + 1), token);
        if (changed != 1) return false;
        await AddEventAsync(id, "manifest_activated", "manifest_activated", token);
        await transaction.CommitAsync(token);
        return true;
    }

    public Task<bool> ConflictAsync(string id, string workerId, string code, CancellationToken token)
        => FinishAsync(id, workerId, PublishJobState.Conflict, code, "conflict", token);

    public Task<bool> FailAsync(string id, string workerId, string code, CancellationToken token)
        => FinishAsync(id, workerId, PublishJobState.Failed, code, "failed", token);

    public async Task<bool> ReleaseAsync(string id, string workerId, string code, CancellationToken token)
    {
        ValidateCode(code);
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        long now = Now;
        var task = await database.ReleaseJobs.Where(job => job.Id == id && job.Kind == "publish"
                && job.State == PublishJobState.Publishing && job.LeaseOwner == workerId && job.LeaseExpiresUtcMs > now)
            .Select(job => new { job.Attempts }).SingleOrDefaultAsync(token);
        if (task == null) return false;
        PublishJobState state = task.Attempts >= MaximumAttempts ? PublishJobState.Failed : PublishJobState.Queued;
        string? error = state == PublishJobState.Failed ? "publish_retry_exhausted" : null;
        int changed = await database.ReleaseJobs.Where(job => job.Id == id && job.Kind == "publish"
                && job.State == PublishJobState.Publishing && job.LeaseOwner == workerId && job.LeaseExpiresUtcMs > now)
            .ExecuteUpdateAsync(update => update.SetProperty(job => job.State, state).SetProperty(job => job.ErrorCode, error)
                .SetProperty(job => job.LeaseOwner, (string?)null).SetProperty(job => job.LeaseExpiresUtcMs, (long?)null)
                .SetProperty(job => job.Revision, job => job.Revision + 1), token);
        if (changed != 1) return false;
        await AddEventAsync(id, state == PublishJobState.Queued ? "retry" : "failed", code, token);
        await transaction.CommitAsync(token);
        return true;
    }

    private async Task<bool> FinishAsync(string id, string workerId, PublishJobState state, string code,
        string stage, CancellationToken token)
    {
        ValidateCode(code);
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        long now = Now;
        int changed = await database.ReleaseJobs.Where(job => job.Id == id && job.Kind == "publish"
                && job.State == PublishJobState.Publishing && job.LeaseOwner == workerId && job.LeaseExpiresUtcMs > now)
            .ExecuteUpdateAsync(update => update.SetProperty(job => job.State, state).SetProperty(job => job.ErrorCode, code)
                .SetProperty(job => job.LeaseOwner, (string?)null).SetProperty(job => job.LeaseExpiresUtcMs, (long?)null)
                .SetProperty(job => job.Revision, job => job.Revision + 1), token);
        if (changed != 1) return false;
        await AddEventAsync(id, stage, code, token);
        await transaction.CommitAsync(token);
        return true;
    }

    private async Task FailExhaustedAsync(long now, CancellationToken token)
    {
        var expired = await database.ReleaseJobs.AsNoTracking()
            .Where(job => job.Kind == "publish" && job.State == PublishJobState.Publishing
                && job.LeaseExpiresUtcMs <= now && job.Attempts >= MaximumAttempts)
            .Select(job => job.Id).Take(32).ToListAsync(token);
        foreach (string id in expired)
        {
            await using var transaction = await database.Database.BeginTransactionAsync(token);
            int changed = await database.ReleaseJobs.Where(job => job.Id == id && job.Kind == "publish"
                    && job.State == PublishJobState.Publishing && job.LeaseExpiresUtcMs <= now && job.Attempts >= MaximumAttempts)
                .ExecuteUpdateAsync(update => update.SetProperty(job => job.State, PublishJobState.Failed)
                    .SetProperty(job => job.ErrorCode, "publish_retry_exhausted")
                    .SetProperty(job => job.LeaseOwner, (string?)null).SetProperty(job => job.LeaseExpiresUtcMs, (long?)null)
                    .SetProperty(job => job.Revision, job => job.Revision + 1), token);
            if (changed == 1) await AddEventAsync(id, "failed", "publish_retry_exhausted", token);
            await transaction.CommitAsync(token);
        }
    }

    private async Task AddEventAsync(string id, string stage, string message, CancellationToken token)
    {
        long sequence = (await database.JobEvents.Where(item => item.JobId == id).MaxAsync(item => (long?)item.Sequence, token) ?? 0) + 1;
        database.JobEvents.Add(new JobEvent { JobId = id, Sequence = sequence, Stage = stage, Message = message, CreatedUtcMs = Now });
        await database.SaveChangesAsync(token);
    }

    private static PublishTaskContext Context(ReleaseJob job, PublishApproval approval)
        => new(job.Id, job.DraftId, approval.ArchiveId, approval.Application, approval.Channel, approval.Version,
            approval.Sequence, approval.ManifestSha256, approval.PackageSha256, approval.ExpectedManifestSha256,
            job.Attempts, job.LeaseExpiresUtcMs ?? 0);

    private static void ValidateWorkerId(string workerId)
    {
        if (workerId.Length is < 1 or > 128 || workerId.Any(c => !char.IsAsciiLetterOrDigit(c) && "._-".IndexOf(c) < 0))
            throw new ArgumentException("Worker identity is invalid.", nameof(workerId));
    }

    private static void ValidateCode(string code)
    {
        if (code.Length is < 1 or > 80 || code.Any(c => !char.IsAsciiLetterOrDigit(c) && "._-".IndexOf(c) < 0))
            throw new ArgumentException("Error code must be a bounded identifier.", nameof(code));
    }

    private static bool IsDigest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
