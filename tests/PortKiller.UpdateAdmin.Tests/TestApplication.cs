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
using System.Net.Http.Json;
using System.Text.Json;

namespace PortKiller.UpdateAdmin.Tests;

internal sealed class TestApplication : IAsyncDisposable
{
    private static readonly string TestRoot = Path.Combine(Path.GetTempPath(), "PortKillAdminTests");
    public string Root { get; } = Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
    public WebApplication App { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public ManualClock Clock { get; } = new();
    private readonly CookieContainer _defaultCookies = new();
    public string DefaultCookieHeader => _defaultCookies.GetCookieHeader(new Uri("https://localhost"));

    public static async Task<TestApplication> StartAsync()
    {
        var fixture = new TestApplication();
        fixture.App = fixture.CreateApp();
        using (var scope = fixture.App.Services.CreateScope())
            await AdminDatabase.MigrateAsync(scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>());
        await fixture.App.StartAsync();
        fixture.Client = fixture.NewClient(fixture._defaultCookies);
        return fixture;
    }

    private WebApplication CreateApp() => AdminApiHost.Create(["--Administration:DataRoot", Root, "--urls", "https://localhost:0"], builder =>
    { builder.WebHost.UseTestServer(); builder.Services.AddSingleton<TimeProvider>(Clock); });
    public HttpClient NewClient(CookieContainer? cookies = null, bool autoCsrf = true) => new(new TestCookieHandler(App.GetTestServer().CreateHandler(), cookies ?? new(), autoCsrf)) { BaseAddress = new Uri("https://localhost") };
    public async Task RestartAsync()
    {
        Client.Dispose(); await App.DisposeAsync();
        App = CreateApp(); await App.StartAsync(); Client = NewClient(_defaultCookies);
    }
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

internal sealed class TestCookieHandler(HttpMessageHandler inner, CookieContainer cookies, bool autoCsrf) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (autoCsrf && request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
        {
            using var csrfRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(request.RequestUri!, "/api/v1/auth/csrf"));
            AddCookies(csrfRequest);
            using var csrfResponse = await base.SendAsync(csrfRequest, token); StoreCookies(csrfRequest, csrfResponse);
            csrfResponse.EnsureSuccessStatusCode();
            string csrf = (await csrfResponse.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("requestToken").GetString()!;
            request.Headers.Add("X-PortKill-CSRF", csrf);
        }
        AddCookies(request);
        var response = await base.SendAsync(request, token); StoreCookies(request, response); return response;
    }
    private void AddCookies(HttpRequestMessage request)
    {
        string cookieHeader = cookies.GetCookieHeader(request.RequestUri!);
        if (cookieHeader.Length != 0) { request.Headers.Remove("Cookie"); request.Headers.Add("Cookie", cookieHeader); }
    }
    private void StoreCookies(HttpRequestMessage request, HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Set-Cookie", out var values))
            foreach (string value in values) cookies.SetCookies(request.RequestUri!, value);
    }
}

internal sealed class ManualClock : TimeProvider
{
    private long _ticks = DateTimeOffset.UtcNow.UtcTicks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    public void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
}
