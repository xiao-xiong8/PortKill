using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using PortKiller.UpdateAdmin.Api;
using Microsoft.Data.Sqlite;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.UpdateManagement.Persistence;

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
        await fixture.App.StartAsync();
        fixture.Client = new HttpClient(fixture.App.GetTestServer().CreateHandler()) { BaseAddress = new Uri("https://localhost") };
        return fixture;
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
