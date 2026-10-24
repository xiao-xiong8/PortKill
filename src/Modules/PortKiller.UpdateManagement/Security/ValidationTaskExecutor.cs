using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Contracts;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Persistence.Entities;

namespace PortKiller.UpdateManagement.Security;

public sealed class ValidationTaskExecutor(
    UpdateAdminDbContext database,
    ValidationTaskQueue queue,
    ValidationArchiveService archive,
    UpdateToolClient tool,
    TimeProvider clock,
    ILogger<ValidationTaskExecutor> logger)
{
    private static readonly TimeSpan LeaseRenewalPeriod = TimeSpan.FromSeconds(20);

    public async Task<bool> ExecuteOnceAsync(string workerId, CancellationToken token)
    {
        ValidationTaskResponse? task = await queue.ClaimAsync(workerId, token);
        if (task == null) return false;
        using var leaseCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task leaseHeartbeat = MaintainLeaseAsync(task.Id, workerId, leaseCancellation.Token);

        async Task StopLeaseHeartbeatAsync()
        {
            leaseCancellation.Cancel();
            await leaseHeartbeat;
        }

        try
        {
            var context = await database.ReleaseJobs.AsNoTracking().Where(job => job.Id == task.Id)
                .Join(database.ReleaseDrafts, job => job.DraftId, draft => draft.Id, (job, draft) => new { job, draft })
                .Join(database.UploadSessions, item => item.draft.UploadId, upload => upload.Id, (item, upload) => new { item.job, item.draft, upload })
                .SingleOrDefaultAsync(token);
            if (context == null)
            {
                await StopLeaseHeartbeatAsync();
                await queue.RejectAsync(task.Id, workerId, "validation_input_missing", token);
                return true;
            }

            ArchivedValidationInput archived = archive.Archive(context.upload, token);
            UpdateToolResult result = await tool.VerifyAsync(archived.ManifestPath, archived.PackagePath, token);
            if (!result.Succeeded)
            {
                await StopLeaseHeartbeatAsync();
                string errorCode = result.ErrorCode ?? "validation_failed";
                bool changed = IsRetryableToolFailure(errorCode)
                    ? await queue.ReleaseAsync(task.Id, workerId, errorCode, token)
                    : await queue.RejectAsync(task.Id, workerId, errorCode, token);
                if (!changed) logger.LogWarning("Validation task {TaskId} lease changed before failure was recorded", task.Id);
                return true;
            }
            if (!DateTimeOffset.TryParse(result.ExpiresUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset expiresUtc)
                || expiresUtc <= clock.GetUtcNow())
            {
                await StopLeaseHeartbeatAsync();
                await queue.RejectAsync(task.Id, workerId, "validation_expired", token);
                return true;
            }
            if (!string.Equals(result.ManifestSha256, archived.ManifestSha256, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(result.PackageSha256, archived.PackageSha256, StringComparison.OrdinalIgnoreCase)
                || result.PackageSize != new FileInfo(archived.PackagePath).Length
                || result.Version == null || result.Sequence == null || result.Runtime == null)
            {
                await StopLeaseHeartbeatAsync();
                await queue.RejectAsync(task.Id, workerId, "validation_digest_mismatch", token);
                return true;
            }

            await StopLeaseHeartbeatAsync();
            bool completed = await queue.CompleteAsync(task.Id, workerId, new ValidationTaskQueue.Completion(
                result.Version, result.Sequence.Value, result.Runtime, result.PackageSize!.Value,
                archived.ManifestSha256, archived.PackageSha256, result.ReleaseNotes,
                clock.GetUtcNow().ToUnixTimeMilliseconds(), expiresUtc.ToUnixTimeMilliseconds(), archived.ArchiveId), token);
            if (!completed) logger.LogWarning("Validation task {TaskId} lease changed before completion", task.Id);
            return true;
        }
        catch (AdminFailure failure)
        {
            await StopLeaseHeartbeatAsync();
            bool changed = failure.Code == "upload_storage_missing"
                ? await queue.ReleaseAsync(task.Id, workerId, failure.Code, token)
                : await queue.RejectAsync(task.Id, workerId, failure.Code, token);
            if (!changed) logger.LogWarning("Validation task {TaskId} lease changed before failure was recorded", task.Id);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogError(exception, "Validation task {TaskId} failed internally", task.Id);
            await StopLeaseHeartbeatAsync();
            bool changed = await queue.ReleaseAsync(task.Id, workerId, "validation_internal_error", token);
            if (!changed) logger.LogWarning("Validation task {TaskId} lease changed before internal failure was recorded", task.Id);
            return true;
        }
        finally
        {
            await StopLeaseHeartbeatAsync();
        }
    }

    private static bool IsRetryableToolFailure(string code)
        => code is "tool_unavailable" or "tool_timeout" or "file_unavailable" or "io_failure"
            or "internal_error" or "remote_check_failed";

    private async Task MaintainLeaseAsync(string taskId, string workerId, CancellationToken token)
    {
        using var timer = new PeriodicTimer(LeaseRenewalPeriod);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                if (!await queue.RenewAsync(taskId, workerId, token))
                {
                    logger.LogWarning("Validation task {TaskId} lease was lost while executing", taskId);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Validation task {TaskId} lease renewal stopped", taskId);
        }
    }
}

public sealed class ValidationTaskWorker(IServiceScopeFactory scopes, UpdateToolOptions options,
    ILogger<ValidationTaskWorker> logger) : BackgroundService
{
    private readonly string _workerId = Environment.MachineName + "." + Guid.NewGuid().ToString("N");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                bool processed = false;
                if (options.Enabled)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    processed = await scope.ServiceProvider.GetRequiredService<ValidationTaskExecutor>()
                        .ExecuteOnceAsync(_workerId, stoppingToken);
                }
                await Task.Delay(processed ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception) { logger.LogCritical(exception, "Validation worker stopped unexpectedly"); throw; }
    }
}
