using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PortKiller.UpdateNotifier;
using PortKiller.Updates;
using static PortKiller.UpdateRegression.AssertEx;

namespace PortKiller.UpdateRegression;

internal static class TransportChecks
{
    internal static async Task ProxySelection()
    {
        That(JsonSerializer.Deserialize("{}", UpdateJsonContext.Default.UpdateOptions)!.UseSystemProxy,
            "Legacy configuration stopped following the system proxy");
        using var fixture = new Fixture();
        await using var app = await fixture.HostAsync();
        var staged = fixture.Stage("1.0.0", 1, fixture.Package());
        File.Copy(staged.Manifest, Path.Combine(fixture.Public, "manifest.json"));
        File.Copy(staged.Package, Path.Combine(fixture.Public, Path.GetFileName(staged.Package)));

        int proxyRequests = 0;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var proxyApp = builder.Build();
        proxyApp.Run(context =>
        {
            Interlocked.Increment(ref proxyRequests);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        });
        await proxyApp.StartAsync();
        string proxyAddress = proxyApp.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var previousProxy = HttpClient.DefaultProxy;
        var proxy = new ScopedProxy(new WebProxy(proxyAddress) { BypassProxyOnLocal = false }, previousProxy);
        HttpClient.DefaultProxy = proxy;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var proxied = new UpdateService(fixture.Options, Path.Combine(fixture.Root, "proxied-cache"), current: "0.1",
                trustPath: Path.Combine(fixture.Root, "proxied-trust.json"));
            await proxied.CheckAsync(deadline.Token, false);
            That(proxyRequests == 1 && proxied.Snapshot.Stage == UpdateStage.Failed && proxied.Snapshot.Failure == UpdateFailure.Server,
                "System proxy was ignored or a failed proxy silently fell back to direct access");

            fixture.Options.UseSystemProxy = false;
            File.WriteAllText(Path.Combine(fixture.Root, "update-settings.json"), JsonSerializer.Serialize(fixture.Options, UpdateJsonContext.Default.UpdateOptions));
            var directOptions = UpdateOptions.Load(fixture.Root);
            That(!directOptions.UseSystemProxy, "Direct connection setting was lost during configuration loading");
            var clock = new TestClock();
            var direct = new UpdateService(directOptions, Path.Combine(fixture.Root, "direct-cache"), current: "0.1", clock: clock,
                trustPath: Path.Combine(fixture.Root, "direct-trust.json"));
            await direct.CheckAsync(deadline.Token);
            That(direct.Snapshot.Stage == UpdateStage.Ready && proxyRequests == 1,
                "Explicit direct access did not fetch and verify the manifest/package independently of the system proxy");

            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var run = direct.RunAsync(lifetime.Token);
            try
            {
                await Task.Delay(100, deadline.Token);
                clock.Advance(); direct.NotifyEnvironmentChanged();
                var monitor = app.Services.GetRequiredService<ReleaseMonitor>();
                await Eventually(() => monitor.Connections == 1);
                That(proxyRequests == 1, "Direct update service sent SSE through the system proxy");
            }
            finally { lifetime.Cancel(); await run; }
        }
        finally
        {
            proxy.Restore();
            HttpClient.DefaultProxy = previousProxy;
        }
    }

    // Shared HttpClient handlers may retain this object after HttpClient.DefaultProxy is restored.
    private sealed class ScopedProxy(IWebProxy testProxy, IWebProxy previousProxy) : IWebProxy
    {
        private IWebProxy _target = testProxy;
        internal void Restore() => Volatile.Write(ref _target, previousProxy);
        public Uri? GetProxy(Uri destination) => Volatile.Read(ref _target).GetProxy(destination);
        public bool IsBypassed(Uri host) => Volatile.Read(ref _target).IsBypassed(host);
        public ICredentials? Credentials
        {
            get => Volatile.Read(ref _target).Credentials;
            set => Volatile.Read(ref _target).Credentials = value;
        }
    }
}
