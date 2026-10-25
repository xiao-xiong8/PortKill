using Microsoft.EntityFrameworkCore;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Contracts;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Persistence.Entities;

namespace PortKiller.UpdateManagement.Security;

/// <summary>Returns a bounded, secret-free readiness snapshot for the local monitor.</summary>
public sealed class AdminHealthService(
    UpdateAdminDbContext database,
    AdminOptions options,
    IPublishedReleaseReader releaseSource,
    TimeProvider clock)
{
    public async Task<AdminHealthResponse> GetAsync(CancellationToken token)
    {
        long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        string databaseStatus = "healthy";
        var queue = new AdminHealthQueueResponse(0, 0, 0, 0);
        try
        {
            await AdminDatabaseReadiness.RequireAsync(database, options, token);
            int validation = await database.ReleaseJobs.AsNoTracking().CountAsync(job => job.Kind == "validate"
                && (job.State == PublishJobState.Queued || job.State == PublishJobState.Publishing), token);
            int publish = await database.ReleaseJobs.AsNoTracking().CountAsync(job => job.Kind == "publish"
                && (job.State == PublishJobState.Queued || job.State == PublishJobState.Publishing
                    || job.State == PublishJobState.ManifestActivated || job.State == PublishJobState.PublicVerifying), token);
            int attention = await database.ReleaseJobs.AsNoTracking().CountAsync(job =>
                job.State == PublishJobState.Failed || job.State == PublishJobState.Conflict
                    || job.State == PublishJobState.ActiveUnverified, token);
            queue = new(validation, publish, checked(validation + publish), attention);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { databaseStatus = "unavailable"; }

        AdminHealthStorageResponse? storage = null;
        try
        {
            string root = Path.GetPathRoot(options.DataRoot) ?? options.DataRoot;
            var drive = new DriveInfo(root);
            long total = drive.TotalSize;
            long free = drive.AvailableFreeSpace;
            storage = new(total, Math.Max(0, total - free), free, total == 0 ? 0 : free * 100d / total);
        }
        catch (Exception) { }

        AdminHealthManifestResponse manifest;
        try
        {
            PublishedRelease? release = await releaseSource.ReadAsync(token);
            if (release == null) manifest = new("missing", null, null, null);
            else
            {
                double days = (release.ExpiresUtcMs - now) / TimeSpan.FromDays(1).TotalMilliseconds;
                string state = days <= 0 ? "expired" : days <= 7 ? "critical" : days <= 14 ? "warning" : "healthy";
                manifest = new(state, release.Version, release.Sequence, days);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { manifest = new("unavailable", null, null, null); }

        string status = databaseStatus == "healthy" && manifest.Status is not ("unavailable" or "missing") ? "ok" : "degraded";
        return new(status, "admin-api", now, databaseStatus, queue, storage, manifest);
    }
}
