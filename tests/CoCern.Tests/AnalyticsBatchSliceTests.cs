using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Adapter;
using CoCern.Contracts;
using CoCern.Core;
using Xunit;

namespace CoCern.Tests;

public sealed class AnalyticsBatchSliceTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static JsonObject Args(params string[] names) => new() { ["name"] = "batch", ["repositoryId"] = "repo", ["reports"] = new JsonArray(names.Select(n => (JsonNode)n).ToArray()) };

    [Fact]
    public async Task RejectsInvalidSelectionBeforeExecuting()
    {
        var queries = new Queries();
        foreach (var args in new[] { Args(), Args("sql"), Args("unknown"), Args(AnalyticsQueryNames.All[0], AnalyticsQueryNames.All[0]) })
            Assert.False((await AnalyticsBatch.RunAsync(queries, args, default)).Ok);
        Assert.Equal(0, queries.Calls);
        Assert.Null(AnalyticsBatch.Problem(Args(AnalyticsQueryNames.All.ToArray())));
    }

    [Fact]
    public async Task SummarizesAndConditionallyReturnsUnchanged()
    {
        var queries = new Queries();
        var args = Args(AnalyticsQueryNames.All[0]);
        var first = await AnalyticsBatch.RunAsync(queries, args, default);
        Assert.True(first.Ok);
        var body = JsonNode.Parse(first.DataJson)!;
        Assert.Single(body["reports"]![AnalyticsQueryNames.All[0]]!["rows"]!.AsArray());
        Assert.True(body["reports"]![AnalyticsQueryNames.All[0]]!["more"]!.GetValue<bool>());
        args["ifNoneMatch"] = body["etag"]!.GetValue<string>();
        var second = JsonNode.Parse((await AnalyticsBatch.RunAsync(queries, args, default)).DataJson)!;
        Assert.True(second["unchanged"]!.GetValue<bool>());
        args["repositoryId"] = "other";
        Assert.Null(JsonNode.Parse((await AnalyticsBatch.RunAsync(queries, args, default)).DataJson)!["unchanged"]);
        args["includeDetails"] = true;
        Assert.Equal(2, JsonNode.Parse((await AnalyticsBatch.RunAsync(queries, args, default)).DataJson)!["reports"]![AnalyticsQueryNames.All[0]]!["rows"]!.AsArray().Count);
    }

    [Fact]
    public async Task RejectsMixedSnapshotsAndEscapedOversizedBatch()
    {
        var mixed = await AnalyticsBatch.RunAsync(new Queries { Mixed = true }, Args(AnalyticsQueryNames.All.Take(2).ToArray()), default);
        Assert.Equal(RelayErrorCodes.AnalyticsUnavailable, mixed.Code);
        var large = await AnalyticsBatch.RunAsync(new Queries { Large = true }, Args(AnalyticsQueryNames.All[0]), default);
        Assert.Equal(RelayErrorCodes.PayloadTooLarge, large.Code);
    }

    [Fact]
    public async Task KeepsPerReportErrorsAndNeverCachesThem()
    {
        var result = await AnalyticsBatch.RunAsync(new Queries { FailFirst = true }, Args(AnalyticsQueryNames.All.Take(2).ToArray()), default);
        Assert.True(result.Ok);
        var body = JsonNode.Parse(result.DataJson)!;
        Assert.Equal(1, body["summary"]!["errors"]!.GetValue<int>());
        Assert.Null(body["etag"]);
    }

    [Fact]
    public void DiagnosticsPreservesWorkerSurfaceAndRequiresAdminForAnalytics()
    {
        var worker = CoordinationTools.NamesForRole(false);
        Assert.Equal(worker.Append(RelayTools.AnalyticsQuery).Order(), CoordinationTools.NamesForRole(true, diagnostics: true).Order());
        Assert.Equal(worker, CoordinationTools.NamesForRole(false, diagnostics: true));
        Assert.False(CoordinationTools.IsAllowed(RelayTools.ControlRequest, true, diagnostics: true));
        Assert.False(CoordinationTools.IsAllowed(RelayTools.AnalyticsQuery, false, diagnostics: true));
        Assert.Equal(CoordinationTools.NamesForRole(true, true), CoordinationTools.NamesForRole(true, true, true));
        var options = AdapterOptions.FromEnvironment(key => key switch { "COCERN_TOKEN" => "test-token", "COCERN_PROFILE" => "diagnostics", _ => null });
        Assert.True(options.Diagnostics);
        Assert.False(options.Admin);
    }

    [Fact]
    public async Task ConditionalTagBindsOrderParametersAndDetailsAndCancellationPropagates()
    {
        var queries = new Queries();
        var names = AnalyticsQueryNames.All.Take(2).ToArray();
        async Task<string> Tag(JsonObject args) => JsonNode.Parse((await AnalyticsBatch.RunAsync(queries, args, default)).DataJson)!["etag"]!.GetValue<string>();
        var original = await Tag(Args(names));
        Assert.NotEqual(original, await Tag(Args(names.Reverse().ToArray())));
        var args = Args(names);
        args["params"] = new JsonObject { ["days"] = 1 };
        Assert.NotEqual(original, await Tag(args));
        args = Args(names);
        args["includeDetails"] = true;
        Assert.NotEqual(original, await Tag(args));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AnalyticsBatch.RunAsync(queries, Args(names), canceled.Token));
    }

    [Fact]
    public void MeasureSchemasAndEstimatedTokens()
    {
        var counter = new CoCern.Host.O200kTokenCounter();
        foreach (var profile in new[] { "worker", "diagnostics", "administrator" })
        {
            var names = CoordinationTools.NamesForRole(profile != "worker", diagnostics: profile == "diagnostics");
            var tools = RelayToolCatalog.All.Where(t => names.Contains(t.Name)).ToArray();
            output.WriteLine($"{profile}: {tools.Length} tools, {tools.Sum(t => t.Description.Length + RelayToolCatalog.ForwardingSchema(t).GetRawText().Length)} description/schema characters, {tools.Sum(t => counter.Count(t.Description) + counter.Count(RelayToolCatalog.ForwardingSchema(t).GetRawText()))} estimated o200k tokens.");
        }
        var analytics = RelayToolCatalog.All.Single(t => t.Name == RelayTools.AnalyticsQuery);
        var schema = RelayToolCatalog.ForwardingSchema(analytics);
        output.WriteLine($"analytics: {analytics.Description.Length + schema.GetRawText().Length} description/schema characters, {counter.Count(analytics.Description) + counter.Count(schema.GetRawText())} estimated o200k tokens.");
        Assert.Contains("batch", schema.GetRawText());
        Assert.DoesNotContain("batch", AnalyticsQueryNames.All);
        Assert.Contains("name", RelayToolCatalog.InputSchema(analytics).GetProperty("required").EnumerateArray().Select(x => x.GetString()));
    }

    private sealed class Queries : IAnalyticsQueries
    {
        public int Calls { get; private set; }
        public bool Mixed { get; init; }
        public bool Large { get; init; }
        public bool FailFirst { get; init; }
        public IReadOnlyList<string> QueryNames => AnalyticsQueryNames.All;
        public Task<AnalyticsResult> RunNamedAsync(string name, string repositoryId, JsonElement parameters = default, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (FailFirst && Calls == 1) throw new AnalyticsException(AnalyticsError.Timeout, "timed out");
            return Task.FromResult(new AnalyticsResult(["key", "cur"], [new object?[] { Large ? new string('"', 5000) : "all", 2 }, new object?[] { "next", 1 }], false, DateTimeOffset.UnixEpoch.AddTicks(Mixed ? Calls : 0)));
        }
        public Task<AnalyticsResult> RunSqlAsync(string sql, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
