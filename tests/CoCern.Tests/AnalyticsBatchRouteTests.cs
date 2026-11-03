using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CoCern.Tests;

public sealed class AnalyticsBatchRouteTests
{
    [Fact]
    public async Task Administrator_can_batch_and_conditionally_reread_but_worker_cannot()
    {
        foreach (var admin in new[] { true, false })
        {
            var queries = new FixedQueries();
            await using var host = Host(admin, queries);
            using var client = host.AuthorizedClient();
            var body = new { repositoryId = "repo", name = "batch", reports = new[] { AnalyticsQueryNames.CallsPerTask, AnalyticsQueryNames.UsageCoverage }, @params = new { days = 1 } };
            var response = await client.PostAsJsonAsync("/api/tools/analytics_query", body);
            if (!admin)
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Equal(0, queries.Reads);
                continue;
            }
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            var reply = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(reply.GetProperty("ok").GetBoolean());
            using var data = JsonDocument.Parse(reply.GetProperty("dataJson").GetString()!);
            Assert.Equal(2, data.RootElement.GetProperty("reports").EnumerateObject().Count());
            var tag = data.RootElement.GetProperty("etag").GetString();
            var repeated = await client.PostAsJsonAsync("/api/tools/analytics_query", new { body.repositoryId, body.name, body.reports, @params = new { days = 1 }, ifNoneMatch = tag });
            Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
            var again = await repeated.Content.ReadFromJsonAsync<JsonElement>();
            using var unchanged = JsonDocument.Parse(again.GetProperty("dataJson").GetString()!);
            Assert.True(unchanged.RootElement.GetProperty("unchanged").GetBoolean());
        }
    }

    [Fact]
    public async Task Invalid_batch_selection_is_rejected_before_queries_run()
    {
        var queries = new FixedQueries();
        await using var host = Host(true, queries);
        using var client = host.AuthorizedClient();
        foreach (var reports in new[] { Array.Empty<string>(), new[] { "missing" }, new[] { "calls_per_task", "calls_per_task" } })
        {
            var response = await client.PostAsJsonAsync("/api/tools/analytics_query", new { repositoryId = "repo", name = "batch", reports });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Equal(0, queries.Reads);
    }

    private static RelayHostFixture Host(bool admin, IAnalyticsQueries queries) => new(settings: new Dictionary<string, string?>
    {
        ["Relay:Analytics:Enabled"] = "false", ["Relay:Clients:0:IsAdministrator"] = admin.ToString()
    }, configureServices: services => services.AddSingleton(provider =>
        new RelayApplication(provider.GetRequiredService<Orleans.IGrainFactory>(), analytics: new AnalyticsTool(queries, false))));

    private sealed class FixedQueries : IAnalyticsQueries
    {
        public int Reads { get; private set; }
        public IReadOnlyList<string> QueryNames => AnalyticsQueryNames.All;
        public Task<AnalyticsResult> RunNamedAsync(string name, string repositoryId, JsonElement parameters = default, CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult(new AnalyticsResult(["key", "cur", "prev", "cur_n", "prev_n"], [["all", 1.0, null, 1L, 0L]], false, new DateTimeOffset(2026, 11, 3, 20, 0, 0, TimeSpan.Zero)));
        }
        public Task<AnalyticsResult> RunSqlAsync(string sql, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
