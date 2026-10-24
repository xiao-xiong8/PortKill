using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PortKiller.UpdateManagement.Security;

public sealed class UploadCleanupWorker(IServiceScopeFactory scopes, ILogger<UploadCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await using var scope = scopes.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<UploadCleanupService>().CleanupAsync(stoppingToken);
                if (result.ExpiredUploads != 0 || result.StagingDirectoriesDeleted != 0 || result.ArchivesDeleted != 0)
                    logger.LogInformation("Upload cleanup expired {Expired} sessions, removed {Staging} staging directories and {Archives} archives",
                        result.ExpiredUploads, result.StagingDirectoriesDeleted, result.ArchivesDeleted);
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception) { logger.LogError(exception, "Upload cleanup worker stopped unexpectedly"); throw; }
    }
}
