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

    [Theory]
    [InlineData("days=0&repositoryId=repo")]
    [InlineData("days=15&repositoryId=repo")]
    [InlineData("days=no&repositoryId=repo")]
    [InlineData("days=7")]
    [InlineData("days=7&repositoryId=%20")]
    [InlineData("days=7&repositoryId=bad%0Aid")]
    [InlineData("days=7&days=1&repositoryId=repo")]
    public async Task Analytics_rejects_invalid_selection(string query)
    {
        await using var host = Host();
        using var client = await Login(host);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/dashboard/api/analytics?" + query)).StatusCode);
    }

    [Fact]
    public async Task Repository_identifiers_retain_the_existing_length_limit()
    {
        await using var host = Host();
        using var client = await Login(host);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/dashboard/api/analytics?days=7&repositoryId=" + new string('r', RelayLimits.MaxIdentifierLength + 1))).StatusCode);
    }

    [Fact]
    public async Task Disabled_analytics_has_an_explicit_unavailable_state()
    {
        await using var host = Host();
        using var client = await Login(host);
        var result = await Get(client);
        Assert.Equal("unavailable", result.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("snapshotAt").ValueKind);
        Assert.Empty(result.GetProperty("queries").EnumerateArray());
    }

    [Fact]
    public async Task A_real_scrubbed_snapshot_preserves_reports_repository_days_and_null_zero()
    {
        await using var host = Host(new Dictionary<string, string?>
        {
            ["Relay:Analytics:Enabled"] = "true",
            ["Relay:Analytics:SnapshotInterval"] = "1.00:00:00",
            ["Relay:Analytics:ExtensionDirectory"] = await AnalyticsTestExtension.DirectoryAsync()
        });
        using var client = await Login(host);
        var store = host.Services.GetRequiredService<SqliteStore>();
        var now = DateTimeOffset.UtcNow;
        foreach (var (at, input) in new[] { (now.AddHours(-3), 100L), (now.AddDays(-10), 40L) })
            store.Record(new ActivityEvent { Timestamp = at, Kind = ActivityEvent.Call, RepositoryId = "past", SessionId = "worker", Role = "worker", Tool = "task_update", Ok = true, Code = "Ok", ReplyBytes = 100, Target = "task:past/default/T-1", WorkflowState = "ready", InputTokens = input, OutputTokens = 0, RequestId = at.ToString("O") });
        var analytics = host.Services.GetRequiredService<AnalyticsService>();
        var deadline = now.AddSeconds(30);
        while (analytics.SnapshotAt is null && DateTimeOffset.UtcNow < deadline) await Task.Delay(50);
        var snapshots = host.Services.GetServices<IHostedService>().OfType<AnalyticsSnapshotService>().Single();
        await snapshots.TakeSnapshotAsync(analytics, CancellationToken.None);
        var result = await Get(client, "past", 7);
        Assert.Equal("ready", result.GetProperty("status").GetString());
        Assert.Contains("unknown", result.GetProperty("message").GetString());
        Assert.Equal("past", result.GetProperty("repositoryId").GetString());
        Assert.Equal(7, result.GetProperty("days").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, result.GetProperty("snapshotAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("availableFrom").ValueKind);
        var queries = result.GetProperty("queries").EnumerateArray().ToDictionary(x => x.GetProperty("name").GetString()!);
        Assert.Equal(AnalyticsQueryNames.All.Order(), queries.Keys.Order());
        var tokens = queries[AnalyticsQueryNames.TokensPerTask].GetProperty("rows")[0];
        Assert.Equal(100, tokens.GetProperty("current").GetDouble());
        Assert.Equal(40, tokens.GetProperty("previous").GetDouble());
        var oneDay = await Get(client, "past", 1);
        var oneTokens = oneDay.GetProperty("queries").EnumerateArray().Single(x => x.GetProperty("name").GetString() == AnalyticsQueryNames.TokensPerTask).GetProperty("rows")[0];
        Assert.Equal(JsonValueKind.Null, oneTokens.GetProperty("previous").ValueKind);
        Assert.Equal(0, oneTokens.GetProperty("previousCount").GetInt64());
        var messages = queries[AnalyticsQueryNames.MessagesPerTask].GetProperty("rows")[0];
        Assert.Equal(0, messages.GetProperty("current").GetDouble());
        var empty = await Get(client, "another", 7);
        var emptyTokens = empty.GetProperty("queries").EnumerateArray().Single(x => x.GetProperty("name").GetString() == AnalyticsQueryNames.TokensPerTask).GetProperty("rows")[0];
        Assert.Equal(JsonValueKind.Null, emptyTokens.GetProperty("current").ValueKind);
    }

    private static RelayHostFixture Host(Dictionary<string, string?>? settings = null)
    {
        settings ??= new();
        settings.TryAdd("Relay:Analytics:Enabled", "false");
        settings["Relay:Clients:0:IsAdministrator"] = "true";
        settings["Relay:Dashboard:ViewerPrincipalIds:0"] = "test-user";
        return new(settings: settings);
    }

    [Theory]
    [InlineData("missing", "no-snapshot")]
    [InlineData("partial", "ready")]
    [InlineData("oversize", "too-large")]
    [InlineData("mixed", "error")]
    public async Task Query_failures_bounds_and_snapshot_switches_are_explicit_and_scrubbed(string mode, string status)
    {
        var queries = new ControlledQueries(mode);
        await using var host = new RelayHostFixture(settings: new Dictionary<string, string?>
        {
            ["Relay:Analytics:Enabled"] = "false",
            ["Relay:Clients:0:IsAdministrator"] = "true",
            ["Relay:Dashboard:ViewerPrincipalIds:0"] = "test-user"
        }, configureServices: services => services.AddSingleton<IAnalyticsTool>(new AnalyticsTool(queries, false)));
        using var client = await Login(host);
        var response = await client.GetAsync("/dashboard/api/analytics?repositoryId=repo&days=3&sql=secret");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private-path", text);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(text) <= RelayLimits.MaxAnalyticsReplyBytes);
        using var document = JsonDocument.Parse(text);
        Assert.Equal(status, document.RootElement.GetProperty("status").GetString());
        if (mode == "partial")
        {
            var reports = document.RootElement.GetProperty("queries").EnumerateArray().ToArray();
            Assert.Contains(reports, x => x.GetProperty("error").ValueKind == JsonValueKind.String);
            Assert.Contains(reports, x => x.GetProperty("rows").GetArrayLength() > 0);
            Assert.Contains(reports, x => x.GetProperty("truncated").GetBoolean());
        }
        Assert.All(queries.Selections, x => { Assert.Equal("repo", x.Repository); Assert.Equal(3, x.Days); Assert.Contains(x.Name, AnalyticsQueryNames.All); });
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
