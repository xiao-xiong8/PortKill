using PortKiller.UpdatePublisher.Worker;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Persistence;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{ Args = args, ContentRootPath = AppContext.BaseDirectory });
var options = AdminHostConfiguration.Load(builder.Configuration, args);
builder.Services.AddSingleton(options);
builder.Services.AddAdminDatabase(options);
builder.Services.AddHostedService<AdminRuntimeService>();
builder.Services.AddHostedService<PublisherWorker>();

var host = builder.Build();
host.Run();
