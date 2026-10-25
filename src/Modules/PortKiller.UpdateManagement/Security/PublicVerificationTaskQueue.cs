using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PortKiller.UpdateManagement.Contracts;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Persistence.Entities;

namespace PortKiller.UpdateManagement.Security;

public sealed record PublicVerificationTask(string Id, string Version, long Sequence, string ManifestSha256, string PackageSha256);

/// <summary>Download checks have their own attempts and never transition back into activation.</summary>
public sealed class PublicVerificationTaskQueue(UpdateAdminDbContext database, TimeProvider clock, JobEventJournal events)
{
    private const int LeaseSeconds = 60;
    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();

    public async Task<PublicVerificationTask?> ClaimAsync(string workerId, CancellationToken token)
    {
        if (workerId.Length is < 1 or > 128 || workerId.Any(c => !char.IsAsciiLetterOrDigit(c) && "._-".IndexOf(c) < 0))
            throw new ArgumentException("Worker identity is invalid.", nameof(workerId));
        for (int attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                long now = Now;
                await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
                var row = await Eligible(now).AsNoTracking().OrderBy(job => job.CreatedUtcMs).ThenBy(job => job.Id)
                    .Join(database.PublishApprovals, job => job.PublishApprovalId, approval => approval.Id,
                        (job, approval) => new { job.Id, approval.Version, approval.Sequence, approval.ManifestSha256, approval.PackageSha256 })
                    .FirstOrDefaultAsync(token);
                if (row == null) { await transaction.CommitAsync(token); return null; }
                int changed = await Eligible(now).Where(job => job.Id == row.Id)
                    .ExecuteUpdateAsync(update => update.SetProperty(job => job.State, PublishJobState.PublicVerifying)
                        .SetProperty(job => job.LeaseOwner, workerId).SetProperty(job => job.LeaseExpiresUtcMs, now + LeaseSeconds * 1000L)
                        .SetProperty(job => job.PublicCheckAttempts, job => job.PublicCheckAttempts + 1)
                        .SetProperty(job => job.PublicCheckStartedUtcMs, now).SetProperty(job => job.ErrorCode, (string?)null)
                        .SetProperty(job => job.Revision, job => job.Revision + 1), token);
                if (changed == 0) { await transaction.CommitAsync(token); continue; }
                await AddEventAsync(row.Id, "public_verifying", "public_check_started", token);
                await transaction.CommitAsync(token);
                return new(row.Id, row.Version, row.Sequence, row.ManifestSha256, row.PackageSha256);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
            { database.ChangeTracker.Clear(); await Task.Delay(10 * (attempt + 1), token); }
        }
        return null;
    }

    public async Task<bool> RenewAsync(string id, string workerId, CancellationToken token)
    {
        long now = Now;
        return await Owned(id, workerId, now).ExecuteUpdateAsync(update => update
            .SetProperty(job => job.LeaseExpiresUtcMs, now + LeaseSeconds * 1000L)
            .SetProperty(job => job.Revision, job => job.Revision + 1), token) == 1;
    }

    public async Task<bool> CompleteAsync(string id, string workerId, bool succeeded, string? error, CancellationToken token)
    {
        if (!succeeded && (error is not { Length: > 0 and <= 80 }
            || error.Any(c => !char.IsAsciiLetterOrDigit(c) && "._-".IndexOf(c) < 0)))
            throw new ArgumentException("A bounded error identifier is required.", nameof(error));
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        long now = Now;
        int changed = await Owned(id, workerId, now).ExecuteUpdateAsync(update => update
            .SetProperty(job => job.State, succeeded ? PublishJobState.Completed : PublishJobState.ActiveUnverified)
            .SetProperty(job => job.ErrorCode, succeeded ? null : error)
            .SetProperty(job => job.PublicCheckCompletedUtcMs, now).SetProperty(job => job.PublicCheckSucceeded, succeeded)
            .SetProperty(job => job.PublicCheckErrorCode, succeeded ? null : error)
            .SetProperty(job => job.LeaseOwner, (string?)null).SetProperty(job => job.LeaseExpiresUtcMs, (long?)null)
            .SetProperty(job => job.Revision, job => job.Revision + 1), token);
        if (changed == 0) return false;
        await AddEventAsync(id, succeeded ? "completed" : "active_unverified", succeeded ? "public_check_passed" : error!, token);
        await transaction.CommitAsync(token);
        return true;
    }

    public async Task<bool> SupersedeAsync(string id, string workerId, CancellationToken token)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        int changed = await Owned(id, workerId, Now).ExecuteUpdateAsync(update => update
            .SetProperty(job => job.State, PublishJobState.Superseded).SetProperty(job => job.ErrorCode, "publish_superseded")
            .SetProperty(job => job.LeaseOwner, (string?)null).SetProperty(job => job.LeaseExpiresUtcMs, (long?)null)
            .SetProperty(job => job.Revision, job => job.Revision + 1), token);
        if (changed == 0) return false;
        await AddEventAsync(id, "superseded", "publish_superseded", token);
        await transaction.CommitAsync(token);
        return true;
    }

    private IQueryable<ReleaseJob> Eligible(long now) => database.ReleaseJobs.Where(job => job.Kind == "publish"
        && (job.State == PublishJobState.ManifestActivated
            || job.State == PublishJobState.PublicVerifying && (job.LeaseExpiresUtcMs == null || job.LeaseExpiresUtcMs <= now)));

    private IQueryable<ReleaseJob> Owned(string id, string workerId, long now) => database.ReleaseJobs.Where(job => job.Id == id
        && job.Kind == "publish" && job.State == PublishJobState.PublicVerifying && job.LeaseOwner == workerId && job.LeaseExpiresUtcMs > now);

    private Task AddEventAsync(string id, string stage, string message, CancellationToken token)
        => events.AppendAsync(id, stage, message, token);
}
