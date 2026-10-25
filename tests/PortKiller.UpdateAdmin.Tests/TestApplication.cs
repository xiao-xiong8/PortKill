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
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PortKiller.UpdateAdmin.Tests;

internal sealed class TestApplication : IAsyncDisposable
{
    private static readonly string TestRoot = Path.Combine(Path.GetTempPath(), "PortKillAdminTests");
    public string Root { get; } = Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
    public WebApplication App { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public ManualClock Clock { get; } = new();
    public CapturedLogs Logs { get; } = new();
    private readonly CookieContainer _defaultCookies = new();
    private Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? _configureServices;
    private bool _realHttp;
    private X509Certificate2? _certificate;
    private Uri _address = new("https://localhost");
    public string DefaultCookieHeader => _defaultCookies.GetCookieHeader(_address);

    public static async Task<TestApplication> StartAsync(Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? configureServices = null,
        bool realHttp = false)
    {
        var fixture = new TestApplication { _configureServices = configureServices, _realHttp = realHttp };
        if (realHttp)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            // Windows Schannel needs an imported key rather than the self-signed certificate's ephemeral key handle.
            fixture._certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null,
                X509KeyStorageFlags.Exportable);
        }
        fixture.App = fixture.CreateApp();
        using (var scope = fixture.App.Services.CreateScope())
            await AdminDatabase.MigrateAsync(scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>());
        await fixture.App.StartAsync();
        if (realHttp) fixture._address = new Uri(fixture.App.Urls.Single());
        fixture.Client = fixture.NewClient(fixture._defaultCookies);
        return fixture;
    }

    private WebApplication CreateApp() => AdminApiHost.Create(["--Administration:DataRoot", Root, "--urls",
        _realHttp ? "https://127.0.0.1:0" : "https://localhost:0"], builder =>
    { if (_realHttp) builder.WebHost.ConfigureKestrel(options => options.ConfigureHttpsDefaults(https => https.ServerCertificate = _certificate));
      else builder.WebHost.UseTestServer();
      builder.Services.AddSingleton<TimeProvider>(Clock); builder.Logging.AddProvider(Logs);
      _configureServices?.Invoke(builder.Services); });
    public HttpClient NewClient(CookieContainer? cookies = null, bool autoCsrf = true) => new(new TestCookieHandler(
        _realHttp ? new HttpClientHandler { UseCookies = false,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator }
            : App.GetTestServer().CreateHandler(), cookies ?? new(), autoCsrf)) { BaseAddress = _address };
    public async Task RestartAsync()
    {
        Client.Dispose(); await App.StopAsync(); await App.DisposeAsync();
        App = CreateApp(); await App.StartAsync();
        if (_realHttp) _address = new Uri(App.Urls.Single());
        Client = NewClient(_defaultCookies);
    }
    public async Task StopAsync() { Client.Dispose(); await App.StopAsync(); }
    public async Task<string> SeedAdminAsync(string name = "operator", string password = "Test-Only-Password!492")
    {
        using var scope = App.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AdminBootstrap>().CreateAsync(name, password);
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (App != null) { await App.StopAsync(); await App.DisposeAsync(); }
        _certificate?.Dispose();
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

internal sealed class CapturedLogs : ILoggerProvider
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _entries = new();
    public string Text => string.Join('\n', _entries);
    public ILogger CreateLogger(string categoryName) => new Collector(_entries);
    public void Dispose() { }
    private sealed class Collector(System.Collections.Concurrent.ConcurrentQueue<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => entries.Enqueue(formatter(state, exception) + exception?.ToString());
    }
}
