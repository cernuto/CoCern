using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Core;
using CoCern.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CoCern.Tests;

public sealed class DashboardAnalyticsConditionalTests
{
    [Fact]
    public async Task Unchanged_report_returns_no_body_and_tags_are_bound_to_repository_period_and_values()
    {
        var analytics = new FixedAnalytics();
        await using var host = Host(analytics);
        using var client = await Browser(host);
        const string path = "/dashboard/api/analytics?repositoryId=repo&days=7";
        var first = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.NotNull(first.Headers.ETag);
        var tag = first.Headers.ETag!.ToString();
        using var repeat = new HttpRequestMessage(HttpMethod.Get, path);
        repeat.Headers.TryAddWithoutValidation("If-None-Match", tag);
        var unchanged = await client.SendAsync(repeat);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        Assert.Empty(await unchanged.Content.ReadAsStringAsync());
        Assert.Equal(tag, unchanged.Headers.ETag?.ToString());
        foreach (var other in new[] { "/dashboard/api/analytics?repositoryId=other&days=7", "/dashboard/api/analytics?repositoryId=repo&days=1" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, other);
            request.Headers.TryAddWithoutValidation("If-None-Match", tag);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
        }
        analytics.Value = 2;
        using var changed = new HttpRequestMessage(HttpMethod.Get, path);
        changed.Headers.TryAddWithoutValidation("If-None-Match", tag);
        var updated = await client.SendAsync(changed);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.NotEqual(tag, updated.Headers.ETag?.ToString());
    }

    [Fact]
    public async Task Partial_failures_are_not_cached_as_unchanged()
    {
        var analytics = new FixedAnalytics();
        await using var host = Host(analytics);
        using var client = await Browser(host);
        const string path = "/dashboard/api/analytics?repositoryId=repo&days=7";
        var first = await client.GetAsync(path);
        analytics.Fail = true;
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("If-None-Match", first.Headers.ETag?.ToString());
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.ETag);
    }

    private static RelayHostFixture Host(IAnalyticsTool analytics) => new(settings: new Dictionary<string, string?>
    {
        ["Relay:Analytics:Enabled"] = "false",
        ["Relay:Clients:0:IsAdministrator"] = "true",
        ["Relay:Dashboard:ViewerPrincipalIds:0"] = "test-user"
    }, configureServices: services => services.AddSingleton(analytics));

    private static async Task<HttpClient> Browser(RelayHostFixture host)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Origin", "https://localhost");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/dashboard/api/login", new { token = RelayHostFixture.Token })).StatusCode);
        return client;
    }

    private sealed class FixedAnalytics : IAnalyticsTool
    {
        public int Value { get; set; } = 1;
        public bool Fail { get; set; }
        public Task<RelayReply> RunAsync(RelayCaller caller, JsonObject args, CancellationToken cancellationToken)
        {
            if (Fail) return Task.FromResult(new RelayReply(false, "AnalyticsTimeout", "{}"));
            var json = JsonSerializer.Serialize(new { cols = new[] { "key", "cur", "prev", "cur_n", "prev_n" }, rows = new object?[][] { ["all", Value, null, 1, 0] }, at = "2026-11-03T20:00:00Z" });
            return Task.FromResult(new RelayReply(true, "Ok", json));
        }
    }
}
