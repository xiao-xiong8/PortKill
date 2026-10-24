using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Contracts;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.Updates;

namespace PortKiller.UpdateManagement.Security;

public sealed class PublishTaskExecutor(
    UpdateAdminDbContext database,
    PublishTaskQueue queue,
    ValidationArchiveService archive,
    UpdateToolClient tool,
    AdminOptions options,
    IServiceScopeFactory scopes,
    ILogger<PublishTaskExecutor> logger)
{
    private static readonly TimeSpan LeaseRenewalPeriod = TimeSpan.FromSeconds(20);

    public async Task<bool> ExecuteOnceAsync(string workerId, CancellationToken token)
    {
        using var executionLock = PublishExecutionLease.TryAcquire(options);
        if (executionLock == null) return false;
        PublishTaskContext? task = await queue.ClaimAsync(workerId, token);
        if (task == null) return false;
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var heartbeatStop = new CancellationTokenSource();
        Task heartbeat = MaintainLeaseAsync(task.Id, workerId, execution, heartbeatStop.Token);
        CancellationToken operation = execution.Token;

        try
        {
            // Recovery precedes both artifact checks and retry limits: the manifest may already be committed.
            if (await ReconcileAsync(task, workerId, operation) != Reconciliation.Baseline) return true;
            if (task.Attempts >= PublishTaskQueue.MaximumAttempts)
            {
                await queue.FailAsync(task.Id, workerId, "publish_retry_exhausted", operation);
                return true;
            }

            var draft = await database.ReleaseDrafts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == task.DraftId, operation);
            if (draft == null || draft.ValidationState != ValidationState.ReadyForApproval
                || draft.Application != task.Application || draft.Channel != task.Channel || draft.Runtime != "win-x64"
                || draft.ArchiveId != task.ArchiveId || draft.ArchiveCleanedUtcMs != null
                || draft.Version != task.Version || draft.Sequence != task.Sequence
                || !DigestEquals(draft.ManifestSha256, task.ManifestSha256) || !DigestEquals(draft.PackageSha256, task.PackageSha256))
            {
                await queue.ConflictAsync(task.Id, workerId, "publish_artifact_changed", operation);
                return true;
            }

            string manifestPath = archive.PathFor(task.ArchiveId, "manifest.envelope");
            string packagePath = archive.PathFor(task.ArchiveId, "package.zip");
            if (!File.Exists(manifestPath) || !File.Exists(packagePath)
                || !DigestEquals(await HashAsync(manifestPath, operation), task.ManifestSha256)
                || !DigestEquals(await HashAsync(packagePath, operation), task.PackageSha256))
            {
                await queue.ConflictAsync(task.Id, workerId, "archive_changed", operation);
                return true;
            }

            if (!await queue.BeginActivationAsync(task.Id, workerId, operation)) return true;
            operation.ThrowIfCancellationRequested();
            UpdateToolActivationResult result = await tool.ActivateAsync(manifestPath, packagePath, task.ExpectedManifestSha256, operation,
                task.ManifestSha256, task.PackageSha256);
            if (result.ErrorCode == "tool_termination_pending")
            {
                await queue.DeferRecoveryAsync(task.Id, workerId, result.ErrorCode, operation);
                return true;
            }

            // A timeout, malformed output or nonzero exit can all occur after the commit point.
            if (await ReconcileAsync(task, workerId, operation) != Reconciliation.Baseline) return true;
            if (result.Succeeded)
                await queue.DeferRecoveryAsync(task.Id, workerId, "activation_result_mismatch", operation);
            else
            {
                string code = result.ErrorCode ?? "publish_failed";
                if (code == "operation_conflict") await queue.ConflictAsync(task.Id, workerId, "publish_baseline_conflict", operation);
                else if (IsRetryable(code)) await queue.ReleaseAsync(task.Id, workerId, code, operation);
                else await queue.FailAsync(task.Id, workerId, code, operation);
            }
            return true;
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested)
        {
            // The tool adapter terminates and joins the child. Keep Publishing for the next fact check.
            token.ThrowIfCancellationRequested();
            return true;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Publish task {TaskId} requires recovery before its outcome can be recorded", task.Id);
            // Never convert a database write failure after activation into an uncommitted or failed release.
            database.ChangeTracker.Clear();
            await queue.DeferRecoveryAsync(task.Id, workerId, "publish_recovery_pending", token);
            return true;
        }
        finally
        {
            heartbeatStop.Cancel();
            await heartbeat;
        }
    }

    private async Task<Reconciliation> ReconcileAsync(PublishTaskContext task, string workerId, CancellationToken token)
    {
        UpdateToolInspectionResult current = await tool.RecoverAsync(token);
        if (!current.Succeeded)
        {
            await queue.DeferRecoveryAsync(task.Id, workerId, current.ErrorCode ?? "publish_recovery_pending", token);
            return Reconciliation.Pending;
        }

        if (current.HasCurrent && DigestEquals(current.ManifestSha256, task.ManifestSha256))
        {
            if (current.Version != task.Version || current.Sequence != task.Sequence || !DigestEquals(current.PackageSha256, task.PackageSha256))
            {
                await queue.DeferRecoveryAsync(task.Id, workerId, "activation_result_mismatch", token);
                return Reconciliation.Pending;
            }
            bool changed = await queue.CompleteAsync(task.Id, workerId,
                new(true, null, current.Version, current.Sequence, current.ExpiresUtc, current.ManifestSha256,
                    current.PackageSha256, current.ReceiptSha256), token);
            if (!changed) logger.LogWarning("Publish task {TaskId} lease changed before the committed manifest was recorded", task.Id);
            return Reconciliation.Finished;
        }

        if (current.HasCurrent && current.Sequence > task.Sequence
            && UpdateManifest.ParseVersion(current.Version!) >= UpdateManifest.ParseVersion(task.Version))
        {
            await queue.SupersedeAsync(task.Id, workerId, token);
            return Reconciliation.Finished;
        }

        if (!string.Equals(current.HasCurrent ? current.ManifestSha256 : "none", task.ExpectedManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            await queue.ConflictAsync(task.Id, workerId, "publish_baseline_conflict", token);
            return Reconciliation.Finished;
        }
        return Reconciliation.Baseline;
    }

    private async Task MaintainLeaseAsync(string taskId, string workerId, CancellationTokenSource execution, CancellationToken token)
    {
        using var timer = new PeriodicTimer(LeaseRenewalPeriod);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                // EF contexts cannot be used concurrently by the executor and its heartbeat.
                await using var scope = scopes.CreateAsyncScope();
                var heartbeatQueue = scope.ServiceProvider.GetRequiredService<PublishTaskQueue>();
                if (!await heartbeatQueue.RenewAsync(taskId, workerId, token))
                {
                    logger.LogWarning("Publish task {TaskId} lease was lost while executing", taskId);
                    execution.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Publish task {TaskId} lease renewal failed; stopping its tool", taskId);
            execution.Cancel();
        }
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

    private enum Reconciliation { Baseline, Finished, Pending }
}
