using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Storage;
using Orleans.Runtime;
using Xunit;

namespace CoCern.Tests;

public sealed class DashboardBindingReportTests
{
    [Theory]
    [InlineData(null, null, null)]
    [InlineData(0L, null, false)]
    [InlineData(2L, null, false)]
    [InlineData(2L, "{}", false)]
    [InlineData(2L, "{\"solution\":\"repo.slnx\"}", true)]
    [InlineData(null, "{\"customToolBinding\":\"reported\"}", true)]
    public async Task Binding_presence_comes_from_reported_metadata_instead_of_a_rename_version(long? version, string? binding, bool? expected)
    {
        using var store = new SqliteStore(Path.Combine(Path.GetTempPath(), "cocern-binding-report-" + Guid.NewGuid().ToString("N"), "relay.db"));
        var document = new JsonObject { ["sessionId"] = "session", ["repositoryId"] = "repo", ["label"] = "Renamed session" };
        if (version is not null) document["bindingVersion"] = version;
        if (binding is not null) document["binding"] = JsonNode.Parse(binding);
        var state = new GrainState<StateEnvelope<CoordinationDocument>>(new() { Value = new() { Json = document.ToJsonString() } });
        await new SqliteGrainStorage(store).WriteStateAsync("session", GrainId.Create("cocern.session", "session"), state);
        var observation = await store.ObserveAsync(DateTimeOffset.UtcNow, "current");
        var json = JsonSerializer.SerializeToElement(Assert.Single(observation.Sessions));
        Assert.True(json.TryGetProperty("HasBindingReport", out var report), "Binding presence must be independent of the metadata version.");
        if (expected is null) Assert.Equal(JsonValueKind.Null, report.ValueKind);
        else Assert.Equal(expected.Value, report.GetBoolean());
    }
}
