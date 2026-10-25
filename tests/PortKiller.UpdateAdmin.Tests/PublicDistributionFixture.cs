using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PortKiller.UpdateAdmin.Tests;

/// <summary>A real loopback distribution endpoint that the separately running tool can reach.</summary>
internal sealed class PublicDistributionFixture : IAsyncDisposable
{
    public PublishFixture Publication { get; private set; } = null!;
    private WebApplication _server = null!;
    public string Mode { get; set; } = "healthy";
    public byte[]? ManifestOverride { get; set; }
    public int RedirectHits;
    public int ManifestHits;
    public int PackageHits;
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Disconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static async Task<PublicDistributionFixture> StartAsync(bool realHttp = false, string? preparedDirectory = null)
    {
        var fixture = new PublicDistributionFixture();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        fixture._server = builder.Build();
        fixture._server.MapGet("/stable/manifest.json", fixture.ManifestAsync);
        fixture._server.MapGet("/stable/{name}", fixture.PackageAsync);
        fixture._server.MapGet("/redirect-target", () => { Interlocked.Increment(ref fixture.RedirectHits); return Results.Ok(); });
        await fixture._server.StartAsync();
        string address = fixture._server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        fixture.Publication = await PublishFixture.StartAsync(address + "/stable/manifest.json", realHttp, preparedDirectory);
        return fixture;
    }

    private async Task ManifestAsync(HttpContext context)
    {
        Interlocked.Increment(ref ManifestHits);
        switch (Mode)
        {
            case "missing": context.Response.StatusCode = 404; return;
            case "unavailable": context.Response.StatusCode = 503; return;
            case "manifest-redirect": context.Response.Redirect("/redirect-target"); return;
            case "manifest-stall": await StallAsync(context); return;
            case "manifest-large": await context.Response.Body.WriteAsync(new byte[65_537], context.RequestAborted); return;
            case "manifest-corrupt": await context.Response.Body.WriteAsync(new byte[] { 1, 2, 3 }, context.RequestAborted); return;
        }
        byte[] bytes = ManifestOverride ?? await File.ReadAllBytesAsync(Path.Combine(Publication.Options.ReleaseRoot, "manifest.json"), context.RequestAborted);
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
    }

    private async Task PackageAsync(HttpContext context)
    {
        Interlocked.Increment(ref PackageHits);
        string name = (string)context.Request.RouteValues["name"]!;
        if (name != $"PortKiller-{Publication.Version}-win-x64.zip") { context.Response.StatusCode = 404; return; }
        bool range = context.Request.Headers.Range.Count != 0;
        if (Mode == "package-redirect" || range && Mode == "range-redirect")
        { context.Response.Redirect("/redirect-target"); return; }
        if (Mode == "package-stall" || range && Mode == "range-stall") { await StallAsync(context); return; }
        if (range && Mode == "hold-range")
        {
            Entered.TrySetResult();
            await Continue.Task.WaitAsync(context.RequestAborted);
        }
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(Publication.Options.ReleaseRoot, name), context.RequestAborted);
        if (Mode == "package-corrupt") bytes[0] ^= 0x01;
        if (Mode == "package-large") bytes = [..bytes, 1];
        if (range && Mode != "no-range")
        {
            context.Response.StatusCode = 206;
            context.Response.Headers.ContentRange = $"bytes 0-0/{bytes.Length}";
            if (Mode == "range-length") context.Response.Headers.ContentRange = $"bytes 0-0/{bytes.Length + 1}";
            byte[] one = Mode == "range-wrong" ? [(byte)(bytes[0] ^ 0x01)] : [bytes[0]];
            await context.Response.Body.WriteAsync(one, context.RequestAborted);
        }
        else await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
    }

    private async Task StallAsync(HttpContext context)
    {
        // Send headers first: HttpClient's header timeout alone cannot protect this case.
        await context.Response.StartAsync(context.RequestAborted);
        Entered.TrySetResult();
        try { await Continue.Task.WaitAsync(context.RequestAborted); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { Disconnected.TrySetResult(); }
    }

    public async Task<string> PublishAsync(long sequence = 1)
    {
        var upload = await Publication.UploadAsync(Publication.Sign(sequence));
        string id = await Publication.ConfirmAsync(upload.DraftId);
        Assert.True(await Publication.ExecuteAsync());
        return id;
    }

    public async ValueTask DisposeAsync()
    {
        Continue.TrySetResult();
        await _server.DisposeAsync();
        if (Publication != null) await Publication.DisposeAsync();
    }
}
