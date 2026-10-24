using PortKiller.UpdateManagement;

namespace PortKiller.UpdatePublisher.Worker;

public sealed class PublisherWorker(ILogger<PublisherWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("{Application} publisher {Version} started", AdminApplication.Name, AdminApplication.Version);
        try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
