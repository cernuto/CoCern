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
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/dashboard/api/analytics/test-effort?repositoryId=repo")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await bearer.GetAsync("/dashboard/api/analytics/test-effort?repositoryId=repo")).StatusCode);
    }

    // Security boundary: the file describes one repository, and a bad file must not put its path in a reply.
    [Fact]
    public async Task Test_effort_answers_only_the_repository_its_file_names_and_never_shows_the_path()
    {
        var directory = Directory.CreateTempSubdirectory("cocern-test-effort-");
        try
        {
            var file = Path.Combine(directory.FullName, "effort.json");
            using var service = new AnalyticsService(new AnalyticsOptions { TestEffortFile = file });
            async Task<string> Reply(string repository) => JsonSerializer.Serialize(await service.RunNamedAsync(AnalyticsQueryNames.TestEffort, repository));

            Assert.Contains("no_run_data", await Reply("repo"));
            var rules = string.Join(",", "abcd".Select(rule => $$"""{"rule":"{{rule}}","runs":0,"judgedRuns":1,"minutes":0}"""));
            await File.WriteAllTextAsync(file, $$"""
                {"schema":1,"repositoryId":"repo","generatedAt":"2026-11-05T00:00:00Z","runs":1,"cases":1,"wallMinutes":2,"rules":[{{rules}}],
                 "days":[{"day":"2026-11-05","runs":1,"wallMinutes":2,"caseMinutes":1}],"expensiveNeverFailed":[{"test":"secret.Name","minutes":1,"executions":1}]}
                """);
            Assert.Contains("secret.Name", await Reply("repo"));
            var other = await Reply("other");
            Assert.Contains("no_run_data", other);
            Assert.DoesNotContain("secret.Name", other);

            await File.WriteAllTextAsync(file, """{"schema":1,"repositoryId":"repo","expensiveNeverFailed":[{"test":"C:/private/path.cs"}]}""");
            var refused = await Reply("repo");
            Assert.Contains("run_data_unreadable", refused);
            Assert.DoesNotContain("private", refused);
            Assert.DoesNotContain(directory.Name, refused);
        }
        finally { directory.Delete(recursive: true); }
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
