using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Domain.Messages;
using static CoCern.Domain.JsonFields;
using Xunit;

namespace CoCern.Tests;

/// <summary>Regression tests for findings of the whole-codebase review.</summary>
public sealed class ReviewFixTests
{
    private static readonly RelayCaller Hub = new("alice", false, ["repo"], true);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-29T00:00:00Z");

    private static RelayReply Apply(StateEnvelope<CoordinationDocument> state, string kind, string operation, JsonObject args, RelayCaller? caller = null)
    {
        args["repositoryId"] = "repo";
        return CoordinationStateMachine.Apply(state, kind, "key", caller ?? Hub, operation, args, Now);
    }

    private static JsonObject State(StateEnvelope<CoordinationDocument> state) => JsonNode.Parse(state.Value.Json)!.AsObject();

    [Fact]
    public void A_late_older_close_never_lowers_a_work_tombstone()
    {
        var session = new StateEnvelope<CoordinationDocument>();
        Assert.True(Apply(session, "session", Operations.Open, new() { ["principalId"] = "alice", ["sessionToken"] = "secret", ["sessionId"] = "a" }).Ok);
        JsonObject Work(bool open, int seconds) => new ActionWorkReport("repo", "a", "default", "t", "k", 1, open, Now.AddSeconds(seconds)).ToJson();
        foreach (var (open, at) in new[] { (true, 0), (false, 30), (false, 10) }) Assert.True(Apply(session, "session", Operations.SessionWork, Work(open, at)).Ok);
        // An open reported at 20 predates the close at 30, so it stays closed.
        Assert.False(JsonNode.Parse(Apply(session, "session", Operations.SessionWork, Work(true, 20)).DataJson)!["tracked"]!.GetValue<bool>());
    }

    [Fact]
    public void The_directory_ignores_a_reordered_ownership_report()
    {
        var control = new StateEnvelope<CoordinationDocument>
        {
            Value = new() { Json = new JsonObject { ["sessions"] = new JsonObject { ["a"] = new JsonObject { ["sessionId"] = "a", ["principalId"] = "alice", ["repositoryId"] = "repo", ["observedAt"] = Now.ToString("O") } } }.ToJsonString() }
        };
        bool Report(bool owns, long seq) => JsonNode.Parse(Apply(control, "control", Operations.SessionOwns, new SessionOwnership("repo", "a", owns, Now, seq).ToJson()).DataJson)!["tracked"]!.GetValue<bool>();
        Assert.True(Report(true, 1));
        Assert.True(Report(false, 3));
        Assert.False(Report(true, 2));  // sent before the clear
        Assert.False(Report(false, 3)); // a repeated copy
        Assert.Null(State(control)["sessions"]!["a"]!["ownsWork"]);
    }

    [Fact]
    public void Session_open_arguments_cannot_preset_the_sessions_own_state()
    {
        var session = new StateEnvelope<CoordinationDocument>();
        Assert.True(Apply(session, "session", Operations.Open, new()
        {
            ["principalId"] = "alice", ["sessionToken"] = "secret", ["sessionId"] = "s1", ["label"] = "mine",
            ["requests"] = new JsonObject { ["alice:ack:x"] = new JsonObject() }, ["inbox"] = new JsonArray(new JsonObject { ["messageId"] = "forged" })
        }).Ok);
        var state = State(session);
        Assert.Equal("mine", state["label"]!.GetValue<string>());
        Assert.Null(state["inbox"]);
        Assert.False(state["requests"] is JsonObject { Count: > 0 });
    }

    private sealed class DeletingSource(int count) : IDueWorkSource
    {
        private readonly List<string> rows = Enumerable.Range(0, count).Select(index => index.ToString("D4")).ToList();
        public List<int> Offsets { get; } = [];
        public int Remaining => rows.Count;
        public void Delete(string id) => rows.Remove(id);
        public Task<IReadOnlyList<DueWorkReference>> ReadDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken = default, int offset = 0)
        {
            Offsets.Add(offset);
            IReadOnlyList<DueWorkReference> due = rows.Skip(offset).Take(limit).Select(id => new DueWorkReference("board", "cocern.board", "k" + id, id, "effect", now)).ToArray();
            return Task.FromResult(due);
        }
    }

}

internal static class JsonObjectTestExtensions
{
    public static JsonObject Also(this JsonObject value, Action<JsonObject> change) { change(value); return value; }
}
