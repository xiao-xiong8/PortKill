using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PortKiller.UpdateManagement;
using PortKiller.UpdateManagement.Configuration;
using PortKiller.Updates;

namespace PortKiller.UpdateAdmin.Tests;

public sealed class SkeletonTests
{
    [Fact]
    public async Task HealthIsPublicAndContainsNoRuntimePaths()
    {
        await using var app = await TestApplication.StartAsync();
        var response = await app.Client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", json.GetProperty("status").GetString());
        Assert.Equal(2, json.EnumerateObject().Count());
    }

    [Fact]
    public async Task ReadyHealthIsBoundedAndSecretFree()
    {
        await using var app = await TestApplication.StartAsync();
        var response = await app.Client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(app.Root, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cookie", body, StringComparison.OrdinalIgnoreCase);
        var json = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Equal("admin-api", json.GetProperty("component").GetString());
        Assert.Equal("healthy", json.GetProperty("database").GetString());
        Assert.Equal(0, json.GetProperty("queue").GetProperty("active").GetInt32());
        Assert.True(json.GetProperty("manifest").GetProperty("status").GetString() is "missing" or "unavailable");
    }

    [Theory]
    [InlineData("/api/v1/system/status")]
    [InlineData("/api/v1/system/capabilities")]
    public async Task ManagementEndpointsDenyAnonymousRequests(string route)
    {
        await using var app = await TestApplication.StartAsync();
        var response = await app.Client.GetAsync(route);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("authentication_required", problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("correlationId").GetString()));
        Assert.DoesNotContain(app.Root, problem.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ManagementRouteAnonymousAllowlistHasNoUnexpectedBypass()
    {
        await using var app = await TestApplication.StartAsync();
        var dataSources = app.App.Services.GetRequiredService<IEnumerable<EndpointDataSource>>();
        var routes = dataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/v1", StringComparison.Ordinal) == true)
            .ToArray();
        var anonymous = routes
            .Where(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(endpoint => endpoint.RoutePattern.RawText!)
            .OrderBy(route => route, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "/api/v1/auth/csrf", "/api/v1/auth/login",
            "/api/v1/client-installations/register", "/api/v1/client-installations/report" }, anonymous);
        Assert.All(routes.Where(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null), endpoint =>
            Assert.DoesNotContain(endpoint.Metadata, metadata => metadata is IAllowAnonymous));
    }

    [Fact]
    public async Task ForgedCookieCannotAuthenticate()
    {
        await using var app = await TestApplication.StartAsync();
        app.Client.DefaultRequestHeaders.Add("Cookie", "__Host-PortKill.Admin=forged");
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client.GetAsync("/api/v1/system/status")).StatusCode);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("")]
    public void StorageMustBeAbsolute(string root)
        => Assert.Throws<InvalidOperationException>(() => new AdminOptions { DataRoot = root }.Validate());

    [Fact]
    public void StorageCannotBeFilesystemRoot()
        => Assert.Throws<InvalidOperationException>(() => new AdminOptions { DataRoot = Path.GetPathRoot(Path.GetTempPath())! }.Validate());

    [Theory]
    [InlineData("http://0.0.0.0:0")]
    [InlineData("http://[::]:0")]
    public void ApiRejectsNonLoopbackBindings(string url)
        => Assert.Throws<InvalidOperationException>(() => PortKiller.UpdateAdmin.Api.AdminApiHost.Create(["--urls", url]));

    [Fact]
    public void ServerAndProtocolHaveNoDesktopDependencies()
    {
        foreach (var assembly in new[] { typeof(AdminApplication).Assembly, typeof(UpdateManifest).Assembly })
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
                reference.Name!.StartsWith("Avalonia", StringComparison.Ordinal)
                || reference.Name.Contains("Desktop", StringComparison.Ordinal)
                || reference.Name.Contains("Infrastructure.Windows", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(UpdateManifest).Assembly.GetReferencedAssemblies(), reference =>
            reference.Name!.Contains("EntityFramework", StringComparison.Ordinal)
            || reference.Name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }
}
