using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CoCern.Analytics;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using CoCern.Host;
using Xunit;

namespace CoCern.Tests;

public sealed class DashboardAnalyticsTests
{
    [Fact]
    public async Task Analytics_requires_a_dashboard_viewer_cookie()
    {
        await using var host = Host();
        using var client = Browser(host);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/dashboard/api/analytics?repositoryId=repo&days=7")).StatusCode);
        using var bearer = host.AuthorizedClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await bearer.GetAsync("/dashboard/api/analytics?repositoryId=repo&days=7")).StatusCode);
    }

    private static RelayHostFixture Host(Dictionary<string, string?>? settings = null)
    {
        settings ??= new();
        settings.TryAdd("Relay:Analytics:Enabled", "false");
        settings["Relay:Clients:0:IsAdministrator"] = "true";
        settings["Relay:Dashboard:ViewerPrincipalIds:0"] = "test-user";
        return new(settings: settings);
    }

    private sealed class ControlledQueries(string mode) : IAnalyticsQueries
    {
        public IReadOnlyList<string> QueryNames => AnalyticsQueryNames.All;
        public List<(string Name, string Repository, int Days)> Selections { get; } = [];
        public Task<AnalyticsResult> RunNamedAsync(string name, string repositoryId, JsonElement parameters = default, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Selections.Add((name, repositoryId, parameters.GetProperty("days").GetInt32()));
            if (mode == "missing") throw new AnalyticsException(AnalyticsError.Unavailable, "No analytics snapshot is loaded yet.");
            if (mode == "partial" && name == AnalyticsQueryNames.CallsPerTask) throw new AnalyticsException(AnalyticsError.Failed, "private-path");
            var key = mode == "oversize" ? new string('x', RelayLimits.MaxAnalyticsReplyBytes) : "all";
            var at = DateTimeOffset.Parse("2026-11-02T12:00:00Z").AddSeconds(mode == "mixed" ? Selections.Count : 0);
            if (mode == "many-rows")
                return Task.FromResult(new AnalyticsResult(["key", "cur", "prev", "cur_n", "prev_n"],
                    Enumerable.Range(0, 8).Select(index => (IReadOnlyList<object?>)new object?[] { $"tool-{index}", 0.0, null, 1L, 0L }).ToArray(), false, at));
            return Task.FromResult(new AnalyticsResult(["key", "cur", "prev", "cur_n", "prev_n"], [new object?[] { key, 0.0, null, 1L, 0L }], mode == "partial", at));
        }
        public Task<AnalyticsResult> RunSqlAsync(string sql, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Viewer SQL must never execute.");
    }

    private static HttpClient Browser(RelayHostFixture host)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Origin", "https://localhost");
        return client;
    }

    private static async Task<HttpClient> Login(RelayHostFixture host)
    {
        var client = Browser(host);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/dashboard/api/login", new { token = RelayHostFixture.Token })).StatusCode);
        return client;
    }

    private static async Task<JsonElement> Get(HttpClient client, string repository = "repo", int days = 7)
    {
        var response = await client.GetAsync($"/dashboard/api/analytics?repositoryId={Uri.EscapeDataString(repository)}&days={days}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}
