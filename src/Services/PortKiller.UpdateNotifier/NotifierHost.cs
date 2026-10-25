using System.Text.Json;
using PortKiller.Updates;

namespace PortKiller.UpdateNotifier;

public static class NotifierHost
{
    public static WebApplication Create(string root, ManifestVerificationOptions options, string url = "http://127.0.0.1:8091")
    {
        options.Validate();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(url);
        builder.Logging.ClearProviders(); builder.Logging.AddJsonConsole();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var monitor = new ReleaseMonitor(root, options);
        builder.Services.AddSingleton(monitor);
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ReleaseMonitor>());
        var app = builder.Build();
        app.MapGet("/health", () => Results.Json(monitor.Health()));
        app.MapGet("/stable/events", async (HttpContext context) =>
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            var connection = monitor.Subscribe();
            try
            {
                await context.Response.StartAsync(context.RequestAborted);
                while (!context.RequestAborted.IsCancellationRequested)
                {
                    ReleaseHint? hint = null;
                    using (var wait = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted))
                    {
                        wait.CancelAfter(TimeSpan.FromSeconds(25));
                        try { hint = await connection.Reader.ReadAsync(wait.Token); }
                        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested) { }
                    }
                    string message = hint == null ? ": heartbeat\n\n" : $"id: {hint.Sequence}\nevent: release\ndata: {JsonSerializer.Serialize(hint, ProtocolJsonContext.Default.ReleaseHint)}\n\n";
                    using var write = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                    write.CancelAfter(TimeSpan.FromSeconds(5));
                    await context.Response.WriteAsync(message, write.Token);
                    await context.Response.Body.FlushAsync(write.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
            finally { monitor.Unsubscribe(connection.Id); }
        });
        return app;
    }
}
