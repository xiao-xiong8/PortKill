using PortKiller.UpdateManagement.Security;

namespace PortKiller.UpdateAdmin.Api.Authentication;

public sealed class SessionMaintenanceService(IServiceScopeFactory scopes, ILogger<SessionMaintenanceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<AdminSessionService>().CleanupAsync(stoppingToken);
                    await scope.ServiceProvider.GetRequiredService<AdminAuditService>().PruneAsync(stoppingToken);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                { logger.LogWarning(error, "Session cleanup will be retried at the next maintenance interval"); }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
