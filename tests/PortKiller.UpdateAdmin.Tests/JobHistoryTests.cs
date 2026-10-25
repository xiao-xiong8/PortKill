using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PortKiller.UpdateAdmin.Tests;

public sealed class JobHistoryTests
{
    [Fact]
    public async Task AnonymousCannotReadHistory()
    {
        await using var app = await TestApplication.StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client.GetAsync("/api/v1/jobs")).StatusCode);
    }

    [Fact]
    public async Task AdministratorGetsStableBoundedPagesAndKindFilter()
    {
        await using var app = await JobEventTests.StartAsync();
        string older = await JobEventTests.CreateTaskAsync(app); app.Clock.Advance(TimeSpan.FromMilliseconds(1));
        string newer = await JobEventTests.CreateTaskAsync(app, foreign: true);
        var first = await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/jobs?pageSize=1");
        Assert.Equal(2, first.GetProperty("total").GetInt64()); Assert.Equal(newer, first.GetProperty("items")[0].GetProperty("id").GetString());
        var second = await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/jobs?pageSize=1&page=2");
        Assert.Equal(older, second.GetProperty("items")[0].GetProperty("id").GetString());
        var empty = await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/jobs?kind=publish");
        Assert.Equal(0, empty.GetProperty("total").GetInt64());
    }

    [Theory]
    [InlineData("Viewer")][InlineData("Uploader")][InlineData("Publisher")]
    public async Task NonAdministratorsCannotDiscoverOtherOwnersTasks(string role)
    {
        await using var app = await JobEventTests.StartAsync();
        string own = await JobEventTests.CreateTaskAsync(app); await JobEventTests.CreateTaskAsync(app, foreign: true);
        await JobEventTests.SetRoleAsync(app, role);
        var result = await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/jobs");
        Assert.Equal(1, result.GetProperty("total").GetInt64()); Assert.Equal(own, result.GetProperty("items")[0].GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("page=0")][InlineData("page=10001")][InlineData("pageSize=0")][InlineData("pageSize=101")][InlineData("kind=unknown")]
    public async Task RejectsUnboundedOrUnknownQueries(string query)
    {
        await using var app = await JobEventTests.StartAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.GetAsync("/api/v1/jobs?" + query)).StatusCode);
    }
}
