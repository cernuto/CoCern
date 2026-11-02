using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain;
using Xunit;

namespace CoCern.Tests;

/// <summary>
/// A worker page is cut to the aggregate reply budget with its machine summary included, by count rather than by stripping its bindings,
/// and its cursor continues from the cut.
/// </summary>
public sealed class WorkerListFitTests
{
    private static readonly RelayCaller Caller = new("alice", true, ["repo"], true);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-29T00:00:00Z");

    private static RelayReply Call(StateEnvelope<CoordinationDocument> state, string op, JsonObject args)
    {
        args["repositoryId"] = "repo";
        return CoordinationStateMachine.Apply(state, "control", "key", Caller, op, args, Now);
    }

    private static JsonObject Data(RelayReply reply) => JsonNode.Parse(reply.DataJson)!.AsObject();

    private static JsonObject Binding() => new()
    {
        ["workspacePath"] = @"C:\Users\PaulC\source\repos\CoCern",
        ["roslyn"] = new JsonObject { ["solutionPath"] = @"C:\Users\PaulC\source\repos\CoCern\CoCern.slnx" },
        ["visualStudio"] = new JsonObject { ["solutionPath"] = @"C:\Users\PaulC\source\repos\CoCern\CoCern.slnx" }
    };

    /// <summary>Opens <paramref name="count"/> sessions in one repository, each on its own machine, with or without a binding.</summary>
    private static (StateEnvelope<CoordinationDocument> State, HashSet<string> Ids) Directory(int count, bool bound = true)
    {
        var state = new StateEnvelope<CoordinationDocument>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var args = new JsonObject { ["requestId"] = "open-" + i, ["machineId"] = "machine-" + i, ["label"] = $"claude: worker-list fit session number {i:D3}" };
            if (bound) args["binding"] = Binding();
            var opened = Call(state, "session_open", args);
            Assert.True(opened.Ok, opened.Code);
            ids.Add(Data(opened)["sessionId"]!.GetValue<string>());
            state.Effects.Clear();
        }
        return (state, ids);
    }

    private static JsonArray AssertFittedPage(RelayReply reply, int total)
    {
        Assert.True(reply.Ok, reply.Code);
        Assert.True(Replies.FitsWire(reply, RelayLimits.AggregateReplyBytes));
        var data = Data(reply);
        Assert.True(data["truncated"]!.GetValue<bool>());
        var items = data["items"]!.AsArray();
        Assert.NotEmpty(items);
        Assert.True(items.Count < 50, $"{items.Count} items");
        Assert.Equal(total, data["total"]!.GetValue<int>());
        Assert.Equal(items.Count, data["nextCursor"]!.GetValue<int>());
        return items;
    }

    [Theory]
    [InlineData(50)]
    [InlineData(200)]
    public void A_large_directory_page_is_cut_by_count_with_its_bindings_and_machines_intact(int limit)
    {
        var (state, _) = Directory(120);
        var items = AssertFittedPage(Call(state, "worker_list", new() { ["limit"] = limit }), 120);
        // The page is shortened, never degraded: each returned worker still carries its binding.
        Assert.All(items, item => { Assert.IsType<JsonObject>(item!["binding"]); Assert.Null(item["bindingTruncated"]); });
    }

    [Theory]
    [InlineData(50)]
    [InlineData(200)]
    public void A_large_directory_of_unbound_sessions_is_cut_by_count(int limit)
    {
        var (state, _) = Directory(120, bound: false);
        AssertFittedPage(Call(state, "worker_list", new() { ["limit"] = limit }), 120);
    }

    [Fact]
    public void Following_the_cursor_visits_every_session_once()
    {
        var (state, ids) = Directory(120);
        var seen = new List<string>();
        int? cursor = 0;
        while (cursor is not null)
        {
            var reply = Call(state, "worker_list", new() { ["limit"] = 50, ["cursor"] = cursor.Value });
            Assert.True(reply.Ok, reply.Code);
            Assert.True(Replies.FitsWire(reply, RelayLimits.AggregateReplyBytes));
            var data = Data(reply);
            seen.AddRange(data["items"]!.AsArray().Select(x => x!["sessionId"]!.GetValue<string>()));
            cursor = data["nextCursor"]?.GetValue<int>();
            Assert.True(seen.Count <= 120, "the cursor never ends");
        }
        Assert.Equal(120, seen.Count);
        Assert.Equal(ids, seen.ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void A_small_directory_is_returned_whole()
    {
        var (state, ids) = Directory(5);
        var data = Data(Call(state, "worker_list", new() { ["limit"] = 50 }));
        Assert.False(data["truncated"]!.GetValue<bool>());
        Assert.Null(data["nextCursor"]);
        Assert.Equal(5, data["total"]!.GetValue<int>());
        Assert.Equal(ids, data["items"]!.AsArray().Select(x => x!["sessionId"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal));
    }
}
