using System.Reflection;
using System.Text.Json.Nodes;
using CoCern.Contracts.Dashboard;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Host.Dashboard;
using CoCern.Storage;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Xunit;

namespace CoCern.Tests;

public sealed class DashboardActivityRetentionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Empty_activity_inventory_respects_the_observation_deadline(bool expired)
    {
        using var store = new SqliteStore(Path.Combine(Path.GetTempPath(), "cocern-empty-activity-" + Guid.NewGuid().ToString("N"), "relay.db"));
        var project = typeof(SqliteStore).GetMethod("ProjectSessionActivityAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        // Control the existing monotonic-time argument, without sleeping or relying on how quickly this test is scheduled.
        var offset = System.Diagnostics.Stopwatch.Frequency * 3600;
        var started = System.Diagnostics.Stopwatch.GetTimestamp() + (expired ? -offset : offset);
        var complete = await (Task<bool>)project.Invoke(store, [new Dictionary<string, DashboardSession>(), DateTimeOffset.UtcNow, started, CancellationToken.None])!;
        Assert.Equal(!expired, complete);
    }

    [Fact]
    public async Task Busy_storage_retains_last_call_and_original_observation_time_with_a_notice()
    {
        await using var host = new RelayHostFixture(disableDispatcher: true);
        var store = host.Services.GetRequiredService<SqliteStore>();
        var collector = host.Services.GetRequiredService<DashboardCollector>();
        var document = new JsonObject { ["sessionId"] = "session", ["repositoryId"] = "repo" };
        var state = new GrainState<StateEnvelope<CoordinationDocument>>(new() { Value = new() { Json = document.ToJsonString() } });
        await new SqliteGrainStorage(store).WriteStateAsync("session", GrainId.Create("cocern.session", "session"), state);
        var lastCall = DateTimeOffset.UtcNow.AddMinutes(-2);
        store.Record(new() { Timestamp = lastCall, Kind = ActivityEvent.Call, RepositoryId = "repo", SessionId = "session", Tool = "check_inbox", Ok = true });
        await store.FlushActivityAsync();
        var collect = typeof(DashboardCollector).GetMethod("CollectAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var first = await (Task<DashboardSnapshot>)collect.Invoke(collector, [collector.Current, CancellationToken.None])!;
        Assert.Equal(lastCall, Assert.Single(first.Sessions).LastActivityAt);
        var gate = (SemaphoreSlim)typeof(SqliteStore).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        await gate.WaitAsync();
        try
        {
            var busy = await (Task<DashboardSnapshot>)collect.Invoke(collector, [first, CancellationToken.None])!;
            Assert.Equal(lastCall, Assert.Single(busy.Sessions).LastActivityAt);
            Assert.Equal(first.ObservedAt, busy.ObservedAt);
            Assert.Contains(busy.Warnings, warning => warning.Contains("busy", StringComparison.OrdinalIgnoreCase));
        }
        finally { gate.Release(); }
    }
}
