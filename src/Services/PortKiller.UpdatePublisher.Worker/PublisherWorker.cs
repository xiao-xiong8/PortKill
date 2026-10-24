using Microsoft.Extensions.DependencyInjection;
using PortKiller.UpdateManagement;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Security;

namespace PortKiller.UpdatePublisher.Worker;

public sealed class PublisherWorker(
    IServiceScopeFactory scopes,
    UpdateToolOptions options,
    ILogger<PublisherWorker> logger) : BackgroundService
{
    private readonly string _workerId = Environment.MachineName + ".publisher." + Guid.NewGuid().ToString("N");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("{Application} publisher {Version} started", AdminApplication.Name, AdminApplication.Version);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                bool processed = false;
                if (options.Enabled)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    processed = await scope.ServiceProvider.GetRequiredService<PublishTaskExecutor>()
                        .ExecuteOnceAsync(_workerId, stoppingToken);
                }
                await Task.Delay(processed ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception) { logger.LogCritical(exception, "Publisher worker stopped unexpectedly"); throw; }
    }
}
