using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortKiller.UpdateManagement.Contracts;
using PortKiller.UpdateManagement.Persistence;
using PortKiller.UpdateManagement.Persistence.Entities;

namespace PortKiller.UpdateAdmin.Tests;

public sealed class ClientInstallationTests
{
    private const string Root = "/api/v1/client-installations";
    private static ClientInstallationRegisterRequest Registration(string machine = "DESKTOP-ABC", string version = "1.2.3")
        => new(Guid.NewGuid().ToString("N"), version, machine);

    private static async Task<ClientInstallationRegisterResponse> Register(HttpClient client, ClientInstallationRegisterRequest? request = null)
    {
        using var response = await client.PostAsJsonAsync(Root + "/register", request ?? Registration());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ClientInstallationRegisterResponse>())!;
    }

    private static Task<HttpResponseMessage> Report(HttpClient client, string token, string result = "installed", string version = "1.2.4")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Root + "/report")
        { Content = JsonContent.Create(new ClientInstallationReportRequest(version, "DESKTOP-ABC", result)) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    [Fact]
    public async Task PublicRegistrationAndReportsStoreOnlyHashAndNeverCreateRequestAuditRows()
    {
        await using var app = await TestApplication.StartAsync();
        using var client = app.NewClient(autoCsrf: false);
        var request = Registration(); var registered = await Register(client, request);
        Assert.Equal(request.InstallationId, registered.InstallationId);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(Root + "/register", request)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Report(client, registered.ClientToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Report(client, registered.ClientToken)).StatusCode);
        using var scope = app.App.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<UpdateAdminDbContext>();
        var row = await db.ClientInstallations.SingleAsync();
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(registered.ClientToken))), row.TokenHash);
        Assert.Equal("1.2.4.0", row.ClientVersion); Assert.Equal("installed", row.LastReportResult);
        Assert.Empty(await db.AuditEvents.ToArrayAsync());
        Assert.DoesNotContain(registered.ClientToken, JsonSerializer.Serialize(row) + app.Logs.Text);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Root)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Report(client, new string('x', 43))).StatusCode);
    }

    [Fact]
    public async Task LocallyGeneratedCredentialMakesRegistrationRetriesSafeWithoutAllowingCredentialReplacement()
    {
        await using var app = await TestApplication.StartAsync(); using var client = app.NewClient(autoCsrf: false);
        string secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var request = Registration() with { ClientToken = secret };
        Assert.Equal(secret, (await Register(client, request)).ClientToken);
        Assert.Equal(secret, (await Register(client, request)).ClientToken);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(Root + "/register", request with { ClientToken = new string('A', 43) })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Report(client, secret)).StatusCode);
    }

    [Theory]
    [InlineData("v1.2.3", "PC")]
    [InlineData("1.2.3-beta", "PC")]
    [InlineData("1", "PC")]
    [InlineData("1.2", "")]
    [InlineData("1.2", "PC\nNAME")]
    public async Task InvalidClientMetadataIsRejected(string version, string machine)
    {
        await using var app = await TestApplication.StartAsync(); using var client = app.NewClient(autoCsrf: false);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Root + "/register", Registration(machine, version))).StatusCode);
    }

    [Fact]
    public async Task BoundsAndResultValidationApplyWithoutCsrfOrAdministratorCookies()
    {
        await using var app = await TestApplication.StartAsync(); using var client = app.NewClient(autoCsrf: false);
        var registered = await Register(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await Report(client, registered.ClientToken, "online")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Root + "/register", Registration(new string('x', 129)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Root + "/register", new { installationId = (string?)null, version = "1.0", machineName = "PC" })).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsync(Root + "/register", new StringContent(new string(' ', 5000), Encoding.UTF8, "application/json"))).StatusCode);
        for (int index = 0; index < 30; index++)
        {
            using var response = await client.PostAsJsonAsync(Root + "/register", Registration());
            if (response.StatusCode == HttpStatusCode.TooManyRequests) return;
        }
        Assert.Fail("Registration did not enforce its per-source rate limit.");
    }

    [Fact]
    public async Task AdminFiltersPaginationRevocationAndFreshAuthenticationAreEnforced()
    {
        await using var app = await TestApplication.StartAsync(); await MfaTests.EnrollAsync(app);
        using var client = app.NewClient(autoCsrf: false);
        var first = await Register(client, Registration("ALPHA", "1.0"));
        var second = await Register(client, Registration("BETA", "2.0"));
        var page = (await app.Client.GetFromJsonAsync<PageResponse<ClientInstallationResponse>>(Root + "?pageSize=1"))!;
        Assert.Equal(2, page.Total); Assert.Single(page.Items);
        var filtered = (await app.Client.GetFromJsonAsync<PageResponse<ClientInstallationResponse>>(Root + "?version=1.0&machineName=alpha&result=started"))!;
        Assert.Single(filtered.Items); Assert.Equal(first.InstallationId[..4] + "…" + first.InstallationId[^4..], filtered.Items[0].InstallationId);
        Assert.Equal(first.InstallationId, filtered.Items[0].RevocationId);
        Assert.DoesNotContain("tokenHash", JsonSerializer.Serialize(filtered), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.GetAsync(Root + "?pageSize=101")).StatusCode);
        Assert.Empty((await app.Client.GetFromJsonAsync<PageResponse<ClientInstallationResponse>>(Root + "?reportedAfterUtcMs=" + app.Clock.GetUtcNow().AddDays(1).ToUnixTimeMilliseconds()))!.Items);
        // Authenticated administrator without CSRF cannot revoke.
        using var noCsrf = app.NewClient(autoCsrf: false);
        noCsrf.DefaultRequestHeaders.Add("Cookie", app.DefaultCookieHeader);
        Assert.Equal(HttpStatusCode.BadRequest, (await noCsrf.PostAsJsonAsync(Root + $"/{first.InstallationId}/revoke", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.PostAsJsonAsync(Root + $"/{first.InstallationId}/revoke", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Report(client, first.ClientToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Report(client, second.ClientToken)).StatusCode);
        Assert.Single((await app.Client.GetFromJsonAsync<PageResponse<ClientInstallationResponse>>(Root + "?revoked=true"))!.Items);
        Assert.Single((await app.Client.GetFromJsonAsync<PageResponse<ClientInstallationResponse>>(Root))!.Items);
        var renewed = await Register(client); Assert.NotEqual(first.InstallationId, renewed.InstallationId);
        app.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Client.PostAsJsonAsync(Root + $"/{second.InstallationId}/revoke", new { })).StatusCode);
        using var scope = app.App.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AdminUser>>(); var user = (await users.FindByNameAsync("operator"))!;
        await users.RemoveFromRoleAsync(user, "Administrator"); await users.AddToRoleAsync(user, "Viewer");
        Assert.Equal(HttpStatusCode.OK, (await app.Client.GetAsync(Root)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Client.PostAsJsonAsync(Root + $"/{second.InstallationId}/revoke", new { })).StatusCode);
    }
}
