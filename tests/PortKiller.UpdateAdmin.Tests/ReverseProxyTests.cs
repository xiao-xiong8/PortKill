using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PortKiller.UpdateAdmin.Api.Authentication;

namespace PortKiller.UpdateAdmin.Tests;

public sealed class ReverseProxyTests
{
    [Fact]
    public async Task DefaultProxyConfigurationRemainsLoopbackOnly()
    {
        var builder = CreateBuilder();
        builder.AddRequestSecurity();
        await using var app = builder.Build();

        var options = app.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
        Assert.Equal(2, options.KnownProxies.Count);
        Assert.Contains(IPAddress.Loopback, options.KnownProxies);
        Assert.Contains(IPAddress.IPv6Loopback, options.KnownProxies);
        Assert.Empty(options.KnownIPNetworks);
        Assert.Equal(1, options.ForwardLimit);
    }

    [Fact]
    public async Task ConfiguredProxyPreservesHttpsForSecureAntiforgeryCookies()
    {
        var proxy = IPAddress.Parse("172.30.246.2");
        var builder = CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ReverseProxy:KnownProxies:0"] = proxy.ToString()
        });
        builder.AddRequestSecurity();
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Connection.RemoteIpAddress = proxy;
            return next(context);
        });
        app.UseForwardedHeaders();
        app.MapGet("/token", (IAntiforgery antiforgery, HttpContext context) => new
        {
            secureRequest = context.Request.IsHttps,
            requestToken = antiforgery.GetAndStoreTokens(context).RequestToken
        });
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/token");
        request.Headers.Add("X-Forwarded-Proto", "https");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("secureRequest").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("requestToken").GetString()));
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), cookie =>
            cookie.StartsWith("__Host-PortKill.Admin.Csrf=", StringComparison.Ordinal)
            && cookie.Contains("; secure", StringComparison.OrdinalIgnoreCase)
            && cookie.Contains("; httponly", StringComparison.OrdinalIgnoreCase));
    }

    private static WebApplicationBuilder CreateBuilder()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = "Production"
        });
        builder.WebHost.UseTestServer();
        return builder;
    }
}
