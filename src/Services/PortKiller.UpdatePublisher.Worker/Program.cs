using PortKiller.UpdatePublisher.Worker;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Security;

bool selfCheckToolOwner = args is ["--self-check-tool-owner"];
string[] hostArgs = selfCheckToolOwner ? [] : args;
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{ Args = hostArgs, ContentRootPath = AppContext.BaseDirectory });
if (selfCheckToolOwner)
{
    // This offline diagnostic uses only tool configuration and an isolated temporary root.
    // Administration settings and database mounts belong to the normal hosted worker below.
    var checkOptions = new UpdateToolOptions();
    builder.Configuration.GetSection("UpdateTool").Bind(checkOptions);
    string temporary = Path.Combine(Path.GetTempPath(), "portkill-publisher-check-" + Guid.NewGuid().ToString("N"));
    try
    {
        checkOptions.Enabled = true;
        checkOptions.ReleaseRoot = Path.Combine(temporary, "stable");
        var result = await new UpdateToolClient(checkOptions).InspectAsync(default);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            Check = "publisher-tool-owner", SelfCheckSchema = 2, result.Succeeded, result.ErrorCode, result.HasCurrent
        }));
        return result.Succeeded && !result.HasCurrent ? 0 : 1;
    }
    finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
}
var options = AdminHostConfiguration.Load(builder.Configuration, hostArgs);
var updateToolOptions = new UpdateToolOptions();
builder.Configuration.GetSection("UpdateTool").Bind(updateToolOptions);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(updateToolOptions);
builder.Services.AddAdminDatabase(options);
builder.Services.AddHostedService<AdminRuntimeService>();
builder.Services.AddAdminWorkerServices();
builder.Services.AddSingleton<UpdateToolClient>();
builder.Services.AddHostedService<ValidationTaskWorker>();
builder.Services.AddHostedService<UploadCleanupWorker>();
builder.Services.AddHostedService<PublisherWorker>();

var host = builder.Build();
host.Run();
return 0;
