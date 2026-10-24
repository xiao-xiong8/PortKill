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
    public async Task<bool> ExecuteOnceAsync(string workerId, CancellationToken token)
    {
        ValidationTaskResponse? task = await queue.ClaimAsync(workerId, token);
        if (task == null) return false;
        try
        {
            var context = await database.ReleaseJobs.AsNoTracking().Where(job => job.Id == task.Id)
                .Join(database.ReleaseDrafts, job => job.DraftId, draft => draft.Id, (job, draft) => new { job, draft })
                .Join(database.UploadSessions, item => item.draft.UploadId, upload => upload.Id, (item, upload) => new { item.job, item.draft, upload })
                .SingleOrDefaultAsync(token);
            if (context == null)
            {
                await queue.RejectAsync(task.Id, workerId, "validation_input_missing", token);
                return true;
            }

            ArchivedValidationInput archived = archive.Archive(context.upload, token);
            UpdateToolResult result = await tool.VerifyAsync(archived.ManifestPath, archived.PackagePath, token);
            if (!result.Succeeded)
            {
                await queue.RejectAsync(task.Id, workerId, result.ErrorCode ?? "validation_failed", token);
                return true;
            }
            if (!string.Equals(result.ManifestSha256, archived.ManifestSha256, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(result.PackageSha256, archived.PackageSha256, StringComparison.OrdinalIgnoreCase)
                || result.PackageSize != new FileInfo(archived.PackagePath).Length
                || result.Version == null || result.Sequence == null || result.Runtime == null || result.ExpiresUtc == null)
            {
                await queue.RejectAsync(task.Id, workerId, "validation_digest_mismatch", token);
                return true;
            }

            bool completed = await queue.CompleteAsync(task.Id, workerId, new ValidationTaskQueue.Completion(
                result.Version, result.Sequence.Value, result.Runtime, result.PackageSize!.Value,
                archived.ManifestSha256, archived.PackageSha256, result.ReleaseNotes,
                clock.GetUtcNow().ToUnixTimeMilliseconds(), archived.ArchiveId), token);
            if (!completed) logger.LogWarning("Validation task {TaskId} lease changed before completion", task.Id);
            return true;
        }
        catch (AdminFailure failure)
        {
            await queue.RejectAsync(task.Id, workerId, failure.Code, token);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogError(exception, "Validation task {TaskId} failed internally", task.Id);
            await queue.RejectAsync(task.Id, workerId, "validation_internal_error", token);
            return true;
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
