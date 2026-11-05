using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Domain;
using Xunit;

namespace CoCern.Tests;

// Primary transaction and persisted due-effect tests. The separate real-silo
// gate must also prove the due pump and self-dispatch, without a read triggering it.
public sealed class EventWaitDeadlineTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static RelayCaller Internal => new("principal", false, ["repo"], true);

    [Fact]
    public void Register_commits_one_secret_free_deadline_effect_and_replay_does_not_duplicate_it()
    {
        var state = Open(); var args = RegisterArgs();
        Assert.True(Apply(state, "__event_wait_register", args, Epoch).Ok);
        var due = Assert.Single(state.Effects, x => x.Operation == "__event_wait_due");
        Assert.Equal(Epoch.AddMinutes(10), due.DueAt);
        Assert.Equal(("session", "owner"), (due.TargetKind, due.TargetKey));
        Assert.DoesNotContain("fixture-token", due.PayloadJson);
        Assert.Contains(state.GetDueWork(), item => item.Id == due.Id && item.DueAt == due.DueAt);
        Assert.True(Apply(state, "__event_wait_register", args, Epoch.AddMinutes(1)).Ok);
        Assert.Single(state.Effects, x => x.Operation == "__event_wait_due");
    }

    [Fact]
    public void Persisted_deadline_expires_once_after_restart_without_a_status_or_inbox_read()
    {
        var state = Open();
        Assert.True(Apply(state, "__event_wait_register", RegisterArgs(), Epoch).Ok);
        var due = Assert.Single(state.Effects, x => x.Operation == "__event_wait_due");
        var restarted = new StateEnvelope<CoordinationDocument> { Value = new() { Json = state.Value.Json } };
        Assert.True(Apply(restarted, due.Operation, JsonNode.Parse(due.PayloadJson)!.AsObject(), due.DueAt).Ok);
        var terminal = JsonNode.Parse(restarted.Value.Json)!["eventWaits"]!["wait"]!["terminal"]!;
        Assert.Equal("expired", terminal["outcome"]!.GetValue<string>());
        Assert.Single(restarted.Effects, x => x.Operation == "__deliver");
        restarted.Effects.Clear();
        Assert.True(Apply(restarted, due.Operation, JsonNode.Parse(due.PayloadJson)!.AsObject(), due.DueAt.AddMinutes(1)).Ok);
        Assert.Empty(restarted.Effects);
    }

    [Fact]
    public void A_stale_deadline_after_cancel_or_session_close_cannot_emit_another_notice()
    {
        foreach (var close in new[] { false, true })
        {
            var state = Open();
            Assert.True(Apply(state, "__event_wait_register", RegisterArgs(), Epoch).Ok);
            var due = Assert.Single(state.Effects, x => x.Operation == "__event_wait_due");
            var end = Args();
            if (close) { end["closeSeq"] = 1; end["force"] = true; end["requestedBy"] = "fixture"; }
            else end["waitId"] = "wait";
            Assert.True(Apply(state, close ? "__session_expire" : "__event_wait_cancel", end, Epoch.AddMinutes(1)).Ok);
            state.Effects.Clear();
            Assert.True(Apply(state, due.Operation, JsonNode.Parse(due.PayloadJson)!.AsObject(), due.DueAt).Ok);
            Assert.Empty(state.Effects);
        }
    }

    private static StateEnvelope<CoordinationDocument> Open()
    {
        var state = new StateEnvelope<CoordinationDocument> { Value = new() { Json = "{}" } };
        var args = Args(); args["principalId"] = "principal";
        Assert.True(Apply(state, "__open", args, Epoch).Ok);
        state.Effects.Clear(); return state;
    }
    private static JsonObject RegisterArgs()
    {
        var args = Args();
        args["request"] = JsonSerializer.SerializeToNode(new EventWaitRequest("wait", "claim_released", "resource", "12", Epoch.AddMinutes(10)), Json);
        return args;
    }
    private static JsonObject Args() => new() { ["repositoryId"] = "repo", ["sessionId"] = "owner", ["sessionToken"] = "fixture-token" };
    private static RelayReply Apply(StateEnvelope<CoordinationDocument> state, string operation, JsonObject args, DateTimeOffset at) =>
        CoordinationStateMachine.Apply(state, "session", "owner", Internal, operation, args, at);
}
