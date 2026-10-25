using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.Updates;

namespace PortKiller.UpdateManagement.Security;

public sealed class PublicVerificationTaskExecutor(PublicVerificationTaskQueue queue, UpdateToolClient tool,
    AdminOptions options, IServiceScopeFactory scopes, ILogger<PublicVerificationTaskExecutor> logger)
{
    public async Task<bool> ExecuteOnceAsync(string workerId, CancellationToken token)
    {
        using var executionLock = PublishExecutionLease.TryAcquire(options);
        if (executionLock == null) return false;
        PublicVerificationTask? task = await queue.ClaimAsync(workerId, token);
        if (task == null) return false;
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var heartbeatStop = new CancellationTokenSource();
        Task heartbeat = MaintainLeaseAsync(task.Id, workerId, execution, heartbeatStop.Token);
        try
        {
            CancellationToken operation = execution.Token;
            if (!await ReconcileAsync(task, workerId, operation)) return true;
            UpdateToolPublicCheckResult result = await tool.PublicCheckAsync(task.ManifestSha256, operation);
            // The public source can change during a long download. Its result belongs only to this digest.
            if (!await ReconcileAsync(task, workerId, operation)) return true;
            bool succeeded = result.Succeeded && result.Version == task.Version && result.Sequence == task.Sequence
                && DigestEquals(result.ManifestSha256, task.ManifestSha256) && DigestEquals(result.PackageSha256, task.PackageSha256);
            await queue.CompleteAsync(task.Id, workerId, succeeded,
                succeeded ? null : result.ErrorCode ?? "public_check_result_mismatch", operation);
            return true;
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested)
        {
            // An interrupted check is reclaimed after expiry; it can never reactivate a release.
            token.ThrowIfCancellationRequested();
            return true;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Public check {TaskId} interrupted; its lease permits check-only recovery", task.Id);
            return true;
        }
        finally { heartbeatStop.Cancel(); await heartbeat; }
    }

    private async Task<bool> ReconcileAsync(PublicVerificationTask task, string workerId, CancellationToken token)
    {
        // Inspect verifies the local package but has no activation or receipt-writing side effects.
        UpdateToolInspectionResult current = await tool.InspectAsync(token);
        if (!current.Succeeded || !current.HasCurrent)
        {
            await queue.CompleteAsync(task.Id, workerId, false, "release_source_unavailable", token);
            return false;
        }
        if (DigestEquals(current.ManifestSha256, task.ManifestSha256) && current.Version == task.Version
            && current.Sequence == task.Sequence && DigestEquals(current.PackageSha256, task.PackageSha256)) return true;
        if (current.Sequence > task.Sequence && UpdateManifest.ParseVersion(current.Version!) >= UpdateManifest.ParseVersion(task.Version))
            await queue.SupersedeAsync(task.Id, workerId, token);
        else await queue.CompleteAsync(task.Id, workerId, false, "public_check_baseline_changed", token);
        return false;
    }

    private async Task MaintainLeaseAsync(string id, string workerId, CancellationTokenSource execution, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                await using var scope = scopes.CreateAsyncScope();
                if (!await scope.ServiceProvider.GetRequiredService<PublicVerificationTaskQueue>().RenewAsync(id, workerId, token))
                { execution.Cancel(); return; }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Public check {TaskId} lost its lease; stopping its tool", id);
            execution.Cancel();
        }
    }

    private static bool DigestEquals(string? left, string right) => left?.Equals(right, StringComparison.OrdinalIgnoreCase) == true;
}
