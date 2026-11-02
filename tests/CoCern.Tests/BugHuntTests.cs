using System.Text.Json.Nodes;
using System.Text.Json;
using CoCern.Contracts;
using CoCern.Core;
using Orleans;
using CoCern.Domain;
using Xunit;

namespace CoCern.Tests;

/// <summary>Regression tests for the bug hunt over the claim, work-tracking and enrollment changes.</summary>
public sealed class BugHuntTests
{
    private static readonly RelayCaller Hub = new("alice", false, ["repo"], true);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-01T00:00:00Z");

    private static RelayReply Apply(StateEnvelope<CoordinationDocument> state, string kind, string operation, JsonObject args, int seconds = 0)
    {
        args["repositoryId"] = "repo";
        return CoordinationStateMachine.Apply(state, kind, "key", Hub, operation, args, Now.AddSeconds(seconds));
    }

    private static JsonObject State(StateEnvelope<CoordinationDocument> state) => JsonNode.Parse(state.Value.Json)!.AsObject();
    private static JsonObject Payload(DurableEffect effect) => JsonNode.Parse(effect.PayloadJson)!.AsObject();

    private static StateEnvelope<CoordinationDocument> OpenSession(JsonObject? extra = null)
    {
        var session = new StateEnvelope<CoordinationDocument>();
        var open = new JsonObject { ["sessionId"] = "s1", ["sessionToken"] = "secret", ["principalId"] = "alice" };
        foreach (var property in extra ?? []) open[property.Key] = property.Value?.DeepClone();
        Assert.True(Apply(session, "session", Operations.Open, open).Ok);
        return session;
    }

    [Fact]
    public async Task Session_open_arguments_cannot_forge_freshness_or_keep_a_session_from_being_pruned()
    {
        var app = new RelayApplication(System.Reflection.DispatchProxy.Create<IGrainFactory, CoordinationTests.TestGrainFactory>());
        async Task<JsonObject> Invoke(string tool, JsonObject args)
        {
            args["repositoryId"] = "repo"; args["requestId"] = Guid.NewGuid().ToString("N");
            var reply = await app.ExecuteAsync(Hub with { IsInternal = false }, tool, JsonSerializer.SerializeToElement(args));
            Assert.True(reply.Ok, reply.Code + " " + reply.DataJson);
            return JsonNode.Parse(reply.DataJson)!.AsObject();
        }
        var forever = "9999-12-31T00:00:00Z";
        var session = await Invoke(RelayTools.SessionOpen, new()
        {
            ["activeAt"] = forever, ["freshUntil"] = forever, ["observedAt"] = forever, ["availability"] = "available", ["bindingVersion"] = 41, ["label"] = "mine"
        });
        var status = await Invoke(RelayTools.GetStatus, new() { ["sessionId"] = session["sessionId"]!.DeepClone(), ["sessionToken"] = session["sessionToken"]!.DeepClone() });
        Assert.DoesNotContain("9999", status.ToJsonString());
        Assert.DoesNotContain("available", status.ToJsonString());
        Assert.DoesNotContain("41", status["bindingVersion"]?.ToJsonString() ?? "");
    }

    [Fact]
    public void A_session_drops_an_overlong_role_and_never_copies_activity()
    {
        var session = OpenSession(new() { ["activeAt"] = "9999-12-31T00:00:00Z", ["role"] = new string('m', RelayLimits.MaxActivityRoleLength + 1), ["label"] = "mine" });
        var state = State(session);
        Assert.Equal((null, null, "mine"), (state["activeAt"], state["role"], JsonFields.Text(state, "label")));
    }

