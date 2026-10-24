using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Contracts;
using PortKiller.UpdateManagement.Persistence;

namespace PortKiller.UpdateManagement.Security;

public sealed class PublishTaskExecutor(
    UpdateAdminDbContext database,
    PublishTaskQueue queue,
    ValidationArchiveService archive,
    UpdateToolClient tool,
    ILogger<PublishTaskExecutor> logger)
{
    private static readonly TimeSpan LeaseRenewalPeriod = TimeSpan.FromSeconds(20);

    public async Task<bool> ExecuteOnceAsync(string workerId, CancellationToken token)
    {
        PublishTaskContext? task = await queue.ClaimAsync(workerId, token);
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
            var draft = await database.ReleaseDrafts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == task.DraftId, token);
            if (draft == null || draft.ValidationState != ValidationState.ReadyForApproval
                || draft.Version != task.Version || draft.Sequence != task.Sequence
                || !DigestEquals(draft.ManifestSha256, task.ManifestSha256) || !DigestEquals(draft.PackageSha256, task.PackageSha256))
            {
                await StopLeaseHeartbeatAsync();
                await queue.ConflictAsync(task.Id, workerId, "publish_artifact_changed", token);
                return true;
            }

            string manifestPath = archive.PathFor(task.ArchiveId, "manifest.envelope");
            string packagePath = archive.PathFor(task.ArchiveId, "package.zip");
            if (!File.Exists(manifestPath) || !File.Exists(packagePath)
                || !DigestEquals(await HashAsync(manifestPath, token), task.ManifestSha256)
                || !DigestEquals(await HashAsync(packagePath, token), task.PackageSha256))
            {
                await StopLeaseHeartbeatAsync();
                await queue.ConflictAsync(task.Id, workerId, "archive_changed", token);
                return true;
            }

            UpdateToolActivationResult result = await tool.ActivateAsync(manifestPath, packagePath, task.ExpectedManifestSha256, token,
                task.ManifestSha256, task.PackageSha256);
            if (!result.Succeeded)
            {
                await StopLeaseHeartbeatAsync();
                string error = result.ErrorCode ?? "publish_failed";
                bool changed = error == "operation_conflict"
                    ? await queue.ConflictAsync(task.Id, workerId, "publish_baseline_conflict", token)
                    : IsRetryable(error)
                        ? await queue.ReleaseAsync(task.Id, workerId, error, token)
                        : await queue.FailAsync(task.Id, workerId, error, token);
                if (!changed) logger.LogWarning("Publish task {TaskId} lease changed before failure was recorded", task.Id);
                return true;
            }

            if (!string.Equals(result.Version, task.Version, StringComparison.Ordinal)
                || result.Sequence != task.Sequence || !DigestEquals(result.ManifestSha256, task.ManifestSha256)
                || !DigestEquals(result.PackageSha256, task.PackageSha256))
            {
                await StopLeaseHeartbeatAsync();
                bool changed = await queue.ConflictAsync(task.Id, workerId, "activation_result_mismatch", token);
                if (!changed) logger.LogWarning("Publish task {TaskId} lease changed before mismatch was recorded", task.Id);
                return true;
            }

            await StopLeaseHeartbeatAsync();
            bool completed = await queue.CompleteAsync(task.Id, workerId, result, token);
            if (!completed) logger.LogWarning("Publish task {TaskId} lease changed before activation was recorded", task.Id);
            return true;
        }
        catch (AdminFailure failure)
        {
            await StopLeaseHeartbeatAsync();
            bool changed = failure.Code is "invalid_storage_path" or "archive_conflict" or "upload_storage_missing"
                ? await queue.ConflictAsync(task.Id, workerId, failure.Code, token)
                : await queue.ReleaseAsync(task.Id, workerId, failure.Code, token);
            if (!changed) logger.LogWarning("Publish task {TaskId} lease changed before failure was recorded", task.Id);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (IOException exception)
        {
            logger.LogWarning(exception, "Publish task {TaskId} encountered storage I/O failure", task.Id);
            await StopLeaseHeartbeatAsync();
            bool changed = await queue.ReleaseAsync(task.Id, workerId, "io_failure", token);
            if (!changed) logger.LogWarning("Publish task {TaskId} lease changed before I/O failure was recorded", task.Id);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Publish task {TaskId} failed internally", task.Id);
            await StopLeaseHeartbeatAsync();
            bool changed = await queue.ReleaseAsync(task.Id, workerId, "publish_internal_error", token);
            if (!changed) logger.LogWarning("Publish task {TaskId} lease changed before internal failure was recorded", task.Id);
            return true;
        }
        finally { await StopLeaseHeartbeatAsync(); }
    }

    private async Task MaintainLeaseAsync(string taskId, string workerId, CancellationToken token)
    {
        using var timer = new PeriodicTimer(LeaseRenewalPeriod);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                if (!await queue.RenewAsync(taskId, workerId, token))
                {
                    logger.LogWarning("Publish task {TaskId} lease was lost while executing", taskId);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { logger.LogWarning(exception, "Publish task {TaskId} lease renewal stopped", taskId); }
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, token));
    }

    private static bool DigestEquals(string? left, string right)
        => left != null && left.Equals(right, StringComparison.OrdinalIgnoreCase);

    private static bool IsRetryable(string code)
        => code is "tool_unavailable" or "tool_timeout" or "file_unavailable" or "io_failure"
            or "internal_error" or "remote_check_failed" or "publish_internal_error";
}
