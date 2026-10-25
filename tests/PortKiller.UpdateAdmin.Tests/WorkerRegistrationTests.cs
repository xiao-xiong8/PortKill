using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Security;

namespace PortKiller.UpdateAdmin.Tests;

public sealed class WorkerRegistrationTests
{
    [Fact]
    public void PublisherWorkerDependenciesCanBeResolvedFromAHostedServiceContainer()
    {
        var options = new AdminOptions
        {
            DataRoot = Path.Combine(Path.GetTempPath(), "PortKill Worker Registration Tests", Guid.NewGuid().ToString("N"))
        };
        options.Validate();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(options);
        services.AddAdminDatabase(options);
        services.AddAdminWorkerServices();
        services.AddSingleton(new UpdateToolOptions { Enabled = false });
        services.AddSingleton<UpdateToolClient>();

        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ValidationTaskExecutor>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<PublishTaskExecutor>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<PublishTaskQueue>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<PublicVerificationTaskQueue>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<PublicVerificationTaskExecutor>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<UploadCleanupService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ValidationTaskQueue>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ValidationArchiveService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<TimeProvider>());
    }
}