    [Fact]
    public void The_directory_orders_work_reports_from_one_second_by_their_sequence()
    {
        var control = new StateEnvelope<CoordinationDocument>
        {
            Value = new() { Json = new JsonObject { ["sessions"] = new JsonObject { ["s1"] = new JsonObject { ["sessionId"] = "s1", ["repositoryId"] = "repo" } } }.ToJsonString() }
        };
        JsonObject Report(bool owns, long seq) => new() { ["sessionId"] = "s1", ["owns"] = owns, ["at"] = Now.ToString("O"), ["seq"] = seq };
        bool Owns() => State(control)["sessions"]!["s1"]!["ownsWork"] is not null;
        // Open, close and open again within one second, the close delivered last.
        Assert.True(Apply(control, "control", Operations.SessionWork, Report(true, 1)).Ok);
        Assert.True(Apply(control, "control", Operations.SessionWork, Report(true, 3)).Ok);
        Assert.False(JsonNode.Parse(Apply(control, "control", Operations.SessionWork, Report(false, 2)).DataJson)!["tracked"]!.GetValue<bool>());
        Assert.True(Owns());
        // A later close in the same second still lands.
        Assert.True(Apply(control, "control", Operations.SessionWork, Report(false, 4)).Ok);
        Assert.False(Owns());
    }

    [Fact]
    public void The_session_numbers_its_work_reports()
    {
        var session = OpenSession();
        JsonObject Work(bool open) => new() { ["actionKey"] = "k", ["open"] = open, ["at"] = Now.ToString("O") };
        Assert.True(Apply(session, "session", Operations.SessionWork, Work(true)).Ok);
        Assert.True(Apply(session, "session", Operations.SessionWork, Work(false), 1).Ok);
        Assert.Equal([1L, 2L], session.Effects.Where(effect => effect.Operation == Operations.SessionWork).Select(effect => Payload(effect)["seq"]!.GetValue<long>()));
    }

    [Fact]
    public void A_reordered_older_close_leaves_a_newer_open_in_the_ledger()
    {
        var session = OpenSession();
        JsonObject Work(bool open, int at) => new() { ["actionKey"] = "k", ["open"] = open, ["at"] = Now.AddSeconds(at).ToString("O") };
        bool Owns(RelayReply reply) => JsonNode.Parse(reply.DataJson)!["owns"]!.GetValue<bool>();
        Assert.True(Owns(Apply(session, "session", Operations.SessionWork, Work(true, 9), 10)));
        // The close from 7 arrives after the reopen at 9: the action came back to this session, so it stays open.
        Assert.True(Owns(Apply(session, "session", Operations.SessionWork, Work(false, 7), 10)));
        Assert.False(Owns(Apply(session, "session", Operations.SessionWork, Work(false, 10), 10)));
    }

    [Fact]
    public void An_archived_task_stays_closed_when_an_older_raise_arrives_late()
    {
        var session = OpenSession();
        Assert.True(Apply(session, "session", Operations.AttentionClose, new() { ["taskIds"] = new JsonArray("t1", "t2"), ["revision"] = 20 }).Ok);
        var late = Apply(session, "session", Operations.AttentionRaise, new() { ["kind"] = AttentionKinds.ReviewReady, ["taskId"] = "t1", ["revision"] = 19 });
        Assert.Equal("stale", JsonNode.Parse(late.DataJson)!["reason"]!.GetValue<string>());
        Assert.Empty(State(session)["attention"]?.AsObject() ?? []);
        // A later raise (a task reusing the ID) is real.
        Assert.True(JsonNode.Parse(Apply(session, "session", Operations.AttentionRaise, new() { ["kind"] = AttentionKinds.ReviewReady, ["taskId"] = "t1", ["revision"] = 21 }).DataJson)!["raised"]!.GetValue<bool>());
    }

