using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using System.Globalization;

namespace PortKiller.UpdateAdmin.Api;

public static class ContractExporter
{
    public static async Task ExportAsync(string output)
    {
        await using var app = AdminApiHost.Create(["--urls", "http://127.0.0.1:0"]);
        await app.StartAsync();
        try
        {
            await using var scope = app.Services.CreateAsyncScope();
            var provider = scope.ServiceProvider.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1");
            var document = await provider.GetOpenApiDocumentAsync();
            document.Info.Title = "PortKill Update Administration API";
            document.Info.Version = "v1";
            document.Servers = [];
            using var buffer = new StringWriter(CultureInfo.InvariantCulture);
            var writer = new OpenApiJsonWriter(buffer);
            document.SerializeAsV3(writer);
            await writer.FlushAsync();
            string path = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, buffer.ToString() + "\n", new System.Text.UTF8Encoding(false));
        }
        finally { await app.StopAsync(); }
    }
}
