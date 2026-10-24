using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using PortKiller.UpdateAdmin.Api;
using Microsoft.Data.Sqlite;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Persistence;
using Microsoft.Extensions.DependencyInjection;
using PortKiller.UpdateManagement.Security;
using System.Net;

namespace PortKiller.UpdateAdmin.Tests;

internal sealed class TestApplication : IAsyncDisposable
{
    private static readonly string TestRoot = Path.Combine(Path.GetTempPath(), "PortKillAdminTests");
    public string Root { get; } = Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
    public WebApplication App { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;

    public static async Task<TestApplication> StartAsync()
    {
        var fixture = new TestApplication();
        fixture.App = AdminApiHost.Create(["--Administration:DataRoot", fixture.Root, "--urls", "https://localhost:0"],
            builder => builder.WebHost.UseTestServer());
        using (var scope = fixture.App.Services.CreateScope())
            await AdminDatabase.MigrateAsync(scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>());
        await fixture.App.StartAsync();
        fixture.Client = fixture.NewClient();
        return fixture;
    }

    public HttpClient NewClient() => new(new TestCookieHandler(App.GetTestServer().CreateHandler())) { BaseAddress = new Uri("https://localhost") };
    public async Task<string> SeedAdminAsync(string name = "operator", string password = "Test-Only-Password!492")
    {
        using var scope = App.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AdminBootstrap>().CreateAsync(name, password);
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (App != null) await App.DisposeAsync();
        using (var connection = new SqliteConnection(AdminDatabase.ConnectionString(new AdminOptions { DataRoot = Root })))
            SqliteConnection.ClearPool(connection);
        string full = Path.GetFullPath(Root);
        if (!full.StartsWith(Path.GetFullPath(TestRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test cleanup outside sandbox");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}

internal sealed class TestCookieHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private readonly CookieContainer _cookies = new();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        string cookies = _cookies.GetCookieHeader(request.RequestUri!);
        if (cookies.Length != 0) { request.Headers.Remove("Cookie"); request.Headers.Add("Cookie", cookies); }
        var response = await base.SendAsync(request, token);
        if (response.Headers.TryGetValues("Set-Cookie", out var values))
            foreach (string value in values) _cookies.SetCookies(request.RequestUri!, value);
        return response;
    }
}
