using System.Text.Json.Nodes;
using System.Text.Json;
using System.Reflection;
using Orleans;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Contracts;
using Xunit;

namespace CoCern.Tests;

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
    public void Dependencies_refuse_cycles_and_unknown_tasks_gate_offers_and_completion_raises_next_work_to_each_unblocked_target()
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
        // Completion raises next_work to each unblocked task's target, in the same commit, with the completing change's revision.
        var raises = state.Effects.Where(effect => effect.Operation == "__attention_raise").ToArray();
        Assert.Equal(["worker-b", "worker-d"], raises.Select(effect => effect.TargetKey).Order());
        Assert.All(raises, effect =>
        {
            var item = JsonNode.Parse(effect.PayloadJson)!.AsObject();
            Assert.Equal(("session", "next_work", "repo"), (effect.TargetKind, item["kind"]!.GetValue<string>(), item["repositoryId"]!.GetValue<string>()));
            Assert.Equal(effect.TargetKey == "worker-b" ? "b" : "d", item["taskId"]!.GetValue<string>());
            Assert.Equal(["a"], item["refs"]!.AsArray().Select(x => x!.GetValue<string>()));
            Assert.Equal(Data(completed)["changeRevision"]!.GetValue<long>(), item["revision"]!.GetValue<long>());
        });
        // The task still waiting on b gets nothing yet.
        Assert.DoesNotContain(state.Effects, effect => effect.TargetKey == "worker-c");
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

    private static StateEnvelope<CoordinationDocument> AttentionSession()
    {
        var session = new StateEnvelope<CoordinationDocument>();
        Assert.True(Call(session, "session", "__open", new() { ["principalId"] = "alice", ["sessionToken"] = "secret", ["sessionId"] = "s1" }).Ok);
        return session;
    }

    private static JsonObject AttentionItems(StateEnvelope<CoordinationDocument> session) => JsonNode.Parse(session.Value.Json)!["attention"]?.AsObject() ?? new();

    private static RelayReply RaiseAttention(StateEnvelope<CoordinationDocument> session, string kind, string taskId, JsonObject? extra = null, int seconds = 0)
    {
        var args = extra ?? new JsonObject();
        args["kind"] = kind; args["taskId"] = taskId;
        return Call(session, "session", "__attention_raise", args, seconds: seconds);
    }

    [Fact]
    public void Attention_raise_is_deduplicated_per_task_and_kind_and_closing_ends_its_items()
    {
        var session = AttentionSession();
        var first = Data(RaiseAttention(session, "blocked", "T-1", new() { ["note"] = "waiting on schema review", ["blocker"] = "prerequisite" }));
        var again = Data(RaiseAttention(session, "blocked", "T-1", new() { ["note"] = "still waiting" }, seconds: 60));
        Assert.Equal(first["itemId"]!.GetValue<string>(), again["itemId"]!.GetValue<string>());
        Assert.True(again["refreshed"]!.GetValue<bool>());
        var items = AttentionItems(session);
        var item = Assert.Single(items).Value!.AsObject();
        Assert.Equal(("still waiting", Now.ToString("O")), (item["note"]!.GetValue<string>(), item["createdAt"]!.GetValue<string>()));
        Assert.False(item.ContainsKey("blocker"));
        // Another kind, the same task on another board, and another task are separate items.
        Assert.True(RaiseAttention(session, "review_ready", "T-1").Ok);
        Assert.True(RaiseAttention(session, "blocked", "T-1", new() { ["boardId"] = "other" }).Ok);
        Assert.True(RaiseAttention(session, "blocked", "T-2").Ok);
        Assert.Equal(4, AttentionItems(session).Count);

        // The blocker is cleared: only that task's blocked item closes. Then the task closes: all of its items on that board close.
        Assert.Equal(1, Data(Call(session, "session", "__attention_close", new() { ["taskId"] = "T-1", ["kinds"] = new JsonArray("blocked") }))["closed"]!.GetValue<int>());
        Assert.Equal(1, Data(Call(session, "session", "__attention_close", new() { ["taskId"] = "T-1" }))["closed"]!.GetValue<int>());
        Assert.Equal(new[] { "other/T-1", "default/T-2" }, AttentionScheduler.Items(AttentionItems(session)).Select(x => x.BoardId + "/" + x.TaskId));
        Assert.Equal(RelayErrorCodes.InvalidArguments, Call(session, "session", "__attention_close", new()).Code);

        // A close at a source revision keeps a later-arriving older raise from reopening the item; a newer raise opens a new one.
        Assert.True(Call(session, "session", "__attention_close", new() { ["taskId"] = "T-3", ["kinds"] = new JsonArray("failed"), ["revision"] = 5 }).Ok);
        Assert.Equal("stale", Data(RaiseAttention(session, "failed", "T-3", new() { ["revision"] = 4 }))["reason"]!.GetValue<string>());
        var reopened = Data(RaiseAttention(session, "failed", "T-3", new() { ["revision"] = 6 }));
        Assert.True(reopened["raised"]!.GetValue<bool>());
        Assert.Equal("stale", Data(RaiseAttention(session, "failed", "T-3", new() { ["revision"] = 5 }))["reason"]!.GetValue<string>());

        // An unopened session refuses items; nothing is stored for it.
        var unknown = new StateEnvelope<CoordinationDocument>();
        Assert.Equal(RelayErrorCodes.SessionUnknown, RaiseAttention(unknown, "info", "T-1").Code);
        Assert.Equal("{}", unknown.Value.Json);
        // Attention state is private: a binding registration never copies it into the control directory.
        var bind = Call(session, "session", "session_bind", new() { ["sessionToken"] = "secret", ["expectedVersion"] = 0, ["binding"] = new JsonObject(), ["requestId"] = "bind" });
        Assert.True(bind.Ok, bind.Code);
        Assert.DoesNotContain("attention", session.Effects.Single(effect => effect.Operation == "__register").PayloadJson);
    }

    [Fact]
    public void A_raise_delivered_again_without_a_revision_cannot_reopen_a_just_closed_item()
    {
        var session = AttentionSession();
        var raise = new JsonObject { ["note"] = "tests failed", ["refs"] = new JsonArray("T-9") };
        var id = Data(RaiseAttention(session, "failed", "T-1", raise.DeepClone().AsObject()))["itemId"]!.GetValue<string>();
        Assert.True(Call(session, "session", "attention_respond", new() { ["sessionToken"] = "secret", ["itemId"] = id, ["action"] = "dismiss", ["requestId"] = "d" }).Ok);
        // The same outbox raise delivered again is ignored for ten minutes after the close.
        Assert.Equal("recentlyClosed", Data(RaiseAttention(session, "failed", "T-1", raise.DeepClone().AsObject(), seconds: 60))["reason"]!.GetValue<string>());
        Assert.Empty(AttentionItems(session));
        // A raise with new content, or with a revision, is a new event and opens an item.
        var changed = Data(RaiseAttention(session, "failed", "T-1", new() { ["note"] = "tests failed again" }, seconds: 120));
        Assert.True(changed["raised"]!.GetValue<bool>());
        Assert.True(Call(session, "session", "__attention_close", new() { ["taskId"] = "T-1" }, seconds: 130).Ok);
        Assert.Equal("recentlyClosed", Data(RaiseAttention(session, "failed", "T-1", new() { ["note"] = "tests failed again" }, seconds: 140))["reason"]!.GetValue<string>());
        var withRevision = raise.DeepClone().AsObject(); withRevision["revision"] = 3;
        Assert.True(Data(RaiseAttention(session, "failed", "T-1", withRevision, seconds: 150))["raised"]!.GetValue<bool>());
        Assert.True(Call(session, "session", "__attention_close", new() { ["taskId"] = "T-1" }, seconds: 160).Ok);
        // After the window the same raise opens an item again.
        Assert.True(Data(RaiseAttention(session, "failed", "T-1", new() { ["note"] = "tests failed again" }, seconds: 140 + RelayLimits.AttentionTombstoneMinutes * 60))["raised"]!.GetValue<bool>());
        // The remembered closes are capped.
        for (var index = 0; index < RelayLimits.MaxAttentionTombstones + 5; index++)
        {
            Assert.True(RaiseAttention(session, "info", "C-" + index, seconds: 2_000).Ok);
            Assert.True(Call(session, "session", "__attention_close", new() { ["taskId"] = "C-" + index }, seconds: 2_000).Ok);
        }
        Assert.Equal(RelayLimits.MaxAttentionTombstones, JsonNode.Parse(session.Value.Json)!["attentionRecent"]!.AsObject().Count);
        Assert.True(Data(RaiseAttention(session, "info", "C-0", seconds: 2_001))["raised"]!.GetValue<bool>());
    }

    [Fact]
    public void Expired_attention_items_vanish()
    {
        var session = AttentionSession();
        var id = Data(RaiseAttention(session, "next_work", "T-1", new() { ["expiryDays"] = 1 }))["itemId"]!.GetValue<string>();
        var kept = Data(RaiseAttention(session, "info", "T-2"))["itemId"]!.GetValue<string>();
        Assert.Equal(Now.AddDays(7).ToString("O"), AttentionItems(session)[kept]!["expiresAt"]!.GetValue<string>());
        var day = (int)TimeSpan.FromDays(1).TotalSeconds;
        Assert.Equal(RelayErrorCodes.NotFound, Call(session, "session", "attention_respond", new() { ["sessionToken"] = "secret", ["itemId"] = id, ["action"] = "accept", ["requestId"] = "r" }, seconds: day).Code);
        var offer = Data(Call(session, "session", "__attention_offer", new() { ["sessionToken"] = "secret" }, seconds: day));
        Assert.Equal(kept, offer["attention"]!["id"]!.GetValue<string>());
        Assert.False(AttentionItems(session).ContainsKey(id));
        // A refresh extends the safety net; an expired item cannot be picked even before anything removes it.
        RaiseAttention(session, "info", "T-2", seconds: 6 * day);
        Assert.Equal(Now.AddDays(13).ToString("O"), AttentionItems(session)[kept]!["expiresAt"]!.GetValue<string>());
        Assert.Null(AttentionScheduler.Pick(AttentionItems(session), Now.AddDays(13)));
    }

    [Fact]
    public void A_full_attention_queue_drops_the_lowest_tier_oldest_item()
    {
        var session = AttentionSession();
        // 10 low-tier items first (the oldest is a-1), then 40 more urgent ones.
        for (var index = 1; index <= 10; index++) Assert.True(RaiseAttention(session, "next_work", "N-" + index, seconds: index).Ok);
        for (var index = 1; index <= 40; index++) Assert.True(RaiseAttention(session, "review_ready", "R-" + index, seconds: 100 + index).Ok);
        Assert.Equal(RelayLimits.MaxAttentionItems, AttentionItems(session).Count);
        var full = Data(RaiseAttention(session, "failed", "F-1", seconds: 500));
        Assert.Equal("a-1", full["dropped"]!.GetValue<string>());
        Assert.Equal(RelayLimits.MaxAttentionItems, AttentionItems(session).Count);
        Assert.Equal("N-2", AttentionScheduler.DropCandidate(AttentionScheduler.Items(AttentionItems(session)))!.TaskId);
        // A refresh never drops anything.
        Assert.True(Data(RaiseAttention(session, "review_ready", "R-1", seconds: 501))["refreshed"]!.GetValue<bool>());
        // Once only urgent items remain, a new lowest-tier item is the one dropped.
        for (var index = 2; index <= 10; index++) Assert.True(RaiseAttention(session, "blocked", "B-" + index, seconds: 600 + index).Ok);
        Assert.DoesNotContain(AttentionScheduler.Items(AttentionItems(session)), item => item.Kind == "next_work");
        var refused = Data(RaiseAttention(session, "info", "I-1", seconds: 700));
        Assert.Equal(("full", false), (refused["reason"]!.GetValue<string>(), refused["raised"]!.GetValue<bool>()));
        Assert.DoesNotContain(AttentionScheduler.Items(AttentionItems(session)), item => item.TaskId == "I-1");
    }

    private static JsonObject BlockedWorkflow(params (string Id, string Kind)[] blockers)
    {
        var workflow = DependentWorkflow("blocked");
        workflow["blockers"] = new JsonArray(blockers.Select(blocker => (JsonNode)new JsonObject { ["id"] = blocker.Id, ["kind"] = blocker.Kind, ["summary"] = "waiting on " + blocker.Id, ["recheckCondition"] = "r" }).ToArray());
        return workflow;
    }

    /// <summary>The outbox effects a board write added, read as (target, operation, payload); a manager-bound item is unwrapped from its route.</summary>
    private static List<(string Target, string Operation, JsonObject Item)> NewEffects(StateEnvelope<CoordinationDocument> state, ref int seen)
    {
        var added = state.Effects.Skip(seen).Select(effect =>
        {
            var payload = JsonNode.Parse(effect.PayloadJson)!.AsObject();
            return effect.Operation == "__attention_route"
                ? ("role:" + payload["role"]!.GetValue<string>(), payload["operation"]!.GetValue<string>(), payload["item"]!.AsObject())
                : (effect.TargetKey, effect.Operation, payload);
        }).ToList();
        seen = state.Effects.Count;
        return added;
    }

    [Fact]
    public void Board_sources_raise_and_close_attention_items_with_their_cause()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        var seen = 0;
        Assert.True(WriteWorkflow(state, "a", 0, DependentWorkflow("ready")).Ok);
        Assert.True(WriteWorkflow(state, "b", 0, DependentWorkflow("ready", "a"), "worker-b").Ok);
        Assert.Empty(NewEffects(state, ref seen));
        // b's worker claims it; a's claim by another worker means a blocks someone else's claimed task.
        Assert.True(Call(state, "board", "task_claim", new() { ["taskId"] = "b", ["sessionId"] = "worker-b", ["requestId"] = "claim-b", ["expectedVersion"] = 1 }).Ok);
        var claimed = NewEffects(state, ref seen);
        Assert.Equal(("worker-b", "__attention_close", "next_work"), (claimed.Single().Target, claimed.Single().Operation, claimed.Single().Item["kinds"]![0]!.GetValue<string>()));
        Assert.True(Call(state, "board", "task_claim", new() { ["taskId"] = "a", ["sessionId"] = "worker-a", ["requestId"] = "claim-a", ["expectedVersion"] = 1 }).Ok);
        Assert.Empty(NewEffects(state, ref seen));

        // Blocking: raised to managers, ranked by the most urgent blocker kind, marked blocking, with the waiting task as a ref.
        var blocked = Call(state, "board", "task_update", new() { ["taskId"] = "a", ["sessionId"] = "worker-a", ["requestId"] = "block", ["expectedVersion"] = 2, ["workflow"] = BlockedWorkflow(("hold", "coordinator_hold"), ("p", "provider_rejection")) });
        Assert.True(blocked.Ok, blocked.Code);
        var raise = Assert.Single(NewEffects(state, ref seen));
        Assert.Equal(("role:manager", "__attention_raise"), (raise.Target, raise.Operation));
        Assert.Equal(("blocked", "a", "provider_rejection", "waiting on p", true), (raise.Item["kind"]!.GetValue<string>(), raise.Item["taskId"]!.GetValue<string>(), raise.Item["blocker"]!.GetValue<string>(), raise.Item["note"]!.GetValue<string>(), raise.Item["blocking"]!.GetValue<bool>()));
        Assert.Equal(["b"], raise.Item["refs"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(Data(blocked)["changeRevision"]!.GetValue<long>(), raise.Item["revision"]!.GetValue<long>());
        Assert.Equal("worker-a", JsonNode.Parse(state.Effects.Last().PayloadJson)!["exceptSessionId"]!.GetValue<string>());
        // Rewriting the same blockers raises nothing new; the item is still open at the managers.
        Assert.True(Call(state, "board", "task_update", new() { ["taskId"] = "a", ["sessionId"] = "worker-a", ["requestId"] = "block-again", ["expectedVersion"] = 3, ["workflow"] = BlockedWorkflow(("hold", "coordinator_hold"), ("p", "provider_rejection")) }).Ok);
        Assert.Empty(NewEffects(state, ref seen));

        // Clearing the blocker closes the blocked item.
        var ready = DependentWorkflow("ready"); ready["recheckEvidenceRef"] = "evidence:1";
        Assert.True(Call(state, "board", "task_update", new() { ["taskId"] = "a", ["sessionId"] = "worker-a", ["requestId"] = "clear", ["expectedVersion"] = 4, ["workflow"] = ready }).Ok);
        var cleared = Assert.Single(NewEffects(state, ref seen));
        Assert.Equal(("role:manager", "__attention_close", "a"), (cleared.Target, cleared.Operation, cleared.Item["taskId"]!.GetValue<string>()));
        Assert.Equal(["blocked"], cleared.Item["kinds"]!.AsArray().Select(x => x!.GetValue<string>()));

        // A failed action raises failed to the managers.
        Assert.True(Call(state, "board", "action_begin", new() { ["taskId"] = "a", ["sessionId"] = "worker-a", ["requestId"] = "begin", ["expectedVersion"] = 5, ["actionId"] = "deploy", ["inputFingerprint"] = "f" }).Ok);
        NewEffects(state, ref seen);
        Assert.True(Call(state, "board", "action_report", new() { ["taskId"] = "a", ["sessionId"] = "worker-a", ["requestId"] = "fail", ["expectedVersion"] = 6, ["actionId"] = "deploy", ["attempt"] = 1, ["outcome"] = "failed" }).Ok);
        var failed = Assert.Single(NewEffects(state, ref seen), effect => effect.Operation == "__attention_raise");
        Assert.Equal(("role:manager", "failed", "action deploy failed"), (failed.Target, failed.Item["kind"]!.GetValue<string>(), failed.Item["note"]!.GetValue<string>()));

        // Completion closes blocked and failed, raises review_ready, closes the claimer's related work, and raises next_work to b's target.
        var done = Call(state, "board", "task_update", new() { ["taskId"] = "a", ["sessionId"] = "worker-a", ["requestId"] = "done", ["expectedVersion"] = 7, ["workflow"] = DependentWorkflow("completed") });
        Assert.True(done.Ok, done.Code);
        var completion = NewEffects(state, ref seen);
        Assert.Contains(completion, effect => effect is { Target: "role:manager", Operation: "__attention_close" } && effect.Item["kinds"]!.AsArray().Select(x => x!.GetValue<string>()).SequenceEqual(["blocked", "failed"]));
        Assert.Contains(completion, effect => effect is { Target: "role:manager", Operation: "__attention_raise" } && effect.Item["kind"]!.GetValue<string>() == "review_ready");
        Assert.Contains(completion, effect => effect is { Target: "worker-a", Operation: "__attention_close" } && effect.Item["subject"]!.GetValue<string>() == "a");
        Assert.Contains(completion, effect => effect is { Target: "worker-b", Operation: "__attention_raise" } && effect.Item["kind"]!.GetValue<string>() == "next_work");
        Assert.All(completion, effect => Assert.Equal(Data(done)["changeRevision"]!.GetValue<long>(), effect.Item["revision"]!.GetValue<long>()));

        // Reopening a completed task closes its review item.
        Assert.True(Call(state, "board", "task_update", new() { ["taskId"] = "a", ["sessionId"] = "worker-a", ["requestId"] = "reopen", ["expectedVersion"] = 8, ["workflow"] = DependentWorkflow("ready") }).Ok);
        var reopened = Assert.Single(NewEffects(state, ref seen));
        Assert.Equal(["review_ready"], reopened.Item["kinds"]!.AsArray().Select(x => x!.GetValue<string>()));

        // A standing proposal is raised to the managers and closed when an administrator decides it.
        var proposed = Data(Call(state, "board", "standing_propose", new() { ["sessionId"] = "worker-a", ["requestId"] = "propose", ["line"] = new JsonObject { ["text"] = "Run tests first" } }));
        var proposal = Assert.Single(NewEffects(state, ref seen));
        Assert.Equal(("standing_proposal", proposed["proposalId"]!.GetValue<string>(), "line: Run tests first"), (proposal.Item["kind"]!.GetValue<string>(), proposal.Item["subject"]!.GetValue<string>(), proposal.Item["note"]!.GetValue<string>()));
        Assert.True(Call(state, "board", "standing_put", new() { ["sessionId"] = "admin", ["requestId"] = "approve", ["expectedRevision"] = 0, ["approve"] = new JsonArray(proposed["proposalId"]!.GetValue<string>()) }).Ok);
        var decided = Assert.Single(NewEffects(state, ref seen));
        Assert.Equal(("__attention_close", proposed["proposalId"]!.GetValue<string>()), (decided.Operation, decided.Item["subject"]!.GetValue<string>()));
    }

    [Fact]
    public void Control_routes_attention_to_the_repositorys_managers_by_role()
    {
        var control = new StateEnvelope<CoordinationDocument>();
        string Open(string role, string repository = "repo", int daysAgo = 0)
        {
            var args = new JsonObject { ["requestId"] = Guid.NewGuid().ToString("N"), ["repositoryId"] = repository, ["role"] = role };
            return Data(CoordinationStateMachine.Apply(control, "control", "hub", new("alice", true, ["repo", "other"], true), "session_open", args, Now.AddDays(-daysAgo)))["sessionId"]!.GetValue<string>();
        }
        var manager = Open("Manager");
        var cause = Open("manager");
        Open("worker");
        Open("manager", "other");
        // A manager chat neither fresh nor active for over a day (a closed chat) is skipped, unless it reported activity since.
        Open("manager", daysAgo: 2);
        var active = Open("manager", daysAgo: 3);
        Assert.True(Call(control, "control", "__register", new() { ["sessionId"] = active, ["activeAt"] = Now.AddHours(-1).ToString("O") }).Ok);
        var before = control.Effects.Count;
        var routed = Call(control, "control", "__attention_route", new()
        {
            ["role"] = "manager", ["operation"] = "__attention_raise", ["exceptSessionId"] = cause,
            ["item"] = new JsonObject { ["kind"] = "blocked", ["taskId"] = "T-1", ["revision"] = 3 }
        });
        Assert.Equal(2, Data(routed)["routed"]!.GetValue<int>());
        var effects = control.Effects.Skip(before).ToArray();
        Assert.Equal([manager, active], effects.Select(item => item.TargetKey));
        var effect = effects.Single(item => item.TargetKey == manager);
        Assert.Equal(("session", manager, "__attention_raise"), (effect.TargetKind, effect.TargetKey, effect.Operation));
        Assert.Equal(("blocked", "repo"), (JsonNode.Parse(effect.PayloadJson)!["kind"]!.GetValue<string>(), JsonNode.Parse(effect.PayloadJson)!["repositoryId"]!.GetValue<string>()));
        // Only raises and closes are routed.
        Assert.False(Call(control, "control", "__attention_route", new() { ["role"] = "manager", ["operation"] = "__deliver", ["item"] = new JsonObject() }).Ok);
    }

    [Fact]
    public void A_lease_nearing_expiry_with_an_action_in_flight_raises_claim_expiring_until_renewed()
    {
        var resource = new StateEnvelope<CoordinationDocument>();
        RelayReply Lock(string op, JsonObject args, int seconds = 0)
        {
            args["repositoryId"] = "repo";
            var reply = CoordinationStateMachine.Apply(resource, "lock", "printer", Caller, op, args, Now.AddSeconds(seconds));
            CoordinationStateMachine.ScheduleLeases(resource);
            return reply;
        }
        Assert.True(Lock("claim_acquire", new() { ["sessionId"] = "worker", ["requestId"] = "acquire", ["leaseSeconds"] = 300 }).Ok);
        // No action in flight: no check is scheduled.
        Assert.DoesNotContain(resource.DueWork, work => work.Kind == "lease_warning");
        var key = CoordinationStateMachine.ActionKey(new JsonObject { ["repositoryId"] = "repo", ["boardId"] = "ops", ["taskId"] = "T-9" }, "deploy", 1);
        Assert.True(Lock("__block", new() { ["sessionId"] = "worker", ["actionId"] = "deploy", ["actionKey"] = key }).Ok);
        var warning = Assert.Single(resource.DueWork, work => work.Kind == "lease_warning");
        Assert.Equal(Now.AddSeconds(300 - RelayLimits.ClaimExpiringLeadSeconds), warning.DueAt);

        Assert.True(Lock("__lease_check", new(), seconds: 250).Ok);
        var raise = JsonNode.Parse(Assert.Single(resource.Effects).PayloadJson)!.AsObject();
        Assert.Equal(("worker", "__attention_raise"), (resource.Effects[0].TargetKey, resource.Effects[0].Operation));
        Assert.Equal(("claim_expiring", "T-9", "ops", "printer", "high"), (raise["kind"]!.GetValue<string>(), raise["taskId"]!.GetValue<string>(), raise["boardId"]!.GetValue<string>(), raise["subject"]!.GetValue<string>(), raise["priority"]!.GetValue<string>()));
        // One item per lease: a second check before the end raises nothing; the next check is due when the lease ends.
        Assert.True(Lock("__lease_check", new(), seconds: 260).Ok);
        Assert.Single(resource.Effects);
        Assert.Equal(Now.AddSeconds(300), Assert.Single(resource.DueWork, work => work.Kind == "lease_warning").DueAt);

        // Renewing closes it with a newer revision and schedules a check for the new lease.
        Assert.True(Lock("claim_renew", new() { ["sessionId"] = "worker", ["requestId"] = "renew", ["generation"] = 1, ["leaseSeconds"] = 300 }, seconds: 270).Ok);
        var close = JsonNode.Parse(resource.Effects[1].PayloadJson)!.AsObject();
        Assert.Equal(("__attention_close", "printer"), (resource.Effects[1].Operation, close["subject"]!.GetValue<string>()));
        Assert.True(close["revision"]!.GetValue<long>() > raise["revision"]!.GetValue<long>());
        Assert.Single(resource.DueWork, work => work.Kind == "lease_warning");
        // A renewal with no warning raised closes nothing.
        Assert.True(Lock("claim_renew", new() { ["sessionId"] = "worker", ["requestId"] = "renew-2", ["generation"] = 1, ["leaseSeconds"] = 300 }, seconds: 280).Ok);
        Assert.Equal(2, resource.Effects.Count);
    }

    [Fact]
    public void A_lease_that_ends_with_an_action_unresolved_closes_claim_expiring_and_raises_interrupted()
    {
        var resource = new StateEnvelope<CoordinationDocument>();
        RelayReply Lock(string op, JsonObject args, int seconds = 0)
        {
            args["repositoryId"] = "repo";
            var reply = CoordinationStateMachine.Apply(resource, "lock", "printer", Caller, op, args, Now.AddSeconds(seconds));
            CoordinationStateMachine.ScheduleLeases(resource);
            return reply;
        }
        List<(string Target, string Operation, JsonObject Item)> Effects(int from) => resource.Effects.Skip(from).Select(effect =>
        {
            var payload = JsonNode.Parse(effect.PayloadJson)!.AsObject();
            return effect.Operation == "__attention_route" ? ("role:" + payload["role"]!.GetValue<string>(), payload["operation"]!.GetValue<string>(), payload["item"]!.AsObject()) : (effect.TargetKey, effect.Operation, payload);
        }).ToList();
        Assert.True(Lock("claim_acquire", new() { ["sessionId"] = "worker", ["requestId"] = "acquire", ["leaseSeconds"] = 300 }).Ok);
        var key = CoordinationStateMachine.ActionKey(new JsonObject { ["repositoryId"] = "repo", ["boardId"] = "ops", ["taskId"] = "T-9" }, "deploy", 1);
        Assert.True(Lock("__block", new() { ["sessionId"] = "worker", ["actionId"] = "deploy", ["actionKey"] = key }).Ok);
        Assert.True(Lock("__lease_check", new(), seconds: 250).Ok);
        var warned = Assert.Single(Effects(0));
        Assert.Equal("claim_expiring", warned.Item["kind"]!.GetValue<string>());

        // The lease ends with the action still unresolved: claim_expiring closes, and interrupted goes to the holder and the managers.
        Assert.True(Lock("__lease_check", new(), seconds: 301).Ok);
        var ended = Effects(1);
        Assert.Equal(3, ended.Count);
        Assert.Equal(("worker", "__attention_close", "claim_expiring"), (ended[0].Target, ended[0].Operation, ended[0].Item["kinds"]![0]!.GetValue<string>()));
        foreach (var (target, item) in new[] { ("worker", ended[1].Item), ("role:manager", ended[2].Item) })
        {
            Assert.Equal(target, ended.Single(effect => effect.Item == item).Target);
            Assert.Equal(("interrupted", "T-9", "ops", "printer"), (item["kind"]!.GetValue<string>(), item["taskId"]!.GetValue<string>(), item["boardId"]!.GetValue<string>(), item["subject"]!.GetValue<string>()));
            Assert.Contains("deploy unresolved", item["note"]!.GetValue<string>());
        }
        // Every effect has its own, rising revision; nothing more is due for this lease, and a repeated check raises nothing.
        var revisions = Effects(0).Select(effect => effect.Item["revision"]!.GetValue<long>()).ToArray();
        Assert.Equal(revisions.Order(), revisions);
        Assert.Equal(revisions.Length, revisions.Distinct().Count());
        Assert.DoesNotContain(resource.DueWork, work => work.Kind == "lease_warning");
        Assert.True(Lock("__lease_check", new(), seconds: 400).Ok);
        Assert.Equal(4, resource.Effects.Count);

        // Reporting the action resolved closes interrupted at the holder and the managers.
        Assert.True(Lock("__unblock", new() { ["sessionId"] = "worker", ["actionId"] = "deploy", ["actionKey"] = key }, seconds: 500).Ok);
        var resolved = Effects(4);
        Assert.Equal(("worker", "__attention_close"), (resolved[0].Target, resolved[0].Operation));
        Assert.Contains("interrupted", resolved[0].Item["kinds"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(("role:manager", "__attention_close", "interrupted"), (resolved[1].Target, resolved[1].Operation, resolved[1].Item["kinds"]![0]!.GetValue<string>()));
        Assert.True(resolved.All(effect => effect.Item["revision"]!.GetValue<long>() > revisions.Max()));
    }

    /// <summary>An operation on a hub that runs standing orders.</summary>
    private static RelayReply OrderCall(StateEnvelope<CoordinationDocument> state, string kind, string op, JsonObject args, RelayCaller? caller = null, int seconds = 0, string key = "repo/default")
    {
        args["repositoryId"] = "repo";
        return CoordinationStateMachine.Apply(state, kind, key, caller ?? Caller, op, args, Now.AddSeconds(seconds), ordersEnabled: true);
    }

    private static JsonObject Order(string orderId, JsonObject trigger, params JsonObject[] actions) =>
        new() { ["requestId"] = Guid.NewGuid().ToString("N"), ["orderId"] = orderId, ["trigger"] = trigger, ["actions"] = new JsonArray(actions.Select(action => (JsonNode)action).ToArray()), ["defaultExpiryDays"] = 7 };

    private static JsonObject OrderTask(StateEnvelope<CoordinationDocument> board, string taskId)
    {
        var task = OrderCall(board, "board", "task_get", new() { ["taskId"] = taskId });
        Assert.True(task.Ok, task.Code);
        return Data(task);
    }

    [Fact]
    public void Standing_order_actions_commit_with_their_trigger_and_never_record_acceptance()
    {
        var board = new StateEnvelope<CoordinationDocument>();
        var worker = new RelayCaller("worker", false, ["repo"], true);
        Assert.True(OrderCall(board, "board", "task_update", new() { ["taskId"] = "a", ["requestId"] = "a", ["expectedVersion"] = 0, ["workflow"] = DependentWorkflow("ready") }, worker).Ok);
        Assert.True(OrderCall(board, "board", "task_update", new() { ["taskId"] = "b", ["requestId"] = "b", ["expectedVersion"] = 0, ["workflow"] = DependentWorkflow("ready") }, worker).Ok);
        Assert.True(OrderCall(board, "board", "task_claim", new() { ["taskId"] = "a", ["requestId"] = "claim", ["expectedVersion"] = 1, ["sessionId"] = "worker-a", ["resourceIds"] = new JsonArray("pc") }, worker).Ok);
        JsonObject Release() => new() { ["do"] = "release_claims" };
        JsonObject Offer() => new() { ["do"] = "offer", ["taskId"] = "b", ["sessionId"] = "worker-b" };
        JsonObject Notify() => new() { ["do"] = "notify", ["sessionId"] = "manager-1", ["note"] = "a is done" };
        JsonObject Completed(string taskId = "a") => new() { ["on"] = "workflow_completed", ["taskId"] = taskId };

        // Writes need an administrator token and a hub with orders on; a session's role is no authority.
        Assert.Equal("AdministratorRequired", OrderCall(board, "board", "order_put", Order("o-1", Completed(), Release()), worker).Code);
        Assert.Equal("OrdersUnavailable", Call(board, "board", "order_put", Order("o-1", Completed(), Release())).Code);
        // Actions are coordination only: acceptance, unknown tasks, extra fields, a second order on one trigger and long lives are refused.
        foreach (var (order, field) in new (JsonObject, string)[]
        {
            (Order("o-1", Completed(), new JsonObject { ["do"] = "accept" }), "actions[0].do"),
            (Order("o-1", Completed(), new JsonObject { ["do"] = "release_claims", ["restoration"] = "accepted" }), "actions[0].restoration"),
            (Order("o-1", Completed("missing"), Release()), "trigger.taskId"),
            (Order("o-1", new JsonObject { ["on"] = "claim_released", ["taskId"] = "a" }, Release()), "trigger.taskId"),
            (Order("o-1", Completed(), new JsonObject { ["do"] = "offer", ["taskId"] = "elsewhere", ["sessionId"] = "worker-b" }), "actions[0].taskId"),
            (Order("o-1", Completed()), "actions"),
            (Order("o-1", Completed(), Release(), Release(), Release(), Release(), Release()), "actions"),
        })
            Assert.Equal(field, Data(OrderCall(board, "board", "order_put", order))["field"]!.GetValue<string>());
        var tooLong = Order("o-1", Completed(), Release()); tooLong["expiresAt"] = Now.AddDays(RelayLimits.MaxOrderExpiryDays + 1).ToString("O");
        Assert.Equal("expiresAt", Data(OrderCall(board, "board", "order_put", tooLong))["field"]!.GetValue<string>());

        var put = OrderCall(board, "board", "order_put", Order("o-1", Completed(), Release(), Offer(), Notify()));
        Assert.True(put.Ok, put.DataJson);
        Assert.Equal(Now.AddDays(7), DateTimeOffset.Parse(Data(put)["expiresAt"]!.GetValue<string>()));
        Assert.Equal("o-1", Data(OrderCall(board, "board", "order_put", Order("o-2", Completed(), Notify())))["conflictsWith"]!.GetValue<string>());
        // An optional expectedRevision must match the write revision, as with standing_put.
        Assert.Equal(1, put.Version);
        var stale = Order("o-3", Completed("b"), Notify()); stale["expectedRevision"] = 0;
        var conflict = OrderCall(board, "board", "order_put", stale);
        Assert.Equal(("VersionConflict", 1L), (conflict.Code, Data(conflict)["currentRevision"]!.GetValue<long>()));
        var current = Order("o-3", Completed("b"), Notify()); current["expectedRevision"] = 1;
        Assert.Equal(2, OrderCall(board, "board", "order_put", current).Version);
        Assert.True(OrderCall(board, "board", "order_remove", new() { ["requestId"] = "remove-o-3", ["orderId"] = "o-3", ["expectedRevision"] = 2 }).Ok);
        var seen = board.Effects.Count;

        // A refused completion commits nothing, so no action is emitted without its trigger.
        Assert.Equal("VersionConflict", OrderCall(board, "board", "task_update", new() { ["taskId"] = "a", ["sessionId"] = "worker-a", ["requestId"] = "stale", ["expectedVersion"] = 1, ["workflow"] = DependentWorkflow("completed") }, worker).Code);
        Assert.Equal(seen, board.Effects.Count);

        var complete = new JsonObject { ["taskId"] = "a", ["sessionId"] = "worker-a", ["requestId"] = "done", ["expectedVersion"] = 2, ["workflow"] = DependentWorkflow("completed") };
        var done = OrderCall(board, "board", "task_update", complete.DeepClone().AsObject(), worker);
        Assert.True(done.Ok, done.DataJson);
        // The completion and every action commit in the same write: the task claim is released in place, and the resource release,
        // the offer and the notice are outbox effects in the same envelope.
        var effects = NewEffects(board, ref seen);
        var lockRelease = Assert.Single(effects, effect => effect.Operation == "__order_release");
        Assert.Equal(("pc", "worker-a", "o-1", "repo/default"), (lockRelease.Target, lockRelease.Item["holderSessionId"]!.GetValue<string>(), lockRelease.Item["orderId"]!.GetValue<string>(), lockRelease.Item["boardKey"]!.GetValue<string>()));
        // The release names the task claim it is for, so the lock never drops a later claim of the same resource.
        Assert.Equal("default/a#1", lockRelease.Item["claimFor"]!.GetValue<string>());
        var offer = Assert.Single(effects, effect => effect.Target == "worker-b");
        Assert.Equal(("__attention_raise", "next_work", "b", "o-1"), (offer.Operation, offer.Item["kind"]!.GetValue<string>(), offer.Item["taskId"]!.GetValue<string>(), offer.Item["subject"]!.GetValue<string>()));
        var notice = Assert.Single(effects, effect => effect.Target == "manager-1");
        Assert.Equal(("info", "a is done", "a"), (notice.Item["kind"]!.GetValue<string>(), notice.Item["note"]!.GetValue<string>(), notice.Item["taskId"]!.GetValue<string>()));
        Assert.All(new[] { lockRelease, offer, notice }, effect => Assert.Equal(lockRelease.Item["run"]!.GetValue<long>(), effect.Item.ContainsKey("run") ? effect.Item["run"]!.GetValue<long>() : effect.Item["revision"]!.GetValue<long>()));
        var task = OrderTask(board, "a");
        Assert.False(task.ContainsKey("owner"));
        Assert.Equal(Data(done)["version"]!.GetValue<long>(), task["version"]!.GetValue<long>());

        // Acceptance stays a manager's decision: the workflow is exactly what the worker reported, no action is accepted, and the
        // review item still goes to the managers.
        Assert.Equal(DependentWorkflow("completed").ToJsonString(), task["workflow"]!.ToJsonString());
        Assert.Empty(task["actions"]!.AsObject());
        Assert.DoesNotContain("accepted", task.ToJsonString());
        Assert.Contains(effects, effect => effect.Target == "role:manager" && effect.Item["kind"]?.GetValue<string>() == "review_ready");

        // A retried completion replays its reply and emits nothing again.
        Assert.Equal(done, OrderCall(board, "board", "task_update", complete.DeepClone().AsObject(), worker));
        Assert.Empty(NewEffects(board, ref seen));
        var status = Data(OrderCall(board, "board", "__orders", new()))["orders"]!.AsArray().Single()!.AsObject();
        Assert.Equal(("o-1", "workflow_completed", "a", 1L, 0L), (status["id"]!.GetValue<string>(), status["on"]!.GetValue<string>(), status["t"]!.GetValue<string>(), status["runs"]!.GetValue<long>(), status["fails"]!.GetValue<long>()));

        // A hub with orders off runs nothing, even for an order written earlier.
        var reopen = DependentWorkflow("ready");
        Assert.True(Call(board, "board", "task_update", new() { ["taskId"] = "a", ["requestId"] = "reopen", ["expectedVersion"] = 3, ["workflow"] = reopen }, worker).Ok);
        Assert.True(Call(board, "board", "task_update", new() { ["taskId"] = "a", ["requestId"] = "again", ["expectedVersion"] = 4, ["workflow"] = DependentWorkflow("completed") }, worker).Ok);
        Assert.DoesNotContain(NewEffects(board, ref seen), effect => effect.Item.ContainsKey("orderId") || effect.Item["subject"]?.GetValue<string>() == "o-1");

        // Removing an order closes its failure item at the managers; removing it twice is not found.
        Assert.True(OrderCall(board, "board", "order_remove", new() { ["requestId"] = "remove", ["orderId"] = "o-1" }).Ok);
        Assert.Contains(NewEffects(board, ref seen), effect => effect is { Target: "role:manager", Operation: "__attention_close" } && effect.Item["subject"]!.GetValue<string>() == "o-1");
        Assert.Equal("NotFound", OrderCall(board, "board", "order_remove", new() { ["requestId"] = "remove-again", ["orderId"] = "o-1" }).Code);
    }

    [Fact]
    public void A_standing_order_offer_applies_the_next_work_checks_and_fails_the_run_otherwise()
    {
        var board = new StateEnvelope<CoordinationDocument>();
        var seen = 0;
        RelayReply Write(string taskId, JsonObject workflow, long version = 0) => OrderCall(board, "board", "task_update", new() { ["taskId"] = taskId, ["requestId"] = Guid.NewGuid().ToString("N"), ["expectedVersion"] = version, ["workflow"] = workflow });
        foreach (var id in new[] { "t", "dep", "free" }) Assert.True(Write(id, DependentWorkflow("ready")).Ok);
        Assert.True(Write("waiting", DependentWorkflow("ready", "dep")).Ok);
        // A grant named in authorityRef that is later removed.
        Assert.True(OrderCall(board, "board", "standing_put", new() { ["requestId"] = "grant", ["expectedRevision"] = 0, ["grants"] = new JsonArray(new JsonObject { ["id"] = "g", ["scope"] = "repository", ["expiresAt"] = Now.AddDays(1).ToString("O") }) }).Ok);
        var granted = DependentWorkflow("ready"); granted["packet"]!["authorityRef"] = "grant:g";
        Assert.True(Write("granted", granted).Ok);
        Assert.True(OrderCall(board, "board", "standing_put", new() { ["requestId"] = "ungrant", ["expectedRevision"] = 1, ["remove"] = new JsonArray("g") }).Ok);
        // Claimed by another session, and claimed by the offered session with an action in flight.
        Assert.True(Write("taken", DependentWorkflow("ready")).Ok);
        Assert.True(OrderCall(board, "board", "task_claim", new() { ["taskId"] = "taken", ["requestId"] = "taken", ["expectedVersion"] = 1, ["sessionId"] = "other" }).Ok);
        Assert.True(Write("busy", DependentWorkflow("ready")).Ok);
        Assert.True(OrderCall(board, "board", "task_claim", new() { ["taskId"] = "busy", ["requestId"] = "busy", ["expectedVersion"] = 1, ["sessionId"] = "s" }).Ok);
        Assert.True(OrderCall(board, "board", "action_begin", new() { ["taskId"] = "busy", ["sessionId"] = "s", ["requestId"] = "busy-begin", ["expectedVersion"] = 2, ["actionId"] = "a", ["inputFingerprint"] = "f" }).Ok);
        Assert.True(OrderCall(board, "board", "task_update", new() { ["taskId"] = "plain", ["requestId"] = "plain", ["expectedVersion"] = 0 }).Ok);
        JsonObject Offer(string taskId) => new() { ["do"] = "offer", ["taskId"] = taskId, ["sessionId"] = "s" };
        Assert.True(OrderCall(board, "board", "order_put", Order("o-1", new JsonObject { ["on"] = "workflow_completed", ["taskId"] = "t" }, Offer("waiting"), Offer("granted"), Offer("taken"), Offer("busy"))).Ok);
        Assert.True(OrderCall(board, "board", "order_put", Order("o-2", new JsonObject { ["on"] = "workflow_completed", ["taskId"] = "free" }, Offer("plain"), Offer("free"))).Ok);
        NewEffects(board, ref seen);

        Assert.True(Write("t", DependentWorkflow("completed"), 1).Ok);
        var effects = NewEffects(board, ref seen);
        // No offer goes out; the run fails with every reason, logged and raised to the managers.
        Assert.DoesNotContain(effects, effect => effect.Target == "s");
        var failed = Assert.Single(effects, effect => effect.Item["kind"]?.GetValue<string>() == "order_failed");
        var note = failed.Item["note"]!.GetValue<string>();
        foreach (var reason in new[] { "task waiting has dependencies not completed", "task granted names a grant that is unknown", "task taken is claimed by another session", "task busy has an unresolved action" })
            Assert.Contains(reason, note);
        Assert.Equal(1L, Data(OrderCall(board, "board", "__orders", new()))["orders"]!.AsArray().Single(order => order!["id"]!.GetValue<string>() == "o-1")!["fails"]!.GetValue<long>());

        // A task without a workflow is never offered; the task that completed is not offered either.
        Assert.True(Write("free", DependentWorkflow("completed"), 1).Ok);
        var second = Assert.Single(NewEffects(board, ref seen), effect => effect.Item["kind"]?.GetValue<string>() == "order_failed").Item["note"]!.GetValue<string>();
        Assert.Contains("task plain has no workflow", second);
        Assert.Contains("task free is completed", second);
    }

    [Fact]
    public void Standing_orders_are_idempotent_on_redelivery_raise_failures_disable_after_three_and_never_fire_expired()
    {
        var board = new StateEnvelope<CoordinationDocument>();
        var printer = new StateEnvelope<CoordinationDocument>();
        var seen = 0;
        RelayReply Lock(string op, JsonObject args, int seconds = 0) => OrderCall(printer, "lock", op, args, seconds: seconds, key: "pc");
        Assert.True(OrderCall(board, "board", "task_update", new() { ["taskId"] = "t", ["requestId"] = "t", ["expectedVersion"] = 0, ["workflow"] = DependentWorkflow("ready") }).Ok);
        Assert.True(OrderCall(board, "board", "task_claim", new() { ["taskId"] = "t", ["requestId"] = "claim", ["expectedVersion"] = 1, ["sessionId"] = "holder" }).Ok);
        Assert.True(OrderCall(board, "board", "action_begin", new() { ["taskId"] = "t", ["sessionId"] = "holder", ["requestId"] = "begin", ["expectedVersion"] = 2, ["actionId"] = "print", ["inputFingerprint"] = "f" }).Ok);
        var put = Order("o-1", new JsonObject { ["on"] = "claim_released", ["resourceId"] = "pc" }, new JsonObject { ["do"] = "release_claims", ["taskId"] = "t" });
        Assert.True(OrderCall(board, "board", "order_put", put).Ok);
        // The order asks the lock it watches to tell this board when its claim is released.
        var watch = Assert.Single(NewEffects(board, ref seen), effect => effect.Operation == "__order_watch");
        Assert.Equal(("pc", true), (watch.Target, watch.Item["watch"]!.GetValue<bool>()));
        Assert.True(Lock("__order_watch", watch.Item.DeepClone().AsObject()).Ok);

        // A release tells the board with the lock's generation; delivering that twice runs the order once.
        var lockSeen = 0;
        Assert.True(Lock("claim_acquire", new() { ["sessionId"] = "other", ["requestId"] = "acquire" }).Ok);
        Assert.True(Lock("claim_release", new() { ["sessionId"] = "other", ["requestId"] = "release", ["generation"] = 1 }).Ok);
        var fire = Assert.Single(NewEffects(printer, ref lockSeen), effect => effect.Operation == "__order_fire");
        Assert.Equal(("repo/default", "pc", 1L), (fire.Target, fire.Item["resourceId"]!.GetValue<string>(), fire.Item["generation"]!.GetValue<long>()));
        Assert.True(OrderCall(board, "board", "__order_fire", fire.Item.DeepClone().AsObject()).Ok);
        // Its task has an action in flight, so the release fails: logged, raised to the managers, never retried.
        var failed = Assert.Single(NewEffects(board, ref seen));
        Assert.Equal(("role:manager", "__attention_raise", "order_failed", "o-1"), (failed.Target, failed.Operation, failed.Item["kind"]!.GetValue<string>(), failed.Item["subject"]!.GetValue<string>()));
        Assert.Contains("unresolved action", failed.Item["note"]!.GetValue<string>());
        Assert.True(OrderCall(board, "board", "__order_fire", fire.Item.DeepClone().AsObject()).Ok);
        Assert.Empty(NewEffects(board, ref seen));
        Assert.Equal("holder", OrderTask(board, "t")["owner"]!.GetValue<string>());

        // Two more failed runs disable the order; a disabled order does not run.
        for (var generation = 2; generation <= 4; generation++)
        {
            var again = fire.Item.DeepClone().AsObject(); again["generation"] = generation;
            Assert.True(OrderCall(board, "board", "__order_fire", again).Ok);
            var raised = NewEffects(board, ref seen);
            if (generation == 4) { Assert.Empty(raised); continue; }
            Assert.Equal("order_failed", Assert.Single(raised).Item["kind"]!.GetValue<string>());
            if (generation == 3) Assert.Contains("disabled after 3 failures", raised[0].Item["note"]!.GetValue<string>());
        }
        var status = Data(OrderCall(board, "board", "__orders", new()))["orders"]!.AsArray().Single()!.AsObject();
        Assert.Equal((3L, 3L, true), (status["runs"]!.GetValue<long>(), status["fails"]!.GetValue<long>(), status["off"]!.GetValue<bool>()));
        Assert.Contains("unresolved action", status["err"]!.GetValue<string>());

        // A lock-side release runs once per order run, leaves a claim the owner no longer holds alone, and reports an unresolved action back.
        Assert.True(Lock("claim_acquire", new() { ["sessionId"] = "holder", ["requestId"] = "hold", ["taskId"] = "t", ["taskClaimGeneration"] = 1 }).Ok);
        NewEffects(printer, ref lockSeen);
        var release = new JsonObject { ["boardKey"] = "repo/default", ["boardId"] = "default", ["holderSessionId"] = "holder", ["claimFor"] = "default/t#1", ["orderId"] = "o-9", ["run"] = 7 };
        Assert.True(Lock("__block", new() { ["actionId"] = "print", ["sessionId"] = "holder" }).Ok);
        Assert.True(Data(Lock("__order_release", release.DeepClone().AsObject()))["failed"]!.GetValue<bool>());
        var back = Assert.Single(NewEffects(printer, ref lockSeen));
        Assert.Equal(("repo/default", "__order_failed", "o-9", 7L), (back.Target, back.Operation, back.Item["orderId"]!.GetValue<string>(), back.Item["run"]!.GetValue<long>()));
        Assert.True(Data(Lock("__order_release", release.DeepClone().AsObject()))["repeated"]!.GetValue<bool>());
        Assert.Empty(NewEffects(printer, ref lockSeen));
        Assert.True(Lock("__unblock", new() { ["actionId"] = "print", ["sessionId"] = "holder" }).Ok);
        // Complete, then claim again: the holder took the same resource for its next task (same lock generation) before the order's
        // release arrived. The late release leaves the new claim alone.
        Assert.True(Lock("claim_acquire", new() { ["sessionId"] = "holder", ["requestId"] = "hold-next", ["taskId"] = "t2", ["taskClaimGeneration"] = 1 }).Ok);
        release["run"] = 8;
        var late = Data(Lock("__order_release", release.DeepClone().AsObject()));
        Assert.Equal((false, true), (late["released"]!.GetValue<bool>(), late["claimedAgain"]!.GetValue<bool>()));
        Assert.Equal("Busy", Lock("claim_acquire", new() { ["sessionId"] = "someone", ["requestId"] = "steal" }).Code);
        Assert.Empty(NewEffects(printer, ref lockSeen));
        // A release for the claim the grant serves now does release it.
        release["run"] = 9; release["claimFor"] = "default/t2#1";
        Assert.True(Data(Lock("__order_release", release.DeepClone().AsObject()))["released"]!.GetValue<bool>());
        Assert.Contains(NewEffects(printer, ref lockSeen), effect => effect.Operation == "__order_fire");
        release["run"] = 10;
        Assert.False(Data(Lock("__order_release", release.DeepClone().AsObject()))["released"]!.GetValue<bool>());
        // A late failure report for an order run that is not the latest is logged but not counted.
        Assert.False(Data(OrderCall(board, "board", "__order_failed", new() { ["orderId"] = "o-1", ["run"] = 1, ["reason"] = "late" }))["counted"]!.GetValue<bool>());

        // An expired order never runs.
        Assert.True(OrderCall(board, "board", "task_update", new() { ["taskId"] = "u", ["requestId"] = "u", ["expectedVersion"] = 0, ["workflow"] = DependentWorkflow("ready") }).Ok);
        var brief = Order("o-2", new JsonObject { ["on"] = "workflow_completed", ["taskId"] = "u" }, new JsonObject { ["do"] = "notify", ["sessionId"] = "manager-1" });
        brief["expiresAt"] = Now.AddHours(1).ToString("O");
        Assert.True(OrderCall(board, "board", "order_put", brief).Ok);
        NewEffects(board, ref seen);
        Assert.True(OrderCall(board, "board", "task_update", new() { ["taskId"] = "u", ["requestId"] = "u-done", ["expectedVersion"] = 1, ["workflow"] = DependentWorkflow("completed") }, seconds: 7200).Ok);
        Assert.DoesNotContain(NewEffects(board, ref seen), effect => effect.Target == "manager-1");
        Assert.True(Data(OrderCall(board, "board", "__orders", new(), seconds: 7200))["orders"]!.AsArray().Single(order => order!["id"]!.GetValue<string>() == "o-2")!["expired"]!.GetValue<bool>());

        // A board with no order on a lock any more asks the lock to stop telling it.
        Assert.True(OrderCall(board, "board", "order_remove", new() { ["requestId"] = "remove", ["orderId"] = "o-1" }).Ok);
        var unwatch = Assert.Single(NewEffects(board, ref seen), effect => effect.Operation == "__order_watch");
        Assert.False(unwatch.Item["watch"]!.GetValue<bool>());
        Assert.False(Data(OrderCall(board, "board", "__order_fire", fire.Item.DeepClone().AsObject()))["ran"]!.GetValue<bool>());
        Assert.True(Lock("__order_watch", unwatch.Item.DeepClone().AsObject()).Ok);
        Assert.True(Lock("claim_acquire", new() { ["sessionId"] = "next", ["requestId"] = "next" }).Ok);
        Assert.True(Lock("claim_release", new() { ["sessionId"] = "next", ["requestId"] = "next-release", ["generation"] = 3 }).Ok);
        Assert.DoesNotContain(NewEffects(printer, ref lockSeen), effect => effect.Operation == "__order_fire");
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
