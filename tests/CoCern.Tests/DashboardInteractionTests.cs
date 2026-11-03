using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using CoCern.Contracts.Dashboard;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Storage;
using Orleans;
using Orleans.Runtime;
using Xunit;

namespace CoCern.Tests;

public sealed class DashboardInteractionTests
{
    [Fact]
    public async Task Direct_message_edges_use_only_valid_retained_metadata_and_survive_reopening()
    {
        var path = DatabasePath();
        var now = DateTimeOffset.UtcNow;
        using (var store = new SqliteStore(path))
        {
            await Session(store, "sender");
            await Session(store, "receiver");
            await Session(store, "foreign", "other-repo");
            store.Record(Message(now.AddMinutes(-2), duration: 10));
            store.Record(Message(now.AddMinutes(-1), duration: 30));
            store.Record(Message(now.AddSeconds(-10), duration: -1));
            store.Record(Message(now.AddMinutes(-3), "receiver", "sender", 7));
            store.Record(Message(now) with { Ok = false });
            store.Record(Message(now) with { Kind = ActivityEvent.Wake });
            store.Record(Message(now) with { Tool = "get_status" });
            store.Record(Message(now) with { RepositoryId = "other-repo" });
            store.Record(Message(now) with { SessionId = "impostor" });
            store.Record(Message(now, recipient: "missing"));
            store.Record(Message(now, recipient: "foreign"));
            store.Record(Message(now) with { Target = "message:sender>receiver>secret" });
            store.Record(Message(now) with { Target = "message:sender>%72eceiver" });
            store.Record(Message(now) with { Target = "task:repo/board/task" });
            store.Record(Message(now.AddHours(-25)));
            store.Record(Message(now.AddMinutes(1)));
            await store.FlushActivityAsync();
            await AssertGraph(store);
        }
        using (var store = new SqliteStore(path)) await AssertGraph(store);

        async Task AssertGraph(SqliteStore store)
        {
            var observation = await CompleteObservation(store, now);
            var json = JsonSerializer.SerializeToElement(observation, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.True(json.TryGetProperty("interactions", out var graph), "The observation must include the retained interaction graph.");
            var edges = graph.GetProperty("edges").EnumerateArray().ToArray();
            Assert.Equal(2, edges.Length);
            var forward = Assert.Single(edges, edge => edge.GetProperty("sourceSessionId").GetString() == "sender");
            Assert.Equal("receiver", forward.GetProperty("targetSessionId").GetString());
            Assert.Equal(3, forward.GetProperty("callCount").GetInt32());
            Assert.Equal(now.AddSeconds(-10), forward.GetProperty("lastInteractionAt").GetDateTimeOffset());
            Assert.Equal(20, forward.GetProperty("meanHubDurationMilliseconds").GetDouble());
            Assert.Equal(2, forward.GetProperty("measuredCallCount").GetInt32());
            Assert.False(graph.GetProperty("truncated").GetBoolean());
            Assert.Equal(now.AddHours(-24), graph.GetProperty("windowStart").GetDateTimeOffset());
            Assert.DoesNotContain("secret", json.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain("requestText", graph.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("target\"", graph.GetRawText(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Evidence_limit_reports_partial_counts_instead_of_scanning_all_history()
    {
        using var store = new SqliteStore(DatabasePath());
        var now = DateTimeOffset.UtcNow;
        await Session(store, "sender");
        await Session(store, "receiver");
        for (var i = 0; i < 4001; i++)
        {
            store.Record(Message(now.AddMilliseconds(-i)));
            if (i % 100 == 0) await store.FlushActivityAsync();
        }
        await store.FlushActivityAsync();
        Assert.Equal(0, store.DroppedActivityEvents);
        var graph = await GraphAtDeadline(store, now, ["sender", "receiver"], expired: false);
        Assert.True(graph.Truncated);
        Assert.Equal(4000, Assert.Single(graph.Edges).CallCount);
    }

    [Fact]
    public async Task Edge_limit_keeps_newest_connections_and_reports_truncation()
    {
        using var store = new SqliteStore(DatabasePath());
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 22; i++) await Session(store, "session" + i);
        var offset = 0;
        for (var source = 0; source < 22; source++)
            for (var target = 0; target < 22; target++)
                if (source != target) store.Record(Message(now.AddMilliseconds(-offset++), "session" + source, "session" + target));
        await store.FlushActivityAsync();
        var graph = await GraphAtDeadline(store, now, Enumerable.Range(0, 22).Select(index => "session" + index).ToArray(), expired: false);
        Assert.True(graph.Truncated);
        var edges = graph.Edges;
        Assert.Equal(300, edges.Length);
        Assert.Contains(edges, edge => edge.SourceSessionId == "session0" && edge.TargetSessionId == "session1");
    }

    [Fact]
    public async Task Expired_interaction_deadline_marks_the_sample_partial_without_reading_evidence()
    {
        using var store = new SqliteStore(DatabasePath());
        var now = DateTimeOffset.UtcNow;
        store.Record(Message(now));
        await store.FlushActivityAsync();
        var graph = await GraphAtDeadline(store, now, ["sender", "receiver"], expired: true);
        Assert.True(graph.Truncated);
        Assert.Empty(graph.Edges);
    }

    private static async Task<DashboardInteractions> GraphAtDeadline(SqliteStore store, DateTimeOffset now, string[] sessionIds, bool expired)
    {
        var sessions = sessionIds.ToDictionary(id => id, id => new DashboardSession { SessionId = id, RepositoryId = "repo" }, StringComparer.Ordinal);
        var read = typeof(SqliteStore).GetMethod("ReadInteractionsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        // Exercise the real SQL/index and projection with the existing deadline argument controlled independently of scheduler load.
        // The count/edge caps get an unexpired boundary; the deadline test gets an expired one. No production budget changes or hooks.
        var offset = System.Diagnostics.Stopwatch.Frequency * 3600;
        var started = System.Diagnostics.Stopwatch.GetTimestamp() + (expired ? -offset : offset);
        return Assert.IsType<DashboardInteractions>(await (Task<DashboardInteractions?>)read.Invoke(store, [sessions, now, started, CancellationToken.None])!);
    }

    private static async Task<RelayObservation> CompleteObservation(SqliteStore store, DateTimeOffset now)
    {
        RelayObservation? observation = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            observation = await store.ReadObservationAsync(now, "current");
            if (observation is { SessionsTruncated: false, Interactions.Truncated: false }) return observation;
            if (attempt < 19) await Task.Delay(50);
        }
        Assert.Fail("The small metadata fixture did not produce a complete observation after 20 samples. Last result: " + JsonSerializer.Serialize(observation));
        return observation!;
    }

    private static string DatabasePath() => Path.Combine(Path.GetTempPath(), "cocern-interactions-" + Guid.NewGuid().ToString("N"), "relay.db");
    private static ActivityEvent Message(DateTimeOffset at, string sender = "sender", string recipient = "receiver", double duration = 5) => new()
    {
        Timestamp = at, Kind = ActivityEvent.Call, RepositoryId = "repo", SessionId = sender,
        Tool = "send_message", Ok = true, Code = "Ok", DurationMilliseconds = duration,
        Target = "message:" + Uri.EscapeDataString(sender) + ">" + Uri.EscapeDataString(recipient)
    };

    private static Task Session(SqliteStore store, string id, string repository = "repo")
    {
        var document = new JsonObject { ["sessionId"] = id, ["label"] = id, ["repositoryId"] = repository };
        var state = new GrainState<StateEnvelope<CoordinationDocument>>(new() { Value = new() { Json = document.ToJsonString() } });
        return new SqliteGrainStorage(store).WriteStateAsync("session", GrainId.Create("cocern.session", id), state);
    }
}
