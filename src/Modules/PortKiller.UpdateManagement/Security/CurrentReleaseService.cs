using Microsoft.EntityFrameworkCore;
using PortKiller.UpdateManagement.Contracts;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Persistence.Entities;

namespace PortKiller.UpdateManagement.Security;

/// <summary>Reads the commit record without downloading packages or calling the writable publisher tool.</summary>
public sealed class CurrentReleaseService(IPublishedReleaseReader source, UpdateAdminDbContext database, TimeProvider clock)
{
    public async Task<CurrentReleaseResponse> GetAsync(CancellationToken token)
    {
        PublishedRelease? release = await source.ReadAsync(token);
        long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        if (release == null) return new("PortKill", "stable", now, null);
        var rows = await database.ReleaseJobs.AsNoTracking().Where(job => job.Kind == "publish"
                && job.Application == "PortKill" && job.Channel == "stable")
            .Join(database.PublishApprovals.Where(approval => approval.ManifestSha256 == release.ManifestSha256),
                job => job.PublishApprovalId, approval => approval.Id, (job, approval) => new { job, approval })
            .Where(row => row.approval.PackageSha256 == release.PackageSha256
                && row.approval.Version == release.Version && row.approval.Sequence == release.Sequence)
            .OrderByDescending(row => row.job.ActivatedUtcMs).ThenByDescending(row => row.job.CreatedUtcMs)
            .FirstOrDefaultAsync(token);
        ReleaseJob? job = rows?.job;
        double days = (release.ExpiresUtcMs - now) / TimeSpan.FromDays(1).TotalMilliseconds;
        var expiry = days <= 0 ? ManifestExpiryStatus.Expired : days < 7 ? ManifestExpiryStatus.Critical
            : days < 14 ? ManifestExpiryStatus.Expiring : ManifestExpiryStatus.Healthy;
        var receipt = job?.ReceiptSha256 is { Length: 64 } sha && job.ActivatedUtcMs is { } at
            ? new ActivationReceiptResponse(job.Id, sha, at) : null;
        return new("PortKill", "stable", now, new(release.Version, release.Sequence, release.ManifestSha256,
            release.PackageSha256, release.ExpiresUtcMs, days, expiry, receipt, job == null ? null : Check(job, now)));
    }

    internal static PublicCheckResponse Check(ReleaseJob job, long now) => new(job.PublicCheckAttempts,
        job.State is PublishJobState.ManifestActivated or PublishJobState.PublicVerifying,
        job.State == PublishJobState.PublicVerifying && job.LeaseOwner != null && job.LeaseExpiresUtcMs > now, job.PublicCheckStartedUtcMs,
        job.PublicCheckCompletedUtcMs, job.PublicCheckSucceeded, job.PublicCheckErrorCode);
}
