using PortKiller.UpdatePublisher.Worker;
using PortKiller.UpdateManagement.Configuration;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{ Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Services.AddSingleton(AdminHostConfiguration.Load(builder.Configuration, args));
builder.Services.AddHostedService<PublisherWorker>();

var host = builder.Build();
host.Run();
