using PortKiller.UpdatePublisher.Worker;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Security;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{ Args = args, ContentRootPath = AppContext.BaseDirectory });
var options = AdminHostConfiguration.Load(builder.Configuration, args);
var updateToolOptions = new UpdateToolOptions();
builder.Configuration.GetSection("UpdateTool").Bind(updateToolOptions);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(updateToolOptions);
builder.Services.AddAdminDatabase(options);
builder.Services.AddHostedService<AdminRuntimeService>();
builder.Services.AddScoped<PortKiller.UpdateManagement.Security.ValidationTaskQueue>();
builder.Services.AddSingleton<UpdateToolClient>();
builder.Services.AddHostedService<ValidationTaskWorker>();
builder.Services.AddHostedService<UploadCleanupWorker>();
builder.Services.AddHostedService<PublisherWorker>();

var host = builder.Build();
host.Run();
