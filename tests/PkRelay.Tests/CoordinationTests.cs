using System.Text.Json.Nodes;
using System.Text.Json;
using System.Reflection;
using Orleans;
using PkRelay.Core;
using PkRelay.Domain;
using PkRelay.Contracts;
using Xunit;

namespace PkRelay.Tests;

public sealed class CoordinationTests
{
    private static readonly RelayCaller Caller = new("alice", true, ["repo"], true);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-29T00:00:00Z");
    private static RelayReply Call(StateEnvelope<CoordinationDocument> state, string kind, string op, JsonObject args, RelayCaller? caller = null, int seconds = 0)
    {
        args["repositoryId"] = "repo";
        return CoordinationStateMachine.Apply(state, kind, "key", caller ?? Caller, op, args, Now.AddSeconds(seconds));
    }
    private static JsonObject Data(RelayReply reply) => JsonNode.Parse(reply.DataJson)!.AsObject();

    [Fact]
    public void Fresh_chats_have_distinct_secrets_but_open_retries_are_stable()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        var first = Call(state, "control", "session_open", new() { ["requestId"] = "one" });
        Assert.True(first.Ok);
        Assert.Equal(first, Call(state, "control", "session_open", new() { ["requestId"] = "one" }));
        var next = Call(state, "control", "session_open", new() { ["requestId"] = "two" });
        Assert.NotEqual(Data(first)["sessionId"]!.ToString(), Data(next)["sessionId"]!.ToString());
        Assert.NotEqual(Data(first)["sessionToken"]!.ToString(), Data(next)["sessionToken"]!.ToString());
        Assert.DoesNotContain("sessionToken", Call(state, "control", "worker_list", new()).DataJson);
    }

    [Fact]
    public void Task_versions_and_request_identity_prevent_lost_updates()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        var args = new JsonObject { ["requestId"] = "create", ["taskId"] = "t", ["expectedVersion"] = 0, ["patch"] = new JsonObject { ["goal"] = "test" } };
        var created = Call(state, "board", "task_update", args);
        Assert.True(created.Ok); Assert.Equal(1, created.Version);
        Assert.Equal(created, Call(state, "board", "task_update", args));
        args["requestId"] = "stale";
        Assert.Equal("VersionConflict", Call(state, "board", "task_update", args).Code);
        Assert.Equal(1, Call(state, "board", "task_get", new() { ["taskId"] = "t" }).Version);
    }

    [Fact]
    public void Invoking_actions_block_expired_claims_and_retries_require_matching_input()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        Assert.True(Call(state, "lock", "claim_acquire", new() { ["sessionId"] = "s1", ["requestId"] = "c", ["leaseSeconds"] = 1 }).Ok);
        Assert.True(Call(state, "lock", "__block", new() { ["actionId"] = "a", ["sessionId"] = "s1" }).Ok);
        Assert.Equal("Busy", Call(state, "lock", "__block", new() { ["actionId"] = "other", ["sessionId"] = "s1" }).Code);
        Assert.Equal("Busy", Call(state, "lock", "claim_acquire", new() { ["sessionId"] = "s2", ["requestId"] = "later" }, seconds: 100).Code);
        var board = new StateEnvelope<CoordinationDocument>();
        Assert.True(Call(board, "board", "task_update", new() { ["taskId"] = "t", ["requestId"] = "t", ["expectedVersion"] = 0 }).Ok);
        Assert.True(Call(board, "board", "task_claim", new() { ["taskId"] = "t", ["requestId"] = "claim", ["expectedVersion"] = 1, ["sessionId"] = "s1" }).Ok);
        var action = new JsonObject { ["taskId"] = "t", ["actionId"] = "a", ["inputFingerprint"] = "sha", ["requestId"] = "a", ["sessionId"] = "s1", ["expectedVersion"] = 2 };
        Assert.True(Call(board, "board", "action_begin", action).Ok);
        action["inputFingerprint"] = "different"; action["requestId"] = "a2";
        Assert.Equal("ActionConflict", Call(board, "board", "action_begin", action).Code);
    }

    [Fact]
    public void Inbox_delivery_deduplicates_and_requires_both_identity_and_secret()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        Assert.True(Call(state, "session", "__open", new() { ["principalId"] = "alice", ["sessionToken"] = "secret" }).Ok);
        var message = new JsonObject { ["messageId"] = "m", ["text"] = "hello" };
        Assert.True(Call(state, "session", "__deliver", message).Ok);
        Assert.True(Call(state, "session", "__deliver", message).Ok);
        Assert.Equal("SessionDenied", Call(state, "session", "check_inbox", new() { ["sessionToken"] = "bad" }).Code);
        var inbox = Call(state, "session", "check_inbox", new() { ["sessionToken"] = "secret" });
        Assert.Single(Data(inbox)["items"]!.AsArray());
        Assert.Equal("SessionDenied", Call(state, "session", "check_inbox", new() { ["sessionToken"] = "secret" }, new("mallory", false, ["repo"], false)).Code);
    }

    [Fact]
    public void Resources_share_canonical_machine_alias_and_pause_is_durable()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        Assert.True(Call(state, "control", "control_request", new() { ["command"] = "register_resource", ["resourceId"] = "pc-a", ["machineId"] = "a", ["aliases"] = new JsonArray("vs", "roslyn") }).Ok);
        Assert.Equal("pc-a", Data(Call(state, "control", "__resolve", new() { ["resourceId"] = "vs", ["machineId"] = "a" }))["resourceId"]!.ToString());
        Assert.Equal("ResourceUnknown", Call(state, "control", "__resolve", new() { ["resourceId"] = "vs", ["machineId"] = "b" }).Code);
        Assert.True(Call(state, "control", "control_request", new() { ["command"] = "pause" }).Ok);
        Assert.True(Data(Call(state, "control", "get_status", new()))["paused"]!.GetValue<bool>());
    }

    [Fact]
    public void Expired_request_retry_never_returns_a_usable_old_grant()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        var args = new JsonObject { ["sessionId"] = "s1", ["requestId"] = "lease", ["leaseSeconds"] = 1 };
        Assert.True(Call(state, "lock", "claim_acquire", args).Ok);
        Assert.Equal("StaleClaim", Call(state, "lock", "claim_acquire", args, seconds: 2).Code);
    }

    [Fact]
    public void Terminal_outcome_preserves_cleanup_hold_and_separate_retries_have_attempt_numbers()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        Call(state, "board", "task_update", new() { ["taskId"] = "t", ["requestId"] = "create", ["expectedVersion"] = 0 });
        Call(state, "board", "task_claim", new() { ["taskId"] = "t", ["requestId"] = "claim", ["expectedVersion"] = 1, ["sessionId"] = "s" });
        var begin = new JsonObject { ["taskId"] = "t", ["actionId"] = "a", ["inputFingerprint"] = "f", ["sessionId"] = "s", ["requestId"] = "begin", ["expectedVersion"] = 2, ["requiresRestoration"] = true };
        Assert.True(Call(state, "board", "action_begin", begin).Ok);
        Assert.True(Call(state, "board", "action_report", new() { ["taskId"] = "t", ["actionId"] = "a", ["sessionId"] = "s", ["requestId"] = "report", ["expectedVersion"] = 3, ["attempt"] = 1, ["outcome"] = "failed", ["restoration"] = "blocked" }).Ok);
        Assert.Equal("Busy", Call(state, "board", "task_claim", new() { ["taskId"] = "t", ["sessionId"] = "replacement", ["requestId"] = "replacement", ["expectedVersion"] = 4 }, seconds: 500).Code);
        begin["requestId"] = "retry"; begin["expectedVersion"] = 4; begin["attempt"] = 2; begin["explicitRetry"] = true;
        Assert.Equal("RestorationRequired", Call(state, "board", "action_begin", begin).Code);
        Assert.True(Call(state, "board", "action_report", new() { ["taskId"] = "t", ["actionId"] = "a", ["sessionId"] = "s", ["requestId"] = "restore", ["expectedVersion"] = 4, ["attempt"] = 1, ["outcome"] = "failed", ["restoration"] = "verified" }).Ok);
        begin["expectedVersion"] = 5;
        var retry = Call(state, "board", "action_begin", begin);
        Assert.True(retry.Ok); Assert.Equal(2, Data(retry)["action"]!["attempt"]!.GetValue<int>());
    }

    [Fact]
    public void Retried_invocation_acknowledgment_never_authorizes_replaying_external_work()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        Call(state, "board", "task_update", new() { ["taskId"] = "t", ["requestId"] = "create", ["expectedVersion"] = 0 });
        Call(state, "board", "task_claim", new() { ["taskId"] = "t", ["requestId"] = "claim", ["expectedVersion"] = 1, ["sessionId"] = "s" });
        var begin = new JsonObject { ["taskId"] = "t", ["actionId"] = "a", ["inputFingerprint"] = "f", ["sessionId"] = "s", ["requestId"] = "begin", ["expectedVersion"] = 2 };
        Assert.True(Data(Call(state, "board", "action_begin", begin))["replayAllowed"]!.GetValue<bool>());
        Assert.False(Data(Call(state, "board", "action_begin", begin))["replayAllowed"]!.GetValue<bool>());
    }

    [Fact]
    public void Rejected_combined_update_never_leaks_earlier_changes_or_outbox_effects()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        Call(state, "board", "task_update", new() { ["taskId"] = "t", ["requestId"] = "create", ["expectedVersion"] = 0 });
        Call(state, "board", "task_claim", new() { ["taskId"] = "t", ["requestId"] = "claim", ["expectedVersion"] = 1, ["sessionId"] = "s", ["resourceIds"] = new JsonArray("resource") });
        Call(state, "board", "action_begin", new() { ["taskId"] = "t", ["actionId"] = "a", ["inputFingerprint"] = "f", ["sessionId"] = "s", ["requestId"] = "begin", ["expectedVersion"] = 2 });
        var before = state.Value.Json; var effectsBefore = state.Effects.Count;
        var update = Call(state, "board", "task_update", new() { ["taskId"] = "t", ["sessionId"] = "s", ["requestId"] = "batch", ["expectedVersion"] = 3, ["actionReports"] = new JsonArray(new JsonObject { ["actionId"] = "a", ["attempt"] = 1, ["outcome"] = "completed" }, new JsonObject { ["actionId"] = "missing", ["attempt"] = 1, ["outcome"] = "completed" }) });
        Assert.Equal("ActionUnknown", update.Code); Assert.Equal(before, state.Value.Json); Assert.Equal(effectsBefore, state.Effects.Count);
    }

    [Fact]
    public void Out_of_order_action_resolution_never_recreates_an_old_resource_hold()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        Call(state, "lock", "claim_acquire", new() { ["sessionId"] = "s", ["requestId"] = "claim" });
        var action = new JsonObject { ["actionId"] = "a", ["actionKey"] = "task/a/1", ["sessionId"] = "s" };
        Assert.True(Call(state, "lock", "__unblock", action).Ok);
        Assert.True(Call(state, "lock", "__block", action).Ok);
        Assert.True(Call(state, "lock", "claim_release", new() { ["sessionId"] = "s", ["requestId"] = "release", ["generation"] = 1 }).Ok);
    }

    [Fact]
    public void Task_claim_renewal_requires_current_owner_generation_and_survives_original_expiry()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        Call(state, "board", "task_update", new() { ["taskId"] = "t", ["requestId"] = "create", ["expectedVersion"] = 0 });
        Call(state, "board", "task_claim", new() { ["taskId"] = "t", ["requestId"] = "claim", ["expectedVersion"] = 1, ["sessionId"] = "s", ["leaseSeconds"] = 2 });
        Assert.True(Call(state, "board", "claim_renew", new() { ["taskId"] = "t", ["sessionId"] = "s", ["claimGeneration"] = 1, ["requestId"] = "renew", ["leaseSeconds"] = 100 }, seconds: 1).Ok);
        Assert.Equal("Busy", Call(state, "board", "task_claim", new() { ["taskId"] = "t", ["requestId"] = "other", ["expectedVersion"] = 2, ["sessionId"] = "other" }, seconds: 3).Code);
        Assert.Equal("StaleClaim", Call(state, "board", "claim_renew", new() { ["taskId"] = "t", ["sessionId"] = "s", ["claimGeneration"] = 0, ["requestId"] = "stale" }, seconds: 3).Code);
    }

    [Fact]
    public async Task Composed_claim_rolls_back_only_new_grants_and_preserves_existing_grants()
    {
        var factory = DispatchProxy.Create<IGrainFactory, TestGrainFactory>();
        var application = new RelayApplication(factory);
        async Task<RelayReply> Invoke(string tool, JsonObject args)
        {
            args["repositoryId"] = "repo";
            if (!args.ContainsKey("requestId")) args["requestId"] = Guid.NewGuid().ToString("N");
            return await application.ExecuteAsync(Caller with { IsInternal = false }, tool, JsonSerializer.SerializeToElement(args));
        }
        var one = Data(await Invoke("session_open", new())); var two = Data(await Invoke("session_open", new()));
        JsonObject For(JsonObject session) => new() { ["sessionId"] = session["sessionId"]!.DeepClone(), ["sessionToken"] = session["sessionToken"]!.DeepClone() };
        foreach (var resource in new[] { "a", "b", "c" }) Assert.True((await Invoke("control_request", new() { ["command"] = "register_resource", ["resourceId"] = resource, ["machineId"] = "pc" })).Ok);
        var a = For(one); a["resourceId"] = "a"; Assert.True((await Invoke("claim_acquire", a)).Ok);
        var c = For(two); c["resourceId"] = "c"; Assert.True((await Invoke("claim_acquire", c)).Ok);
        var create = For(one); create["taskId"] = "t"; create["expectedVersion"] = 0; Assert.True((await Invoke("task_update", create)).Ok);
        var claim = For(one); claim["taskId"] = "t"; claim["expectedVersion"] = 1; claim["resourceIds"] = new JsonArray("c", "b", "a");
        Assert.Equal("Busy", (await Invoke("task_claim", claim)).Code);
        var b = For(two); b["resourceId"] = "b"; Assert.True((await Invoke("claim_acquire", b)).Ok);
        a = For(two); a["resourceId"] = "a"; Assert.Equal("Busy", (await Invoke("claim_acquire", a)).Code);
        var task = Data(await Invoke("task_get", new() { ["taskId"] = "t" }));
        Assert.Null(task["owner"]);
        var bad = For(one); bad["sessionToken"] = "incorrect"; bad["taskId"] = "t"; bad["expectedVersion"] = 3;
        Assert.Equal("SessionDenied", (await Invoke("task_update", bad)).Code);
        Assert.Equal("UnknownTool", (await Invoke("__deliver", new())).Code);
    }

    private static StateEnvelope<CoordinationDocument> StartedReviewAction()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        Assert.True(Call(state, "board", "task_update", new() { ["taskId"] = "t", ["requestId"] = "create", ["expectedVersion"] = 0 }).Ok);
        Assert.True(Call(state, "board", "task_claim", new() { ["taskId"] = "t", ["sessionId"] = "s", ["requestId"] = "claim", ["expectedVersion"] = 1, ["resourceIds"] = new JsonArray("A") }).Ok);
        Assert.True(Call(state, "board", "action_begin", new() { ["taskId"] = "t", ["sessionId"] = "s", ["requestId"] = "begin", ["expectedVersion"] = 2, ["actionId"] = "a", ["inputFingerprint"] = "f", ["requiresRestoration"] = true }, Caller with { IsAdministrator = false }).Ok);
        return state;
    }

    private static JsonObject ReviewReport(long version, string restoration, int attempt = 1) => new() { ["taskId"] = "t", ["sessionId"] = "s", ["requestId"] = Guid.NewGuid().ToString("N"), ["expectedVersion"] = version, ["actionId"] = "a", ["attempt"] = attempt, ["outcome"] = "completed", ["restoration"] = restoration };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Review_late_attempt_evidence_never_resolves_current_attempt(bool embedded)
    {
        var state = StartedReviewAction();
        Assert.True(Call(state, "board", "action_report", ReviewReport(3, "verified")).Ok);
        Assert.True(Call(state, "board", "action_begin", new() { ["taskId"] = "t", ["sessionId"] = "s", ["requestId"] = "retry", ["expectedVersion"] = 4, ["actionId"] = "a", ["inputFingerprint"] = "f", ["attempt"] = 2, ["explicitRetry"] = true, ["requiresRestoration"] = true }).Ok);
        var effectCount = state.Effects.Count;
        var late = ReviewReport(5, "verified"); late["report"] = "late old evidence";
        var args = embedded ? new JsonObject { ["taskId"] = "t", ["sessionId"] = "s", ["requestId"] = "milestone", ["expectedVersion"] = 5, ["actionReports"] = new JsonArray(late) } : late;
        Assert.True(Call(state, "board", embedded ? "task_update" : "action_report", args).Ok);
        var task = Data(Call(state, "board", "task_get", new() { ["taskId"] = "t" }));
        Assert.Equal("invoking", task["actions"]!["a"]!["status"]!.ToString());
        Assert.Equal("pending", task["actions"]!["a"]!["restoration"]!.ToString());
        Assert.Equal(effectCount, state.Effects.Count);
        Assert.Equal(1, task["reports"]!.AsArray().Last()!["attempt"]!.GetValue<int>());
        Assert.Equal("late old evidence", task["reports"]!.AsArray().Last()!["report"]!.ToString());
    }

    [Fact]
    public void Review_required_restoration_cannot_be_downgraded_or_reopened()
    {
        var state = StartedReviewAction();
        Assert.Equal("RestorationRequired", Call(state, "board", "action_report", ReviewReport(3, "not_required"), Caller with { IsAdministrator = false }).Code);
        Assert.True(Call(state, "board", "action_report", ReviewReport(3, "verified")).Ok);
        Assert.Equal("RestorationResolved", Call(state, "board", "action_report", ReviewReport(4, "pending")).Code);
        var missing = ReviewReport(4, "verified"); missing.Remove("attempt");
        Assert.Equal("AttemptRequired", Call(state, "board", "action_report", missing).Code);
    }

    [Fact]
    public void Review_separate_admin_accepts_cleanup_without_forging_outcome()
    {
        var state = StartedReviewAction();
        var resource = new StateEnvelope<CoordinationDocument>();
        Assert.True(Call(resource, "lock", "claim_acquire", new() { ["sessionId"] = "s" }, Caller with { IsAdministrator = false }).Ok);
        Assert.True(Call(resource, "lock", "__block", JsonNode.Parse(state.Effects[0].PayloadJson)!.AsObject(), Caller with { IsAdministrator = false }).Ok);
        Assert.True(Call(state, "board", "action_report", ReviewReport(3, "blocked"), Caller with { IsAdministrator = false }).Ok);
        var acceptance = ReviewReport(4, "accepted"); acceptance["sessionId"] = "bob-session"; acceptance.Remove("outcome");
        var bob = Caller with { PrincipalId = "bob" };
        Assert.True(Call(state, "board", "action_report", acceptance, bob).Ok);
        var task = Data(Call(state, "board", "task_get", new() { ["taskId"] = "t" }));
        Assert.Equal("completed", task["actions"]!["a"]!["status"]!.ToString());
        Assert.Equal("accepted", task["actions"]!["a"]!["restoration"]!.ToString());
        Assert.Equal("alice", task["actions"]!["a"]!["principalId"]!.ToString());
        Assert.Equal("bob", task["reports"]!.AsArray().Last()!["author"]!.ToString());
        var unblock = state.Effects.Last(x => x.Operation == "__unblock");
        Assert.True(Call(resource, "lock", "__unblock", JsonNode.Parse(unblock.PayloadJson)!.AsObject(), bob).Ok);
        Assert.True(Call(resource, "lock", "claim_acquire", new() { ["sessionId"] = "replacement" }, seconds: 500).Ok);
        var forgery = ReviewReport(5, "accepted"); forgery["sessionId"] = "bob-session"; forgery["outcome"] = "failed";
        Assert.Equal("ActionDenied", Call(state, "board", "action_report", forgery, bob).Code);
    }

    [Fact]
    public async Task Review_existing_task_claim_rejects_changed_resources_before_acquiring()
    {
        var application = new RelayApplication(DispatchProxy.Create<IGrainFactory, TestGrainFactory>());
        async Task<RelayReply> Invoke(string tool, JsonObject args) { args["repositoryId"] = "repo"; args["requestId"] = Guid.NewGuid().ToString("N"); return await application.ExecuteAsync(Caller with { IsInternal = false }, tool, JsonSerializer.SerializeToElement(args)); }
        var one = Data(await Invoke("session_open", new())); var two = Data(await Invoke("session_open", new()));
        JsonObject For(JsonObject s) => new() { ["sessionId"] = s["sessionId"]!.DeepClone(), ["sessionToken"] = s["sessionToken"]!.DeepClone() };
        foreach (var resource in new[] { "A", "B" }) Assert.True((await Invoke("control_request", new() { ["command"] = "register_resource", ["resourceId"] = resource, ["machineId"] = "pc" })).Ok);
        var create = For(one); create["taskId"] = "t"; create["expectedVersion"] = 0; Assert.True((await Invoke("task_update", create)).Ok);
        var claim = For(one); claim["taskId"] = "t"; claim["expectedVersion"] = 1; claim["resourceIds"] = new JsonArray("A"); Assert.True((await Invoke("task_claim", claim)).Ok);
        claim["expectedVersion"] = 2; claim["resourceIds"] = new JsonArray("A", "B"); Assert.Equal("ResourceSetConflict", (await Invoke("task_claim", claim)).Code);
        var acquire = For(two); acquire["resourceId"] = "B"; Assert.True((await Invoke("claim_acquire", acquire)).Ok);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_read_conveniences_require_matching_private_session(bool supplyStolenToken)
    {
        var application = new RelayApplication(DispatchProxy.Create<IGrainFactory, TestGrainFactory>());
        async Task<RelayReply> Invoke(RelayCaller caller, string tool, JsonObject args) { args["repositoryId"] = "repo"; args["requestId"] = Guid.NewGuid().ToString("N"); return await application.ExecuteAsync(caller with { IsInternal = false }, tool, JsonSerializer.SerializeToElement(args)); }
        var session = Data(await Invoke(Caller, "session_open", new()));
        Assert.True((await Invoke(Caller, "control_request", new() { ["command"] = "register_resource", ["resourceId"] = "A", ["machineId"] = "pc" })).Ok);
        var acquire = new JsonObject { ["sessionId"] = session["sessionId"]!.DeepClone(), ["sessionToken"] = session["sessionToken"]!.DeepClone(), ["resourceId"] = "A" };
        var grant = Data(await Invoke(Caller, "claim_acquire", acquire));
        var read = new JsonObject { ["sessionId"] = session["sessionId"]!.DeepClone(), ["renewClaims"] = new JsonArray(new JsonObject { ["resourceId"] = "A", ["generation"] = grant["generation"]!.DeepClone() }), ["ackMessageIds"] = new JsonArray("unknown") };
        if (supplyStolenToken) read["sessionToken"] = session["sessionToken"]!.DeepClone();
        var result = await Invoke(Caller with { PrincipalId = "bob", IsAdministrator = false }, "task_get", read);
        Assert.True(result.Ok);
        Assert.Equal("SessionDenied", Data(result)["conveniences"]!["renewals"]![0]!["code"]!.ToString());
        Assert.Equal("SessionDenied", Data(result)["conveniences"]!["ack"]!["code"]!.ToString());
    }

    [Fact]
    public async Task Review_reply_bounds_measure_full_escaped_envelope_and_preserve_committed_mutation()
    {
        var application = new RelayApplication(DispatchProxy.Create<IGrainFactory, TestGrainFactory>());
        async Task<RelayReply> Invoke(string tool, JsonObject args) { args["repositoryId"] = "repo"; args["requestId"] = Guid.NewGuid().ToString("N"); return await application.ExecuteAsync(Caller with { IsInternal = false }, tool, JsonSerializer.SerializeToElement(args)); }
        var session = Data(await Invoke("session_open", new()));
        JsonObject For() => new() { ["sessionId"] = session["sessionId"]!.DeepClone(), ["sessionToken"] = session["sessionToken"]!.DeepClone(), ["taskId"] = "t" };
        for (var i = 0; i < 2; i++)
        {
            var patch = For(); patch["expectedVersion"] = i; patch["patch"] = new JsonObject { ["fact" + i] = new string('"', 2400) };
            var reply = await Invoke("task_update", patch); Assert.True(reply.Ok); Assert.Equal(i + 1, reply.Version);
            Assert.True(JsonSerializer.SerializeToUtf8Bytes(reply).Length <= RelayLimits.MaxPayloadBytes, "Full escaped reply exceeded wire bound");
        }
        var read = await Invoke("task_get", new() { ["taskId"] = "t" }); Assert.True(read.Ok); Assert.Equal(2, read.Version);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(read).Length <= RelayLimits.MaxPayloadBytes);
        Assert.True(Data(read)["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Review_throwing_effect_delivery_persists_backoff_without_acknowledgment()
    {
        var persistent = DispatchProxy.Create<Orleans.Runtime.IPersistentState<StateEnvelope<CoordinationDocument>>, ReviewPersistentState>();
        var storage = (ReviewPersistentState)(object)persistent;
        var due = DateTimeOffset.UtcNow.AddMinutes(-1);
        storage.Value.Effects.Add(new("failed-effect", "session", "recipient", "__deliver", "{}", due));
        var factory = DispatchProxy.Create<IGrainFactory, ThrowingGrainFactory>();
        var grain = new SessionGrain(persistent, factory, new EmptyServices());
        var before = DateTimeOffset.UtcNow;
        var result = await grain.ExecuteAsync(Caller, "__dispatch", "{}");
        Assert.Equal("DeliveryPending", result.Code);
        Assert.Equal(1, storage.Writes);
        var effect = Assert.Single(storage.Value.Effects);
        Assert.Equal("failed-effect", effect.Id);
        Assert.True(effect.DueAt >= before.AddSeconds(5));
        Assert.Equal(effect.DueAt, JsonSerializer.Deserialize<StateEnvelope<CoordinationDocument>>(storage.Saved!)!.Effects[0].DueAt);
        Assert.Equal("DeliveryPending", (await grain.ExecuteAsync(Caller, "__dispatch", "{}")).Code);
        Assert.Equal(1, ((ThrowingGrainFactory)(object)factory).Attempts);
    }

    [Theory]
    [InlineData("started")]
    [InlineData("unknown")]
    [InlineData("incomplete")]
    public void Review_original_writer_can_finish_after_admin_accepts_nonterminal_cleanup(string outcome)
    {
        var state = StartedReviewAction();
        var alice = Caller with { IsAdministrator = false }; var bob = Caller with { PrincipalId = "bob" };
        var resource = new StateEnvelope<CoordinationDocument>();
        Assert.True(Call(resource, "lock", "claim_acquire", new() { ["sessionId"] = "s" }, alice).Ok);
        Assert.True(Call(resource, "lock", "__block", JsonNode.Parse(state.Effects[0].PayloadJson)!.AsObject(), alice).Ok);
        var initial = ReviewReport(3, "blocked"); initial["outcome"] = outcome;
        Assert.True(Call(state, "board", "action_report", initial, alice).Ok);
        Assert.Equal("AdministratorRequired", Call(state, "board", "action_report", ReviewReport(4, "accepted"), alice).Code);
        var acceptance = ReviewReport(4, "accepted"); acceptance["sessionId"] = "bob-session"; acceptance.Remove("outcome");
        Assert.True(Call(state, "board", "action_report", acceptance, bob).Ok);
        Assert.DoesNotContain(state.Effects, x => x.Operation == "__unblock");
        var spoof = ReviewReport(5, "accepted"); spoof["sessionId"] = "bob-session";
        Assert.Equal("ActionDenied", Call(state, "board", "action_report", spoof, bob).Code);
        var completed = ReviewReport(5, "accepted"); completed.Remove("restoration");
        var result = Call(state, "board", "action_report", completed, alice);
        Assert.True(result.Ok, result.Code);
        var action = Data(result)["actions"]!["a"]!;
        Assert.Equal("completed", action["status"]!.ToString());
        Assert.Equal("accepted", action["restoration"]!.ToString());
        Assert.Equal("alice", action["principalId"]!.ToString());
        Assert.Equal("s", action["sessionId"]!.ToString());
        var unblock = Assert.Single(state.Effects, x => x.Operation == "__unblock");
        Assert.True(Call(resource, "lock", "__unblock", JsonNode.Parse(unblock.PayloadJson)!.AsObject(), alice).Ok);
        Assert.True(Call(resource, "lock", "claim_acquire", new() { ["sessionId"] = "replacement" }, seconds: 500).Ok);
    }

    [Fact]
    public async Task Review_slow_delivery_failure_gets_full_backoff_after_failure()
    {
        var persistent = DispatchProxy.Create<Orleans.Runtime.IPersistentState<StateEnvelope<CoordinationDocument>>, ReviewPersistentState>();
        var storage = (ReviewPersistentState)(object)persistent;
        storage.Value.Effects.Add(new("slow-effect", "session", "recipient", "__deliver", "{}", DateTimeOffset.UtcNow.AddMinutes(-1)));
        var factory = DispatchProxy.Create<IGrainFactory, ThrowingGrainFactory>();
        var delivery = (ThrowingGrainFactory)(object)factory; delivery.DelayMilliseconds = 50;
        var grain = new SessionGrain(persistent, factory, new EmptyServices());
        Assert.Equal("DeliveryPending", (await grain.ExecuteAsync(Caller, "__dispatch", "{}")).Code);
        var effect = Assert.Single(storage.Value.Effects);
        Assert.True(effect.DueAt >= delivery.FailedAt.AddSeconds(5), "Backoff began before the slow recipient failure");
        Assert.Equal(effect.DueAt, JsonSerializer.Deserialize<StateEnvelope<CoordinationDocument>>(storage.Saved!)!.Effects[0].DueAt);
    }

    public class ReviewPersistentState : DispatchProxy
    {
        public StateEnvelope<CoordinationDocument> Value = new();
        public int Writes;
        public string? Saved;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "get_State") return Value;
            if (method.Name == "set_State") { Value = (StateEnvelope<CoordinationDocument>)args![0]!; return null; }
            if (method.Name == "WriteStateAsync") { Writes++; Saved = JsonSerializer.Serialize(Value); return Task.CompletedTask; }
            throw new NotSupportedException(method.Name);
        }
    }

    public class ThrowingGrainFactory : DispatchProxy
    {
        public int Attempts;
        public int DelayMilliseconds;
        public DateTimeOffset FailedAt;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => new ThrowingAggregate(this);
        private sealed class ThrowingAggregate(ThrowingGrainFactory owner) : ISessionGrain
        {
            public async Task<RelayReply> ExecuteAsync(RelayCaller caller, string operation, string argumentsJson)
            {
                owner.Attempts++;
                if (owner.DelayMilliseconds > 0) await Task.Delay(owner.DelayMilliseconds);
                owner.FailedAt = DateTimeOffset.UtcNow;
                throw new IOException("recipient unavailable");
            }
        }
    }

    private sealed class EmptyServices : IServiceProvider { public object? GetService(Type serviceType) => null; }

    [Fact]
    public async Task Review_oversized_grant_metadata_is_rejected_before_task_claim_commits()
    {
        var application = new RelayApplication(DispatchProxy.Create<IGrainFactory, TestGrainFactory>());
        async Task<RelayReply> Invoke(string tool, JsonObject args) { args["repositoryId"] = "repo"; args["requestId"] = Guid.NewGuid().ToString("N"); return await application.ExecuteAsync(Caller with { IsInternal = false }, tool, JsonSerializer.SerializeToElement(args)); }
        var session = Data(await Invoke("session_open", new()));
        JsonObject For() => new() { ["sessionId"] = session["sessionId"]!.DeepClone(), ["sessionToken"] = session["sessionToken"]!.DeepClone(), ["taskId"] = "t" };
        var resources = new JsonArray();
        for (var i = 0; i < 16; i++)
        {
            var id = new string('"', 230) + i;
            resources.Add(id);
            Assert.True((await Invoke("control_request", new() { ["command"] = "register_resource", ["resourceId"] = id, ["machineId"] = "pc" })).Ok);
        }
        var create = For(); create["expectedVersion"] = 0; Assert.True((await Invoke("task_update", create)).Ok);
        var claim = For(); claim["expectedVersion"] = 1; claim["resourceIds"] = resources;
        var result = await Invoke("task_claim", claim);
        Assert.Equal("PayloadTooLarge", result.Code);
        var task = Data(await Invoke("task_get", new() { ["taskId"] = "t" }));
        Assert.Null(task["owner"]);
    }

    [Fact]
    public void Review_bounded_mutation_retains_action_outcome_attempt_and_blocker_identity()
    {
        var state = StartedReviewAction();
        for (var i = 0; i < 2; i++) Assert.True(Call(state, "board", "task_update", new() { ["taskId"] = "t", ["sessionId"] = "s", ["requestId"] = "fact" + i, ["expectedVersion"] = 3 + i, ["patch"] = new JsonObject { ["fact" + i] = new string('"', 2400) } }).Ok);
        var reply = Call(state, "board", "action_report", ReviewReport(5, "verified"));
        Assert.True(reply.Ok);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(reply).Length <= RelayLimits.MaxPayloadBytes);
        var action = Data(reply)["actions"]!["a"]!;
        Assert.Equal("completed", action["status"]!.ToString());
        Assert.Equal("verified", action["restoration"]!.ToString());
        Assert.Equal(1, action["attempt"]!.GetValue<int>());
        Assert.Equal("A", action["resourceIds"]![0]!.ToString());
        var retry = Call(state, "board", "action_begin", new() { ["taskId"] = "t", ["sessionId"] = "s", ["requestId"] = "begin-again", ["expectedVersion"] = 6, ["actionId"] = "a", ["inputFingerprint"] = "f" });
        Assert.True(retry.Ok);
        Assert.False(Data(retry)["replayAllowed"]!.GetValue<bool>());
        Assert.Equal("completed", Data(retry)["action"]!["status"]!.ToString());
    }

    [Fact]
    public async Task Review_admin_acceptance_uses_admins_own_authenticated_session()
    {
        var application = new RelayApplication(DispatchProxy.Create<IGrainFactory, TestGrainFactory>());
        var alice = Caller with { IsAdministrator = false, IsInternal = false }; var bob = Caller with { PrincipalId = "bob", IsInternal = false };
        async Task<RelayReply> Invoke(RelayCaller caller, string tool, JsonObject args) { args["repositoryId"] = "repo"; args["requestId"] = Guid.NewGuid().ToString("N"); return await application.ExecuteAsync(caller, tool, JsonSerializer.SerializeToElement(args)); }
        var worker = Data(await Invoke(alice, "session_open", new())); var admin = Data(await Invoke(bob, "session_open", new()));
        JsonObject For(JsonObject s) => new() { ["sessionId"] = s["sessionId"]!.DeepClone(), ["sessionToken"] = s["sessionToken"]!.DeepClone(), ["taskId"] = "t" };
        var create = For(worker); create["expectedVersion"] = 0; Assert.True((await Invoke(alice, "task_update", create)).Ok);
        var claim = For(worker); claim["expectedVersion"] = 1; Assert.True((await Invoke(alice, "task_claim", claim)).Ok);
        var begin = For(worker); begin["expectedVersion"] = 2; begin["actionId"] = "a"; begin["inputFingerprint"] = "f"; begin["requiresRestoration"] = true;
        Assert.Equal("ActionPending", (await Invoke(alice, "action_begin", begin)).Code); // This fake routes no dispatch; reservation is durable.
        var report = For(worker); report["expectedVersion"] = 3; report["actionId"] = "a"; report["attempt"] = 1; report["outcome"] = "completed"; report["restoration"] = "blocked";
        Assert.True((await Invoke(alice, "action_report", report)).Ok);
        var acceptance = For(admin); acceptance["expectedVersion"] = 4; acceptance["actionId"] = "a"; acceptance["attempt"] = 1; acceptance["restoration"] = "accepted";
        Assert.True((await Invoke(bob, "action_report", acceptance)).Ok);
        var spoof = For(worker); spoof["expectedVersion"] = 5; spoof["actionId"] = "a"; spoof["attempt"] = 1; spoof["restoration"] = "accepted";
        Assert.Equal("SessionDenied", (await Invoke(bob, "action_report", spoof)).Code);
    }

    private static JsonObject DependentWorkflow(string state, params string[] dependsOn)
    {
        var workflow = new JsonObject
        {
            ["state"] = state,
            ["packet"] = new JsonObject { ["goal"] = "g", ["target"] = "t", ["authorityRef"] = "human-grant:1", ["restoration"] = "r", ["stopCondition"] = "s" },
            ["criteria"] = new JsonArray(new JsonObject { ["id"] = "c", ["description"] = "d", ["result"] = state == "completed" ? "passed" : "not_run", ["evidenceRef"] = state == "completed" ? "artifact:1" : null }),
            ["blockers"] = new JsonArray()
        };
        if (dependsOn.Length > 0) workflow["dependsOn"] = new JsonArray(dependsOn.Select(id => (JsonNode)id).ToArray());
        return workflow;
    }

    private static RelayReply WriteWorkflow(StateEnvelope<CoordinationDocument> state, string taskId, long version, JsonObject workflow, string? target = null)
    {
        if (target is not null) workflow["targetSessionId"] = target;
        return Call(state, "board", "task_update", new() { ["taskId"] = taskId, ["requestId"] = Guid.NewGuid().ToString("N"), ["expectedVersion"] = version, ["workflow"] = workflow });
    }

    [Fact]
    public void Dependencies_refuse_cycles_and_unknown_tasks_gate_offers_and_report_what_completion_unblocks()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        Assert.Equal("DependencyUnknown", WriteWorkflow(state, "b", 0, DependentWorkflow("ready", "a")).Code);
        Assert.True(WriteWorkflow(state, "a", 0, DependentWorkflow("ready")).Ok);
        Assert.Equal("DependencyCycle", WriteWorkflow(state, "self", 0, DependentWorkflow("ready", "self")).Code);
        Assert.True(WriteWorkflow(state, "b", 0, DependentWorkflow("ready", "a"), "worker-b").Ok);
        Assert.True(WriteWorkflow(state, "c", 0, DependentWorkflow("ready", "a", "b"), "worker-c").Ok);
        Assert.True(WriteWorkflow(state, "d", 0, DependentWorkflow("ready", "a"), "worker-d").Ok);
        // a <- b <- c: making a depend on c closes a cycle anywhere on the board.
        Assert.Equal("DependencyCycle", WriteWorkflow(state, "a", 1, DependentWorkflow("ready", "c")).Code);
        Assert.Equal("InvalidWorkflow", WriteWorkflow(state, "e", 0, DependentWorkflow("ready", "a", "a")).Code);
        Assert.Equal("InvalidWorkflow", WriteWorkflow(state, "e", 0, DependentWorkflow("ready", Enumerable.Range(0, RelayLimits.MaxDependencies + 1).Select(i => "x" + i).ToArray())).Code);

        string? Offered(string session) => Data(Call(state, "board", "__next_work", new() { ["sessionId"] = session }))["offer"]?["taskId"]?.GetValue<string>();
        Assert.Equal("a", Offered("worker-b"));
        Assert.Equal("a", Offered("worker-d"));

        var completed = WriteWorkflow(state, "a", 1, DependentWorkflow("completed"));
        Assert.True(completed.Ok, completed.Code);
        // Only tasks whose every dependency is now completed are unblocked; c still waits on b.
        Assert.Equal(["b", "d"], Data(completed)["unblocked"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Empty(state.Effects);
        Assert.Equal("b", Offered("worker-b"));
        Assert.Equal("d", Offered("worker-d"));
        Assert.Null(Offered("worker-c"));
        // Completing again unblocks nothing new.
        Assert.Null(Data(WriteWorkflow(state, "a", 2, DependentWorkflow("completed")))["unblocked"]);
        Assert.Equal(["c"], Data(WriteWorkflow(state, "b", 1, DependentWorkflow("completed", "a")))["unblocked"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal("c", Offered("worker-c"));
    }

    [Fact]
    public void Legacy_dependsOn_of_another_shape_is_skipped_and_never_breaks_writes_or_completion()
    {
        // Boards always accepted unknown workflow fields, so a stored dependsOn may hold any JSON.
        var odd = new JsonNode?[] { JsonValue.Create("a"), new JsonArray(1, 2), new JsonArray(new JsonObject { ["id"] = "a" }), new JsonArray("a", null) };
        var tasks = new JsonObject();
        for (var i = 0; i < odd.Length; i++)
        {
            var workflow = DependentWorkflow("ready"); workflow["dependsOn"] = odd[i]; workflow["packet"]!["resources"] = odd[i]?.DeepClone();
            tasks["legacy" + i] = new JsonObject { ["taskId"] = "legacy" + i, ["version"] = 1, ["changeRevision"] = i + 1, ["actions"] = new JsonObject(), ["workflow"] = workflow };
        }
        var state = new StateEnvelope<CoordinationDocument> { Value = new() { Json = new JsonObject { ["repositoryId"] = "repo", ["taskRevision"] = odd.Length, ["tasks"] = tasks }.ToJsonString() } };
        Assert.True(WriteWorkflow(state, "a", 0, DependentWorkflow("ready")).Ok);
        // A new dependency walks through the legacy tasks without failing.
        Assert.True(WriteWorkflow(state, "b", 0, DependentWorkflow("ready", "a", "legacy0", "legacy1", "legacy2", "legacy3")).Ok);
        Assert.True(WriteWorkflow(state, "c", 0, DependentWorkflow("ready", "a")).Ok);
        var completed = WriteWorkflow(state, "a", 1, DependentWorkflow("completed"));
        Assert.True(completed.Ok, completed.Code);
        Assert.Equal(["c"], Data(completed)["unblocked"]!.AsArray().Select(x => x!.GetValue<string>()));
        // The legacy tasks fail validation and stay out of offers; b waits on them.
        var next = Data(Call(state, "board", "__next_work", new() { ["sessionId"] = "worker", ["limit"] = RelayLimits.NextWorkCandidates, ["busyResources"] = new JsonArray("a") }));
        Assert.Equal("c", next["offer"]!["taskId"]!.GetValue<string>());
        Assert.Single(next["candidates"]!.AsArray());
    }

    public class TestGrainFactory : DispatchProxy
    {
        private readonly Dictionary<string, TestAggregate> aggregates = [];
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != "GetGrain" || !targetMethod.IsGenericMethod) throw new NotSupportedException();
            var type = targetMethod.GetGenericArguments()[0];
            var kind = type == typeof(ISessionGrain) ? "session" : type == typeof(ITaskBoardGrain) ? "board" : type == typeof(ILockGrain) ? "lock" : "control";
            var key = args![0]!.ToString()!; var id = kind + ":" + key;
            if (!aggregates.TryGetValue(id, out var aggregate)) aggregates[id] = aggregate = new(kind, key);
            return aggregate;
        }
    }
    private sealed class TestAggregate(string kind, string key) : ISessionGrain, ITaskBoardGrain, ILockGrain, IControlGrain
    {
        private readonly StateEnvelope<CoordinationDocument> state = new();
        public Task<RelayReply> ExecuteAsync(RelayCaller caller, string operation, string argumentsJson) => Task.FromResult(CoordinationStateMachine.Apply(state, kind, key, caller, operation, JsonNode.Parse(argumentsJson)!.AsObject(), DateTimeOffset.UtcNow));
    }
}