    [Fact]
    public void A_second_failed_action_keeps_the_first_in_the_managers_note()
    {
        var board = new StateEnvelope<CoordinationDocument>();
        Assert.True(Apply(board, "board", RelayTools.TaskUpdate, new() { ["taskId"] = "t", ["requestId"] = "create", ["expectedVersion"] = 0 }).Ok);
        Assert.True(Apply(board, "board", RelayTools.TaskClaim, new() { ["taskId"] = "t", ["sessionId"] = "s", ["requestId"] = "claim", ["expectedVersion"] = 1 }).Ok);
        var version = 2;
        foreach (var action in new[] { "a", "b" })
        {
            Assert.True(Apply(board, "board", RelayTools.ActionBegin, new() { ["taskId"] = "t", ["actionId"] = action, ["inputFingerprint"] = "f", ["sessionId"] = "s", ["requestId"] = "begin-" + action, ["expectedVersion"] = version++ }).Ok);
            Assert.True(Apply(board, "board", RelayTools.ActionReport, new() { ["taskId"] = "t", ["actionId"] = action, ["sessionId"] = "s", ["requestId"] = "report-" + action, ["expectedVersion"] = version++, ["attempt"] = 1, ["outcome"] = "failed" }).Ok);
        }
        var note = board.Effects.Where(effect => effect.Operation == Operations.AttentionRoute).Select(Payload)
            .Where(route => JsonFields.Text(route, "operation") == Operations.AttentionRaise && JsonFields.Text(route["item"]!.AsObject(), "kind") == AttentionKinds.Failed)
            .Select(route => JsonFields.Text(route["item"]!.AsObject(), "note")).Last();
        Assert.Equal("action a, b failed", note);
    }

    private static RelayReply? Guard(string tool, string json)
    {
        Assert.True(RelayToolCatalog.TryGet(tool, out var definition));
        return RelayRequestGuard.Validate(Hub, definition!, JsonDocument.Parse(System.Text.Encoding.UTF8.GetBytes(json)).RootElement, out _);
    }

    [Fact]
    public void A_repeated_key_or_unreadable_text_is_refused_not_an_internal_error()
    {
        Assert.Equal(RelayErrorCodes.InvalidArguments, Guard(RelayTools.TaskGet, """{"repositoryId":"repo","repositoryId":"other"}""")?.Code);
        Assert.True(RelayToolCatalog.TryGet(RelayTools.TaskGet, out var tool));
        var unreadable = JsonDocument.Parse(new byte[] { (byte)'{', (byte)'"', (byte)'a', (byte)'"', (byte)':', (byte)'"', 0xFF, 0xFE, (byte)'"', (byte)'}' }).RootElement;
        Assert.Equal(RelayErrorCodes.InvalidArguments, RelayRequestGuard.Validate(Hub, tool!, unreadable, out _)?.Code);
    }

    [Theory]
    [InlineData("1.5", false)]
    [InlineData("18446744073709551616", false)]
    [InlineData("true", false)]
    [InlineData("\"one\"", false)]
    [InlineData("\"3\"", true)]
    [InlineData("3", true)]
    [InlineData("null", true)]
    public void A_version_must_be_a_whole_number_never_read_as_zero(string version, bool accepted) =>
        Assert.Equal(accepted, Guard(RelayTools.TaskUpdate, $$"""{"repositoryId":"repo","requestId":"r","taskId":"t","expectedVersion":{{version}}}""") is null);

    [Theory]
    [InlineData("\"   \"")]
    [InlineData("\"a\\nb\"")]
    [InlineData("\"a\\u0000b\"")]
    public void An_identifier_may_not_be_blank_or_hold_control_characters(string taskId) =>
        Assert.Equal(RelayErrorCodes.InvalidArguments, Guard(RelayTools.TaskUpdate, $$"""{"repositoryId":"repo","requestId":"r","taskId":{{taskId}},"expectedVersion":0}""")?.Code);

    [Fact]
    public void A_note_needs_content() =>
        Assert.Equal(RelayErrorCodes.InvalidArguments, Guard(RelayTools.ContextPut, """{"repositoryId":"repo","requestId":"r","sessionId":"s","sessionToken":"t","contextId":"c","content":null,"expectedVersion":0}""")?.Code);

    [Fact]
    public void A_message_to_a_session_that_was_never_opened_is_dropped_not_retried_forever()
    {
        var nobody = new StateEnvelope<CoordinationDocument>();
        var delivered = Apply(nobody, "session", Operations.Deliver, new() { ["messageId"] = "m1", ["text"] = "hi", ["senderSessionId"] = "s1" });
        Assert.True(delivered.Ok);
        Assert.False(JsonNode.Parse(delivered.DataJson)!["delivered"]!.GetValue<bool>());
        Assert.Null(State(nobody)["inbox"]);
    }
}
