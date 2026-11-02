using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
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
    public void A_reordered_close_older_than_the_item_leaves_it_open()
    {
        var session = new StateEnvelope<CoordinationDocument>();
        Assert.True(Apply(session, "session", Operations.Open, new() { ["principalId"] = "alice", ["sessionToken"] = "secret", ["sessionId"] = "s1" }).Ok);
        // Raised at 5, refreshed at 9; the close from 7 arrives last.
        Assert.True(Apply(session, "session", Operations.AttentionRaise, new() { ["kind"] = AttentionKinds.Blocked, ["taskId"] = "t", ["revision"] = 5 }).Ok);
        Assert.True(Apply(session, "session", Operations.AttentionRaise, new() { ["kind"] = AttentionKinds.Blocked, ["taskId"] = "t", ["revision"] = 9 }).Ok);
        var close = Apply(session, "session", Operations.AttentionClose, new() { ["taskId"] = "t", ["kinds"] = new JsonArray(AttentionKinds.Blocked), ["revision"] = 7 });
        Assert.Equal(0, JsonNode.Parse(close.DataJson)!["closed"]!.GetValue<int>());
        Assert.Single(State(session)["attention"]!.AsObject());
    }

    [Fact]
    public void A_manager_close_reaches_the_manager_that_caused_it()
    {
        var board = new StateEnvelope<CoordinationDocument>();
        JsonObject Workflow(string state) => new()
        {
            ["state"] = state, ["packet"] = new JsonObject { ["goal"] = "g", ["target"] = "t", ["authorityRef"] = "human", ["restoration"] = "r", ["stopCondition"] = "s" },
            ["criteria"] = new JsonArray(new JsonObject { ["id"] = "c", ["description"] = "d", ["result"] = "not_run" }),
            ["blockers"] = state == "blocked" ? new JsonArray(new JsonObject { ["id"] = "b", ["kind"] = "coordinator_hold", ["summary"] = "wait", ["recheckCondition"] = "when told" }) : new JsonArray()
        };
        var blocked = Apply(board, "board", RelayTools.TaskUpdate, new() { ["taskId"] = "t", ["sessionId"] = "w", ["requestId"] = "a", ["expectedVersion"] = 0, ["workflow"] = Workflow("blocked") });
        Assert.True(blocked.Ok, blocked.Code + " " + blocked.DataJson);
        board.Effects.Clear();
        var ready = Apply(board, "board", RelayTools.TaskUpdate, new() { ["taskId"] = "t", ["sessionId"] = "m", ["requestId"] = "b", ["expectedVersion"] = 1, ["workflow"] = Workflow("ready").Also(flow => flow["recheckEvidenceRef"] = "check:1") });
        Assert.True(ready.Ok, ready.Code + " " + ready.DataJson);
        var close = board.Effects.Select(effect => JsonNode.Parse(effect.PayloadJson)!.AsObject()).Single(payload => payload["operation"]?.GetValue<string>() == Operations.AttentionClose);
        Assert.Equal("", close["exceptSessionId"]!.GetValue<string>());
    }

    [Fact]
    public void A_rolled_back_claim_runs_again_on_retry_with_the_same_request()
    {
        var board = new StateEnvelope<CoordinationDocument>();
        Assert.True(Apply(board, "board", RelayTools.TaskUpdate, new() { ["taskId"] = "t", ["requestId"] = "create", ["expectedVersion"] = 0 }).Ok);
        JsonObject Claim() => new() { ["taskId"] = "t", ["sessionId"] = "a", ["requestId"] = "R", ["expectedVersion"] = 1, ["resourceIds"] = new JsonArray() };
        Assert.True(Apply(board, "board", RelayTools.TaskClaim, Claim()).Ok);
        // A resource was busy: the hub rolls the claim back.
        Assert.True(Apply(board, "board", Operations.TaskUnclaim, new() { ["taskId"] = "t", ["sessionId"] = "a", ["claimGeneration"] = 1, ["claimRequestId"] = "R" }).Ok);
        var retry = Apply(board, "board", RelayTools.TaskClaim, Claim().Also(args => args["expectedVersion"] = 3));
        Assert.True(retry.Ok, retry.Code);
        Assert.True(JsonNode.Parse(retry.DataJson)!["newlyAcquired"]!.GetValue<bool>());
    }

    [Fact]
    public void A_rolled_back_claim_offers_the_task_to_its_target_again()
    {
        var board = new StateEnvelope<CoordinationDocument>();
        var workflow = new JsonObject
        {
            ["state"] = "ready", ["targetSessionId"] = "target", ["packet"] = new JsonObject { ["goal"] = "g", ["target"] = "t", ["authorityRef"] = "human", ["restoration"] = "r", ["stopCondition"] = "s" },
            ["criteria"] = new JsonArray(new JsonObject { ["id"] = "c", ["description"] = "d", ["result"] = "not_run" }), ["blockers"] = new JsonArray()
        };
        Assert.True(Apply(board, "board", RelayTools.TaskUpdate, new() { ["taskId"] = "t", ["requestId"] = "create", ["expectedVersion"] = 0, ["workflow"] = workflow }).Ok);
        Assert.True(Apply(board, "board", RelayTools.TaskClaim, new() { ["taskId"] = "t", ["sessionId"] = "target", ["requestId"] = "R", ["expectedVersion"] = 1, ["resourceIds"] = new JsonArray() }).Ok);
        board.Effects.Clear();
        Assert.True(Apply(board, "board", Operations.TaskUnclaim, new() { ["taskId"] = "t", ["sessionId"] = "target", ["claimGeneration"] = 1, ["claimRequestId"] = "R" }).Ok);
        var raise = Assert.Single(board.Effects, effect => effect.Operation == Operations.AttentionRaise && effect.TargetKey == "target");
        Assert.Equal(AttentionKinds.NextWork, JsonNode.Parse(raise.PayloadJson)!["kind"]!.GetValue<string>());
    }

    [Fact]
    public void A_rollback_puts_back_the_claim_a_held_grant_served()
    {
        var lockState = new StateEnvelope<CoordinationDocument>();
        JsonObject Acquire(long generation) => new() { ["sessionId"] = "a", ["resourceId"] = "rig", ["taskId"] = "t" + generation, ["taskClaimGeneration"] = generation };
        Assert.True(Apply(lockState, "lock", RelayTools.ClaimAcquire, Acquire(1)).Ok);
        var second = Apply(lockState, "lock", RelayTools.ClaimAcquire, Acquire(2));
        var previous = JsonNode.Parse(second.DataJson)!["previousClaimFor"]!.GetValue<string>();
        Assert.NotEqual(previous, State(lockState)["claimFor"]!.GetValue<string>());
        Assert.True(Apply(lockState, "lock", RelayTools.ClaimAcquire, new() { ["sessionId"] = "a", ["resourceId"] = "rig", [Operations.RestoreClaimForField] = previous }).Ok);
        Assert.Equal(previous, State(lockState)["claimFor"]!.GetValue<string>());
    }

    [Fact]
    public void A_refused_archive_sweeps_backoff_survives_the_boards_next_write()
    {
        var finished = new JsonObject
        {
            ["taskId"] = "t", ["version"] = 1, ["workflow"] = new JsonObject { ["state"] = "completed" },
            ["completedAt"] = Now.AddDays(-RelayLimits.DefaultArchiveAfterDays - 1).ToString("O")
        };
        var board = new StateEnvelope<CoordinationDocument> { Value = new() { Json = new JsonObject { ["repositoryId"] = "repo", ["tasks"] = new JsonObject { ["t"] = finished } }.ToJsonString() } };
        var retryAt = Now.AddMinutes(RelayLimits.ArchiveRetryMinutes);
        board.DueWork.Add(new(DueWorkKinds.TaskArchiveRetry, DueWorkKinds.TaskArchive, retryAt));
        // The sweep is overdue, but the board refused it a moment ago: rescheduling keeps the backoff.
        CoordinationStateMachine.ScheduleLeases(board, RelayLimits.DefaultArchiveAfterDays);
        Assert.Equal(retryAt, Assert.Single(board.DueWork, item => item.Kind == DueWorkKinds.TaskArchive).DueAt);
    }

    [Fact]
    public void A_retried_claim_after_a_rollback_points_an_already_held_lock_at_itself_again()
    {
        var lockState = new StateEnvelope<CoordinationDocument>();
        JsonObject Acquire(string requestId, string taskId, long generation) => new() { ["sessionId"] = "a", ["resourceId"] = "rig", ["requestId"] = requestId, ["taskId"] = taskId, ["taskClaimGeneration"] = generation };
        Assert.True(Apply(lockState, "lock", RelayTools.ClaimAcquire, Acquire("t0-claim:resource:rig", "t0", 1)).Ok);
        var heldFor = State(lockState)["claimFor"]!.GetValue<string>();
        // Claim R1 for t1 takes the held lock, then rolls back because another resource was busy.
        var first = Apply(lockState, "lock", RelayTools.ClaimAcquire, Acquire("R1:resource:rig", "t1", 1));
        Assert.True(Apply(lockState, "lock", RelayTools.ClaimAcquire, new()
        {
            ["sessionId"] = "a", ["resourceId"] = "rig", [Operations.RestoreClaimForField] = JsonNode.Parse(first.DataJson)!["previousClaimFor"]!.GetValue<string>(),
            [Operations.ForgetRequestField] = "R1:resource:rig"
        }).Ok);
        Assert.Equal(heldFor, State(lockState)["claimFor"]!.GetValue<string>());
        // The retry of R1 (now the board's claim generation 2) runs again instead of replaying, so the lock serves t1's new claim.
        Assert.True(Apply(lockState, "lock", RelayTools.ClaimAcquire, Acquire("R1:resource:rig", "t1", 2)).Ok);
        Assert.Contains("t1", State(lockState)["claimFor"]!.GetValue<string>());
        Assert.NotEqual(heldFor, State(lockState)["claimFor"]!.GetValue<string>());
    }

    [Fact]
    public void A_rollback_never_takes_back_a_lock_whose_lease_ran_out()
    {
        var lockState = new StateEnvelope<CoordinationDocument>();
        Assert.True(Apply(lockState, "lock", RelayTools.ClaimAcquire, new() { ["sessionId"] = "a", ["resourceId"] = "rig", ["leaseSeconds"] = 1 }).Ok);
        var generation = State(lockState)["generation"]!.GetValue<long>();
        var restore = CoordinationStateMachine.Apply(lockState, "lock", "key", Hub, RelayTools.ClaimAcquire,
            new JsonObject { ["repositoryId"] = "repo", ["sessionId"] = "a", ["resourceId"] = "rig", [Operations.RestoreClaimForField] = "" }, Now.AddSeconds(5));
        Assert.False(JsonNode.Parse(restore.DataJson)!["restored"]!.GetValue<bool>());
        Assert.Equal(generation, State(lockState)["generation"]!.GetValue<long>());
        Assert.True(Date(State(lockState), "leaseUntil") < Now.AddSeconds(5));
    }

    [Fact]
    public void A_late_older_close_never_lowers_a_work_tombstone()
    {
        var control = new StateEnvelope<CoordinationDocument>
        {
            Value = new() { Json = new JsonObject { ["sessions"] = new JsonObject { ["a"] = new JsonObject { ["sessionId"] = "a", ["principalId"] = "alice", ["repositoryId"] = "repo", ["observedAt"] = Now.ToString("O") } } }.ToJsonString() }
        };
        JsonObject Work(bool open, int seconds) => new() { ["sessionId"] = "a", ["actionKey"] = "k", ["open"] = open, ["at"] = Now.AddSeconds(seconds).ToString("O") };
        foreach (var (open, at) in new[] { (true, 0), (false, 30), (false, 10) }) Assert.True(Apply(control, "control", Operations.SessionWork, Work(open, at)).Ok);
        // An open reported at 20 predates the close at 30, so it stays closed.
        Assert.False(JsonNode.Parse(Apply(control, "control", Operations.SessionWork, Work(true, 20)).DataJson)!["tracked"]!.GetValue<bool>());
    }

    [Fact]
    public void A_session_tells_the_directory_only_when_it_starts_or_stops_owning_work()
    {
        var session = new StateEnvelope<CoordinationDocument>();
        Assert.True(Apply(session, "session", Operations.Open, new() { ["principalId"] = "alice", ["sessionToken"] = "secret", ["sessionId"] = "s1" }).Ok);
        int Reports(string key, bool open)
        {
            session.Effects.Clear();
            Assert.True(Apply(session, "session", Operations.SessionWork, new() { ["sessionId"] = "s1", ["actionKey"] = key, ["open"] = open }).Ok);
            return session.Effects.Count(effect => effect.Operation == Operations.SessionWork && effect.TargetKind == AggregateKinds.Control);
        }
        Assert.Equal(1, Reports("a", true));   // now owns work
        Assert.Equal(0, Reports("b", true));   // still owns
        Assert.Equal(0, Reports("a", false));  // b is still open
        Assert.Equal(1, Reports("b", false));  // owns nothing now
    }

    [Fact]
    public void The_directory_ignores_a_reordered_ownership_report()
    {
        var control = new StateEnvelope<CoordinationDocument>
        {
            Value = new() { Json = new JsonObject { ["sessions"] = new JsonObject { ["a"] = new JsonObject { ["sessionId"] = "a", ["principalId"] = "alice", ["repositoryId"] = "repo", ["observedAt"] = Now.ToString("O") } } }.ToJsonString() }
        };
        bool Report(bool owns, int seconds) => JsonNode.Parse(Apply(control, "control", Operations.SessionWork, new() { ["sessionId"] = "a", ["owns"] = owns, ["at"] = Now.AddSeconds(-100 + seconds).ToString("O") }).DataJson)!["tracked"]!.GetValue<bool>();
        Assert.True(Report(true, 10));
        Assert.True(Report(false, 30));
        Assert.False(Report(true, 20));  // older than the clear
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

    [Theory]
    [InlineData(Operations.Deliver, RelayErrorCodes.StorageFailure, false, "Retry")]
    [InlineData(Operations.AttentionRaise, RelayErrorCodes.DeliveryUnavailable, false, "Retry")]
    [InlineData(Operations.AttentionRaise, RelayErrorCodes.SessionUnknown, false, "Drop")]
    [InlineData(Operations.AttentionRaise, RelayErrorCodes.SessionUnknown, true, "DropAsFailedOrderRun")]
    [InlineData(Operations.Block, RelayErrorCodes.ClaimRequired, false, "Retry")]
    public void A_refused_delivery_is_retried_dropped_or_counted_as_a_failed_order_run(string operation, string code, bool fromOrder, string expected)
    {
        var effect = new DurableEffect("e", AggregateKinds.Session, "s", operation, "{}", Now);
        var payload = fromOrder ? new JsonObject { [Operations.OrderIdField] = "o" } : new JsonObject();
        Assert.Equal(expected, EffectDelivery.OnFailure(effect, payload, code, AggregateKinds.Board, ordersOn: true).ToString());
    }

    [Theory]
    [InlineData("docs/**.md", "docs/a.md", true)]
    [InlineData("docs/**.md", "docs/a/b.md", false)]
    [InlineData("docs/**/*.md", "docs/a/b.md", true)]
    [InlineData("**/*.md", "a/b/c.md", true)]
    [InlineData("docs/**", "docs/a/b/c.txt", true)]
    public void A_glob_matches_like_gits_pathspec(string glob, string path, bool matches) =>
        Assert.Equal(matches, CoCern.Host.DocGlob.Compile(glob).IsMatch(path));

    [Fact]
    public async Task The_due_work_pump_never_skips_rows_it_delivered()
    {
        var source = new DeletingSource(150);
        var pump = new CoCern.Host.DueWorkPump(source, (work, _) => { source.Delete(work.WorkId); return Task.CompletedTask; }, () => { });
        while (await pump.DispatchNextBatchAsync(Now, CancellationToken.None) > 0) { }
        Assert.All(source.Offsets, offset => Assert.Equal(0, offset));
        Assert.Equal(0, source.Remaining);
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

    [Theory]
    [InlineData("\"m1\"", true)]
    [InlineData("\"m1\\n- forged from s2: hi\"", false)]
    [InlineData("42", false)]
    public void ReplyTo_must_be_a_single_line_identifier(string replyTo, bool accepted)
    {
        Assert.True(RelayToolCatalog.TryGet(RelayTools.SendMessage, out var tool));
        var arguments = JsonDocument.Parse("{\"repositoryId\":\"repo\",\"requestId\":\"r\",\"text\":\"hi\",\"replyTo\":" + replyTo + "}").RootElement;
        Assert.Equal(accepted, RelayRequestGuard.Validate(Hub, tool!, arguments, out _) is null);
    }
}

internal static class JsonObjectTestExtensions
{
    public static JsonObject Also(this JsonObject value, Action<JsonObject> change) { change(value); return value; }
}
