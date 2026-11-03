using System.Text.Json.Nodes;
using System.Text.Json;
using System.Reflection;
using Orleans;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Domain.Messages;
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
        Assert.True(Call(state, "lock", "__block", new() { ["actionId"] = "a", ["actionKey"] = "a/1", ["sessionId"] = "s1" }).Ok);
        Assert.Equal("Busy", Call(state, "lock", "__block", new() { ["actionId"] = "other", ["actionKey"] = "other/1", ["sessionId"] = "s1" }).Code);
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
        Assert.True(Call(state, "session", "__open", new() { ["principalId"] = "alice", ["sessionToken"] = "secret", ["sessionId"] = "key" }).Ok);
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
        Assert.Equal("Ok", (await Invoke(alice, "action_begin", begin)).Code); // The hub blocks the locks itself, so even this fake confirms them.
        var report = For(worker); report["expectedVersion"] = 3; report["actionId"] = "a"; report["attempt"] = 1; report["outcome"] = "completed"; report["restoration"] = "blocked";
        Assert.True((await Invoke(alice, "action_report", report)).Ok);
        var acceptance = For(admin); acceptance["expectedVersion"] = 4; acceptance["actionId"] = "a"; acceptance["attempt"] = 1; acceptance["restoration"] = "accepted";
        Assert.True((await Invoke(bob, "action_report", acceptance)).Ok);
        var spoof = For(worker); spoof["expectedVersion"] = 5; spoof["actionId"] = "a"; spoof["attempt"] = 1; spoof["restoration"] = "accepted";
        Assert.Equal("SessionDenied", (await Invoke(bob, "action_report", spoof)).Code);
    }

    private static JsonObject StateJson(StateEnvelope<CoordinationDocument> state) => JsonNode.Parse(state.Value.Json)!.AsObject();
    private static JsonObject Note(string requestId, long version) => new() { ["contextId"] = "note", ["requestId"] = requestId, ["expectedVersion"] = version, ["content"] = "v" + version };

    [Fact]
    public void Request_replays_are_bounded_so_one_note_takes_three_times_the_record_limit()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        Assert.True(Call(state, "board", "task_update", new() { ["taskId"] = "t", ["requestId"] = "create", ["expectedVersion"] = 0 }).Ok);
        // A request that would change state again if it ran twice: a task message has no expected version.
        var message = new JsonObject { ["taskId"] = "t", ["sessionId"] = "s", ["requestId"] = "first-message", ["text"] = "hello" };
        Assert.True(Call(state, "board", "send_message", message.DeepClone().AsObject()).Ok);
        RelayReply last = default!;
        for (var version = 0; version < 3 * RelayLimits.MaxRecords; version++)
        {
            last = Call(state, "board", "context_put", Note("note-" + version, version));
            Assert.True(last.Ok, version + ": " + last.Code);
        }
        var stored = StateJson(state);
        Assert.InRange(stored["requests"]!.AsObject().Count, 1, RelayLimits.MaxRecords);
        Assert.InRange(stored["requestTombstones"]!.AsObject().Count, 1, RelayLimits.MaxRequestTombstones);
        // A recent retry still replays its original reply.
        Assert.Equal(last, Call(state, "board", "context_put", Note("note-" + (3 * RelayLimits.MaxRecords - 1), 3 * RelayLimits.MaxRecords - 1)));
        // An evicted request is refused, not run again, and changes nothing.
        var before = state.Value.Json;
        var expired = Call(state, "board", "send_message", message.DeepClone().AsObject());
        Assert.Equal("RequestExpired", expired.Code);
        Assert.True(Data(expired)["readCurrentState"]!.GetValue<bool>());
        Assert.Equal(RelayErrorKind.Conflict, RelayErrorCodes.Classify(expired.Code));
        Assert.Equal(before, state.Value.Json);
        Assert.Single(StateJson(state)["tasks"]!["t"]!["messages"]!.AsArray());
        // Reads are never stored, so they never hit a tombstone.
        Assert.True(Call(state, "board", "task_get", new() { ["taskId"] = "t", ["requestId"] = "first-message" }).Ok);
    }

    [Fact]
    public void Request_replays_past_the_window_are_pruned_and_old_format_entries_count_as_old()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        Assert.True(Call(state, "board", "context_put", Note("legacy", 0)).Ok);
        // Entries written before retention carry no time; one still replays its original reply.
        var stored = StateJson(state);
        var legacyKey = "alice:context_put:legacy";
        stored["requests"]![legacyKey]!.AsObject().Remove("at");
        state.Value = new() { Json = stored.ToJsonString() };
        Assert.Equal(1, Call(state, "board", "context_put", Note("legacy", 0)).Version);

        // Fill the map: old-format and day-old entries go by time, not count; a recent one stays. An old-format entry has no time and may
        // be recent, so it alone leaves a tombstone.
        stored = StateJson(state);
        var requests = stored["requests"]!.AsObject();
        var prior = requests[legacyKey]!.DeepClone().AsObject();
        for (var i = 0; requests.Count < RelayLimits.MaxRecords - 1; i++)
        {
            var old = prior.DeepClone().AsObject(); old["at"] = Now.AddHours(-RelayLimits.RequestReplayRetentionHours - 1).ToString("O");
            requests["alice:context_put:old-" + i] = old;
        }
        var recent = prior.DeepClone().AsObject(); recent["at"] = Now.AddHours(-1).ToString("O");
        requests["alice:context_put:recent"] = recent;
        state.Value = new() { Json = stored.ToJsonString() };
        Assert.Equal(RelayLimits.MaxRecords, StateJson(state)["requests"]!.AsObject().Count);

        Assert.True(Call(state, "board", "context_put", Note("new", 1)).Ok);
        stored = StateJson(state);
        Assert.Equal(["alice:context_put:recent", "alice:context_put:new"], stored["requests"]!.AsObject().Select(x => x.Key));
        Assert.Single(stored["requestTombstones"]!.AsObject());
        Assert.Equal(Now.ToString("O"), stored["requests"]!["alice:context_put:new"]!["at"]!.GetValue<string>());
        // The old-format request's retry is refused, not run; a day-old one past its window runs as new (its stale version conflicts).
        Assert.Equal("RequestExpired", Call(state, "board", "context_put", Note("legacy", 0)).Code);
        Assert.Equal("VersionConflict", Call(state, "board", "context_put", Note("old-0", 0)).Code);
    }

    [Fact]
    public void Acknowledgments_and_resolved_actions_stay_bounded_and_keep_ignoring_recent_duplicates()
    {
        var session = new StateEnvelope<CoordinationDocument>();
        Assert.True(Call(session, "session", "__open", new() { ["principalId"] = "alice", ["sessionToken"] = "secret", ["sessionId"] = "key" }).Ok);
        var total = 3 * RelayLimits.MaxRecords;
        for (var batch = 0; batch < total / RelayLimits.MaxAcknowledgedMessages + 1; batch++)
        {
            var ids = Enumerable.Range(batch * RelayLimits.MaxAcknowledgedMessages, RelayLimits.MaxAcknowledgedMessages).Select(i => (JsonNode)("m-" + i)).ToArray();
            var ack = Call(session, "session", "ack", new() { ["sessionToken"] = "secret", ["requestId"] = "ack-" + batch, ["messageIds"] = new JsonArray(ids) });
            Assert.True(ack.Ok, batch + ": " + ack.Code);
        }
        Assert.InRange(StateJson(session)["acked"]!.AsObject().Count, 1, RelayLimits.MaxRecords);
        // A late redelivery of a recently acknowledged message is still ignored.
        Assert.True(Data(Call(session, "session", "__deliver", new() { ["messageId"] = "m-" + (total - 1), ["text"] = "again" }))["duplicate"]!.GetValue<bool>());
        // Acknowledgments stored as true (before retention) count as old and are pruned first.
        var stored = StateJson(session);
        stored["acked"] = new JsonObject(Enumerable.Range(0, RelayLimits.MaxRecords).Select(i => KeyValuePair.Create("legacy-" + i, (JsonNode?)true)));
        session.Value = new() { Json = stored.ToJsonString() };
        Assert.True(Call(session, "session", "ack", new() { ["sessionToken"] = "secret", ["requestId"] = "ack-after-legacy", ["messageIds"] = new JsonArray("fresh") }).Ok);
        Assert.Equal(["fresh"], StateJson(session)["acked"]!.AsObject().Select(x => x.Key));

        // A resource that resolved MaxRecords actions within the window still unblocks: the oldest are dropped, and recent ones are kept.
        var resource = new StateEnvelope<CoordinationDocument>();
        Assert.True(Call(resource, "lock", "claim_acquire", new() { ["sessionId"] = "s1", ["requestId"] = "c", ["leaseSeconds"] = RelayLimits.MaxLeaseSeconds }).Ok);
        for (var i = 0; i < RelayLimits.MaxRecords; i++)
            Assert.True(Call(resource, "lock", "__unblock", new() { ["actionKey"] = "action-" + i, ["actionId"] = "a", ["sessionId"] = "s1" }, seconds: i).Ok, i.ToString());
        Assert.True(Call(resource, "lock", "__unblock", new() { ["actionKey"] = "action-new", ["actionId"] = "a", ["sessionId"] = "s1" }, seconds: RelayLimits.MaxRecords).Ok);
        var resolved = StateJson(resource)["resolvedActions"]!.AsObject();
        Assert.Equal(RelayLimits.RetainedEntriesAfterPrune + 1, resolved.Count);
        Assert.False(resolved.ContainsKey("action-0"));
        // A late block for a recently resolved action is still ignored, so it cannot strand the resource.
        Assert.True(Data(Call(resource, "lock", "__block", new() { ["actionKey"] = "action-" + (RelayLimits.MaxRecords - 1), ["actionId"] = "a", ["sessionId"] = "s1" }, seconds: RelayLimits.MaxRecords))["resolved"]!.GetValue<bool>());
        Assert.Null(StateJson(resource)["blockers"]?.AsObject().FirstOrDefault().Key);
        // Resolutions stored as true (before retention) count as old.
        stored = StateJson(resource);
        stored["resolvedActions"] = new JsonObject(Enumerable.Range(0, RelayLimits.MaxRecords).Select(i => KeyValuePair.Create("legacy-" + i, (JsonNode?)true)));
        resource.Value = new() { Json = stored.ToJsonString() };
        Assert.True(Call(resource, "lock", "__unblock", new() { ["actionKey"] = "after-legacy", ["actionId"] = "a", ["sessionId"] = "s1" }).Ok);
        Assert.Equal(["after-legacy"], StateJson(resource)["resolvedActions"]!.AsObject().Select(x => x.Key));
    }

    [Fact]
    public void A_message_that_escaping_would_make_unreadable_is_refused_when_sent()
    {
        // 7,500 backslashes are within the 8 KiB text limit, but JSON escaping doubles them twice on the way to a reader.
        var text = new string('\\', 7_500);
        var sender = new StateEnvelope<CoordinationDocument>();
        Assert.True(Call(sender, "session", "__open", new() { ["principalId"] = "alice", ["sessionToken"] = "secret", ["sessionId"] = "key" }).Ok);
        var direct = Call(sender, "session", "send_message", new() { ["sessionToken"] = "secret", ["requestId"] = "big", ["recipientSessionId"] = "r", ["text"] = text });
        Assert.Equal("PayloadTooLarge", direct.Code);
        Assert.Equal(RelayLimits.AggregateReplyBytes, Data(direct)["maxBytes"]!.GetValue<int>());
        Assert.True(Data(direct)["bytes"]!.GetValue<int>() > RelayLimits.AggregateReplyBytes);
        Assert.Empty(sender.Effects);
        // The same text in plain letters is delivered and readable.
        Assert.True(Call(sender, "session", "send_message", new() { ["sessionToken"] = "secret", ["requestId"] = "plain", ["recipientSessionId"] = "r", ["text"] = new string('x', 7_500) }).Ok);

        var board = new StateEnvelope<CoordinationDocument>();
        Assert.True(Call(board, "board", "task_update", new() { ["taskId"] = "t", ["requestId"] = "create", ["expectedVersion"] = 0 }).Ok);
        Assert.Equal("PayloadTooLarge", Call(board, "board", "send_message", new() { ["taskId"] = "t", ["sessionId"] = "s", ["requestId"] = "big", ["text"] = text }).Code);
        Assert.Equal("PayloadTooLarge", Call(board, "board", "task_update", new() { ["taskId"] = "t", ["sessionId"] = "s", ["requestId"] = "big-update", ["expectedVersion"] = 1, ["message"] = new JsonObject { ["text"] = text } }).Code);
        Assert.Equal(1, Call(board, "board", "task_get", new() { ["taskId"] = "t" }).Version);
        // A shared note under its own byte limits but unreadable once escaped is refused the same way.
        Assert.Equal("PayloadTooLarge", Call(board, "board", "context_put", new() { ["contextId"] = "n", ["requestId"] = "note", ["expectedVersion"] = 0, ["content"] = new string('\\', 8_000) }).Code);
        Assert.Equal("NotFound", Call(board, "board", "context_get", new() { ["contextId"] = "n" }).Code);
    }

    [Fact]
    public void An_oversized_stored_item_is_read_in_pieces_and_never_blocks_the_items_after_it()
    {
        // A message stored before sends were measured: too large for any page once escaped.
        var session = new StateEnvelope<CoordinationDocument>();
        Assert.True(Call(session, "session", "__open", new() { ["principalId"] = "alice", ["sessionToken"] = "secret", ["sessionId"] = "key" }).Ok);
        var big = new string('\\', 7_500) + "end";
        foreach (var (id, text) in new[] { ("m1", "first"), ("big", big), ("m3", "after") })
            Assert.True(Call(session, "session", "__deliver", new() { ["messageId"] = id, ["text"] = text }).Ok);
        RelayReply Inbox(JsonObject paging) { paging["sessionToken"] = "secret"; return Call(session, "session", "check_inbox", paging); }

        var first = Inbox(new() { ["limit"] = 50 });
        Assert.True(first.Ok, first.Code);
        Assert.Equal(["m1"], Data(first)["items"]!.AsArray().Select(x => x!["messageId"]!.GetValue<string>()));
        Assert.Equal(1, Data(first)["nextCursor"]!.GetValue<int>());

        // The oversized item comes back as its first piece, with its ID, full size and where the next piece starts; paging moves past it.
        var json = new System.Text.StringBuilder();
        long? partOffset = 0;
        var pieces = 0;
        while (partOffset is { } at)
        {
            var paging = new JsonObject { ["cursor"] = 1, ["limit"] = 1 };
            if (at > 0) paging["partOffset"] = at;
            var page = Inbox(paging);
            Assert.True(page.Ok, page.Code);
            var piece = Data(page)["items"]!.AsArray().Single()!.AsObject();
            Assert.True(piece["truncated"]!.GetValue<bool>());
            Assert.Equal("big", piece["messageId"]!.GetValue<string>());
            Assert.Contains("partOffset", piece["hint"]!.GetValue<string>());
            Assert.Equal(at, piece["partOffset"]!.GetValue<long>());
            Assert.True(piece["bytes"]!.GetValue<int>() > RelayLimits.AggregateReplyBytes / 4);
            Assert.Equal(2, Data(page)["nextCursor"]!.GetValue<int>());
            json.Append(piece["part"]!.GetValue<string>());
            partOffset = piece["nextPartOffset"]?.GetValue<long>();
            pieces++;
        }
        Assert.True(pieces > 1);
        Assert.Equal(big, JsonNode.Parse(json.ToString())!["text"]!.GetValue<string>());
        // A page that starts at it with room for more still returns only its first piece, and the next item stays reachable.
        Assert.Single(Data(Inbox(new() { ["cursor"] = 1, ["limit"] = 50 }))["items"]!.AsArray());
        Assert.Equal(["m3"], Data(Inbox(new() { ["cursor"] = 2 }))["items"]!.AsArray().Select(x => x!["messageId"]!.GetValue<string>()));
        // It can be acknowledged like any other message.
        Assert.Equal(1, Data(Call(session, "session", "ack", new() { ["sessionToken"] = "secret", ["requestId"] = "ack-big", ["messageId"] = "big" }))["acknowledged"]!.GetValue<int>());
    }

    [Fact]
    public void The_session_directory_drops_long_stale_sessions_so_new_opens_keep_working()
    {
        // A directory full of sessions opened over time: most closed days ago (some in another repository), a few still in use.
        var sessions = new JsonObject();
        var live = new List<string>();
        for (var i = 0; i < RelayLimits.MaxRecords; i++)
        {
            var id = "s-" + i;
            var seen = i % 40 == 0 ? Now.AddMinutes(-30) : Now.AddHours(-RelayLimits.StaleSessionHours - 2).AddSeconds(-i);
            var record = new JsonObject
            {
                ["sessionId"] = id, ["principalId"] = "alice", ["repositoryId"] = i % 7 == 0 ? "other" : "repo", ["sessionToken"] = "t" + i, ["bindingVersion"] = 0,
                ["observedAt"] = seen.ToString("O"), ["freshUntil"] = seen.AddMinutes(2).ToString("O"), ["availability"] = "unknown"
            };
            if (i % 40 == 0) { record["activeAt"] = seen.ToString("O"); live.Add(id); }
            sessions[id] = record;
        }
        var control = new StateEnvelope<CoordinationDocument> { Value = new() { Json = new JsonObject { ["sessions"] = sessions }.ToJsonString() } };
        var opened = new List<string>();
        var delivered = new List<DurableEffect>();
        for (var i = 0; i < RelayLimits.MaxRecords / 2; i++)
        {
            var open = Call(control, "control", "session_open", new() { ["requestId"] = "open-" + i }, seconds: i);
            Assert.True(open.Ok, i + ": " + open.Code);
            opened.Add(Data(open)["sessionId"]!.GetValue<string>());
            // The outbox delivers between opens.
            delivered.AddRange(control.Effects); control.Effects.Clear();
        }
        var directory = StateJson(control)["sessions"]!.AsObject();
        Assert.InRange(directory.Count, 1, RelayLimits.MaxRecords);
        // Live sessions and every new one are untouched; only sessions not seen for a day were dropped, oldest first.
        Assert.All(live.Concat(opened), id => Assert.True(directory.ContainsKey(id), id));
        Assert.True(directory.ContainsKey("s-1"));
        Assert.False(directory.ContainsKey("s-" + (RelayLimits.MaxRecords - 1)));
        // Each dropped session is told, under its own repository, so its chat gets a clean error and opens again.
        var expiries = delivered.Where(effect => effect.Operation == Operations.SessionExpire).ToList();
        Assert.Equal(RelayLimits.MaxRecords + opened.Count - directory.Count, expiries.Count);
        Assert.All(expiries, effect => Assert.False(directory.ContainsKey(effect.TargetKey)));
        CoordinationStateMachine.StampEffects(delivered, 0, Caller with { Repositories = ["repo"] });
        var other = delivered.First(effect => effect.Operation == Operations.SessionExpire && JsonNode.Parse(effect.PayloadJson)!["repositoryId"]!.GetValue<string>() == "other");
        Assert.Equal(["other"], CoordinationStateMachine.TakeEffectCaller(JsonNode.Parse(other.PayloadJson)!.AsObject()).Repositories);
        // A late registration or activity report from a dropped session is accepted and ignored, so its outbox never retries it forever.
        Assert.True(Call(control, "control", Operations.SessionActive, new() { ["sessionId"] = "s-" + (RelayLimits.MaxRecords - 1), ["activeAt"] = Now.ToString("O") }).Ok);
        Assert.True(Call(control, "control", Operations.Register, new SessionRegistration("repo", "s-" + (RelayLimits.MaxRecords - 1), new SessionPresence
        {
            BindingVersion = 1, ObservedAt = Now, FreshUntil = Now.AddMinutes(2)
        }).ToJson()).Ok);

        // When every session was seen within the day, an open is refused for now and works again once some go stale.
        var busy = new StateEnvelope<CoordinationDocument> { Value = new() { Json = new JsonObject { ["sessions"] = new JsonObject(sessions.Select(x =>
        {
            var copy = x.Value!.DeepClone().AsObject(); copy["activeAt"] = Now.ToString("O");
            return KeyValuePair.Create(x.Key, (JsonNode?)copy);
        })) }.ToJsonString() } };
        Assert.Equal("CapacityExceeded", Call(busy, "control", "session_open", new() { ["requestId"] = "busy" }).Code);
        Assert.True(Call(busy, "control", "session_open", new() { ["requestId"] = "later" }, seconds: (RelayLimits.StaleSessionHours + 1) * 3600).Ok);
    }

    [Fact]
    public void An_expired_session_gets_a_clean_error_and_drops_what_is_sent_to_it()
    {
        var session = new StateEnvelope<CoordinationDocument>();
        Assert.True(Call(session, "session", "__open", new() { ["principalId"] = "alice", ["sessionToken"] = "secret", ["sessionId"] = "s" }).Ok);
        Assert.True(Call(session, "session", Operations.SessionExpire, new SessionExpire("repo", "s", 1).ToJson()).Ok);
        // Repeated expiry, or one for a session this grain never held, is accepted.
        Assert.True(Call(session, "session", Operations.SessionExpire, new SessionExpire("repo", "s", 1).ToJson()).Ok);
        Assert.True(Call(new StateEnvelope<CoordinationDocument>(), "session", Operations.SessionExpire, new SessionExpire("repo", "other", 2).ToJson()).Ok);
        var inbox = Call(session, "session", "check_inbox", new() { ["sessionToken"] = "secret" });
        Assert.Equal("SessionUnknown", inbox.Code);
        Assert.True(Data(inbox)["expired"]!.GetValue<bool>());
        Assert.Equal("session_open", Data(inbox)["reopen"]!.GetValue<string>());
        Assert.Equal("SessionUnknown", Call(session, "session", "__verify", new() { ["sessionToken"] = "secret" }).Code);
        Assert.Equal("SessionDenied", Call(session, "session", "check_inbox", new() { ["sessionToken"] = "secret" }, new("mallory", false, ["repo"], true)).Code);
        // Deliveries are dropped rather than retried forever; attention is refused, which drops it too.
        Assert.True(Data(Call(session, "session", "__deliver", new() { ["messageId"] = "m", ["text"] = "late" }))["expired"]!.GetValue<bool>());
        Assert.Equal("SessionUnknown", Call(session, "session", "__attention_raise", new() { ["kind"] = "info", ["taskId"] = "t" }).Code);
        Assert.DoesNotContain("secret", session.Value.Json);
    }

    private static RelayReply Board(StateEnvelope<CoordinationDocument> state, string op, JsonObject args, double days = 0, int archiveAfterDays = RelayLimits.DefaultArchiveAfterDays)
    {
        args["repositoryId"] = "repo";
        return CoordinationStateMachine.Apply(state, "board", "repo/default", Caller, op, args, Now.AddDays(days), archiveAfterDays: archiveAfterDays);
    }

    [Fact]
    public void A_board_whose_tasks_finish_never_fills_and_a_board_of_open_tasks_says_how_many_are_open()
    {
        // More than three times the task limit over 63 days, completed as they go (every 64th left open), with the daily due sweep
        // archiving what finished more than a day ago: no create is ever refused, and open tasks stay.
        var state = new StateEnvelope<CoordinationDocument>();
        var open = new List<string>();
        var total = 3 * RelayLimits.MaxRecords + 400;
        var perDay = total / 62;
        var midSize = 0;
        for (var i = 0; i < total; i++)
        {
            var id = "t" + i;
            var keepOpen = i % 64 == 0;
            var day = i / perDay;
            var created = Board(state, "task_update", new() { ["taskId"] = id, ["expectedVersion"] = 0, ["workflow"] = DependentWorkflow(keepOpen ? "ready" : "completed") }, days: day, archiveAfterDays: 1);
            Assert.True(created.Ok, i + ": " + created.Code);
            if (keepOpen) open.Add(id);
            state.Effects.Clear(); // the outbox delivers between writes
            if ((i + 1) % perDay == 0) Assert.True(Board(state, Operations.TaskArchive, new(), days: day + 0.5, archiveAfterDays: 1).Ok);
            if (i == 2 * total / 3) midSize = state.Value.Json.Length;
        }
        var stored = StateJson(state);
        var tasks = stored["tasks"]!.AsObject();
        var archived = stored["archivedTasks"]!.AsObject();
        Assert.All(open, id => Assert.True(tasks.ContainsKey(id), id));
        // The board remembers only recent archived IDs: more than three times that bound went through, and its state stayed flat.
        Assert.InRange(state.PendingArchive().Count, 3 * RelayLimits.MaxArchivedIds, total - open.Count);
        Assert.InRange(archived.Count, 1, RelayLimits.MaxArchivedIds);
        Assert.True(stored["archiveFloor"]!.GetValue<long>() > 0);
        Assert.True(state.Value.Json.Length <= midSize * 11 / 10, $"{state.Value.Json.Length} > {midSize}");
        Assert.Equal(total, tasks.Count + state.PendingArchive().Count);
        Assert.All(state.PendingArchive(), task => Assert.Equal("completed", JsonNode.Parse(task.Json)!["workflow"]!["state"]!.GetValue<string>()));

        // With ArchiveAfterDays 0 only the cap archives: a write to a nearly full board moves its oldest finished tasks, even young ones,
        // down to the low-water mark, and the task it names is never one of them.
        var near = new JsonObject();
        for (var i = 0; i < RelayLimits.ArchiveHighWater; i++)
        {
            var finished = i % 4 != 0;
            near["n" + i] = new JsonObject
            {
                ["taskId"] = "n" + i, ["version"] = 1, ["changeRevision"] = i + 1, ["actions"] = new JsonObject(), ["workflow"] = DependentWorkflow(finished ? "completed" : "ready"),
                ["completedAt"] = finished ? Now.AddDays(-1).AddMinutes(-i).ToString("O") : null
            };
            // A finished task someone still holds a live claim on stays.
            if (i % 4 == 1) { near["n" + i]!["owner"] = "s"; near["n" + i]!["leaseUntil"] = Now.AddHours(1).ToString("O"); }
        }
        var capped = new StateEnvelope<CoordinationDocument> { Value = new() { Json = new JsonObject { ["repositoryId"] = "repo", ["taskRevision"] = RelayLimits.ArchiveHighWater, ["tasks"] = near }.ToJsonString() } };
        var oldest = "n" + (RelayLimits.ArchiveHighWater - 1);
        Assert.True(Board(capped, "task_update", new() { ["taskId"] = oldest, ["expectedVersion"] = 1, ["patch"] = new JsonObject { ["x"] = 1 } }, archiveAfterDays: 0).Ok);
        var left = StateJson(capped)["tasks"]!.AsObject();
        Assert.Equal(RelayLimits.ArchiveLowWater, left.Count);
        Assert.True(left.ContainsKey(oldest));
        Assert.True(left.ContainsKey("n1"));
        Assert.False(left.ContainsKey("n" + (RelayLimits.ArchiveHighWater - 2)));
        Assert.All(Enumerable.Range(0, RelayLimits.ArchiveHighWater).Where(i => i % 4 is 0 or 1), i => Assert.True(left.ContainsKey("n" + i)));
        // Each archived task's open attention items are closed, in batches, with the archive's revision.
        var closes = capped.Effects.Select(effect => JsonNode.Parse(effect.PayloadJson)!.AsObject()).Where(payload => payload["operation"]?.GetValue<string>() == Operations.AttentionClose).ToList();
        Assert.Equal(RelayLimits.ArchiveHighWater - RelayLimits.ArchiveLowWater, closes.Sum(payload => payload["item"]!["taskIds"]!.AsArray().Count));
        Assert.All(closes, payload => Assert.Equal(StateJson(capped)["taskRevision"]!.GetValue<long>() - 1, payload["item"]!["revision"]!.GetValue<long>()));
        // Near the cap a task is still archived no sooner than a day after it completed.
        var fresh = new JsonObject();
        for (var i = 0; i < RelayLimits.ArchiveHighWater; i++)
            fresh["f" + i] = new JsonObject { ["taskId"] = "f" + i, ["version"] = 1, ["changeRevision"] = i + 1, ["actions"] = new JsonObject(), ["workflow"] = DependentWorkflow("completed"), ["completedAt"] = Now.AddHours(-1).ToString("O") };
        var young = new StateEnvelope<CoordinationDocument> { Value = new() { Json = new JsonObject { ["repositoryId"] = "repo", ["taskRevision"] = RelayLimits.ArchiveHighWater, ["tasks"] = fresh }.ToJsonString() } };
        Assert.True(Board(young, "context_put", new() { ["contextId"] = "n", ["expectedVersion"] = 0, ["content"] = "x" }, archiveAfterDays: 0).Ok);
        Assert.Equal(RelayLimits.ArchiveHighWater, StateJson(young)["tasks"]!.AsObject().Count);

        // A board whose every task is still open cannot archive anything: the create is refused and says why.
        var full = new JsonObject();
        for (var i = 0; i < RelayLimits.MaxRecords; i++) full["o" + i] = new JsonObject { ["taskId"] = "o" + i, ["version"] = 1, ["changeRevision"] = i + 1, ["actions"] = new JsonObject(), ["workflow"] = DependentWorkflow("ready") };
        var busy = new StateEnvelope<CoordinationDocument> { Value = new() { Json = new JsonObject { ["repositoryId"] = "repo", ["taskRevision"] = RelayLimits.MaxRecords, ["tasks"] = full }.ToJsonString() } };
        var refused = Board(busy, "task_update", new() { ["taskId"] = "more", ["expectedVersion"] = 0 });
        Assert.Equal("CapacityExceeded", refused.Code);
        Assert.Equal((RelayLimits.MaxRecords, RelayLimits.MaxRecords, 0, 0), (Data(refused)["maxTasks"]!.GetValue<int>(), Data(refused)["openTasks"]!.GetValue<int>(), Data(refused)["finishedTasks"]!.GetValue<int>(), Data(refused)["archivable"]!.GetValue<int>()));
    }

    [Fact]
    public void Archived_tasks_count_as_completed_refuse_writes_and_leave_deltas_as_removals()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        Assert.True(Board(state, "task_update", new() { ["taskId"] = "a", ["expectedVersion"] = 0, ["workflow"] = DependentWorkflow("ready") }).Ok);
        Assert.True(Board(state, "task_update", new() { ["taskId"] = "b", ["expectedVersion"] = 0, ["workflow"] = DependentWorkflow("ready", "a") }).Ok);
        // A finished task whose claim lapsed is archived like any other. (Completion already needs every action resolved.)
        Assert.True(Board(state, "task_update", new() { ["taskId"] = "claimed", ["expectedVersion"] = 0, ["workflow"] = DependentWorkflow("ready") }).Ok);
        Assert.True(Board(state, "task_claim", new() { ["taskId"] = "claimed", ["sessionId"] = "s", ["expectedVersion"] = 1, ["leaseSeconds"] = RelayLimits.MaxLeaseSeconds }).Ok);
        Assert.True(Board(state, "task_update", new() { ["taskId"] = "claimed", ["sessionId"] = "s", ["expectedVersion"] = 2, ["workflow"] = DependentWorkflow("completed") }).Ok);
        Assert.True(Board(state, "task_update", new() { ["taskId"] = "a", ["expectedVersion"] = 1, ["workflow"] = DependentWorkflow("completed") }).Ok);
        var checkpoint = StateJson(state)["taskRevision"]!.GetValue<long>();
        // The next sweep is due when a finished task passes the setting.
        CoordinationStateMachine.ScheduleLeases(state, RelayLimits.DefaultArchiveAfterDays);
        var due = Assert.Single(state.DueWork, work => work.Kind == DueWorkKinds.TaskArchive);
        Assert.Equal(Now.AddDays(RelayLimits.DefaultArchiveAfterDays), due.DueAt);

        // Before the setting nothing moves; after it, only a sits idle and finished.
        Assert.Equal(0, Data(Board(state, Operations.TaskArchive, new(), days: RelayLimits.DefaultArchiveAfterDays - 1))["archived"]!.GetValue<int>());
        Assert.Equal(2, Data(Board(state, Operations.TaskArchive, new(), days: RelayLimits.DefaultArchiveAfterDays + 1))["archived"]!.GetValue<int>());
        var stored = StateJson(state);
        Assert.False(stored["tasks"]!.AsObject().ContainsKey("a"));
        Assert.False(stored["tasks"]!.AsObject().ContainsKey("claimed"));
        var archived = Assert.Single(state.PendingArchive(), task => task.TaskId == "a");
        Assert.Equal(("a", Now), (archived.TaskId, archived.CompletedAt));
        Assert.Equal(Now.AddDays(RelayLimits.DefaultArchiveAfterDays + 1), JsonFields.Date(JsonNode.Parse(archived.Json)!.AsObject(), "archivedAt"));

        // Writes to it are refused as a conflict; the hub reads it from the archive.
        foreach (var (op, extra) in new (string, JsonObject)[]
        {
            ("task_update", new() { ["expectedVersion"] = 2, ["patch"] = new JsonObject { ["x"] = 1 } }), ("task_claim", new() { ["sessionId"] = "s", ["expectedVersion"] = 2 }),
            ("action_begin", new() { ["sessionId"] = "s", ["expectedVersion"] = 2, ["actionId"] = "x", ["inputFingerprint"] = "f" }), ("task_get", new()),
            ("task_update", new() { ["expectedVersion"] = 0 })
        })
        {
            extra["taskId"] = "a";
            var refused = Board(state, op, extra, days: 31);
            Assert.Equal("TaskArchived", refused.Code);
            Assert.Equal(RelayErrorKind.Conflict, RelayErrorCodes.Classify(refused.Code));
        }
        // It is gone from the list; a delta from before the sweep reports it removed, one from after does not.
        Assert.DoesNotContain(Data(Board(state, "task_get", new(), days: 31))["items"]!.AsArray(), item => item!["taskId"]!.GetValue<string>() == "a");
        var delta = Data(Board(state, "task_get", new() { ["sinceRevision"] = checkpoint }, days: 31))["items"]!.AsArray();
        Assert.Equal(["a", "claimed"], delta.Select(item => item!["taskId"]!.GetValue<string>()));
        Assert.All(delta, item => Assert.True(item!["archived"]!.GetValue<bool>()));
        Assert.Empty(Data(Board(state, "task_get", new() { ["sinceRevision"] = StateJson(state)["taskRevision"]!.GetValue<long>() }, days: 31))["items"]!.AsArray());

        // A dependency on it is satisfied: b is offered and claimable, and a new task may depend on it.
        Assert.Equal("b", Data(Board(state, "__next_work", new() { ["sessionId"] = "w" }, days: 31))["offer"]!["taskId"]!.GetValue<string>());
        Assert.True(Board(state, "task_claim", new() { ["taskId"] = "b", ["sessionId"] = "w", ["expectedVersion"] = 1 }, days: 31).Ok);
        Assert.True(Board(state, "task_update", new() { ["taskId"] = "later", ["expectedVersion"] = 0, ["workflow"] = DependentWorkflow("ready", "a") }, days: 31).Ok);
        Assert.Equal("DependencyUnknown", Board(state, "task_update", new() { ["taskId"] = "never", ["expectedVersion"] = 0, ["workflow"] = DependentWorkflow("ready", "nowhere") }, days: 31).Code);
        // Reopening a completed task clears its completion time.
        Assert.True(Board(state, "task_update", new() { ["taskId"] = "later", ["expectedVersion"] = 1, ["workflow"] = DependentWorkflow("completed", "a") }, days: 31).Ok);
        Assert.NotNull(StateJson(state)["tasks"]!["later"]!["completedAt"]);
        Assert.True(Board(state, "task_update", new() { ["taskId"] = "later", ["expectedVersion"] = 2, ["workflow"] = DependentWorkflow("ready", "a") }, days: 31).Ok);
        Assert.Null(StateJson(state)["tasks"]!["later"]!["completedAt"]);
    }

    [Fact]
    public void A_session_that_keeps_calling_is_never_dropped_from_a_full_directory_even_without_attention()
    {
        // A chat that only makes verified calls (attention off, or attention: false): no offer ever runs. Each hourly verify still reports it.
        var session = new StateEnvelope<CoordinationDocument>();
        Assert.True(Call(session, "session", "__open", new() { ["principalId"] = "alice", ["sessionToken"] = "secret", ["sessionId"] = "busy" }).Ok);
        var sessions = new JsonObject
        {
            ["busy"] = new JsonObject { ["sessionId"] = "busy", ["principalId"] = "alice", ["repositoryId"] = "repo", ["observedAt"] = Now.ToString("O"), ["freshUntil"] = Now.AddMinutes(2).ToString("O") }
        };
        for (var i = 1; i < RelayLimits.MaxRecords; i++)
            sessions["old-" + i] = new JsonObject { ["sessionId"] = "old-" + i, ["principalId"] = "alice", ["repositoryId"] = "repo", ["observedAt"] = Now.AddDays(-3).ToString("O"), ["freshUntil"] = Now.AddDays(-3).ToString("O") };
        var control = new StateEnvelope<CoordinationDocument> { Value = new() { Json = new JsonObject { ["sessions"] = sessions }.ToJsonString() } };
        for (var hour = 0; hour <= 30; hour++)
        {
            Assert.True(Call(session, "session", "__verify", new() { ["sessionToken"] = "secret" }, seconds: hour * 3600).Ok);
            foreach (var effect in session.Effects.Where(effect => effect.Operation == Operations.SessionActive))
                Assert.True(Call(control, "control", effect.Operation, JsonNode.Parse(effect.PayloadJson)!.AsObject(), seconds: hour * 3600).Ok);
            session.Effects.Clear();
        }
        Assert.Equal(Now.AddHours(30).ToString("O"), StateJson(control)["sessions"]!["busy"]!["activeAt"]!.GetValue<string>());
        // Thirty hours on, the directory is full and drops stale sessions; this one was seen within the hour.
        Assert.True(Call(control, "control", "session_open", new() { ["requestId"] = "new" }, seconds: 30 * 3600 + 60).Ok);
        Assert.True(StateJson(control)["sessions"]!.AsObject().ContainsKey("busy"));
        Assert.False(StateJson(control)["sessions"]!.AsObject().ContainsKey("old-1"));
    }

    [Fact]
    public void A_delta_from_zero_pages_every_live_task_and_one_older_than_the_archive_floor_restarts()
    {
        // 1,500 live tasks and the board's full memory of 2,048 archived IDs: more than the cursor ever pages through at once.
        var tasks = new JsonObject();
        for (var i = 0; i < 1_500; i++) tasks["live-" + i] = new JsonObject { ["taskId"] = "live-" + i, ["version"] = 1, ["changeRevision"] = 3_000 + i, ["actions"] = new JsonObject() };
        var archived = new JsonObject();
        for (var i = 0; i < RelayLimits.MaxArchivedIds; i++) archived["gone-" + i] = new JsonArray(2_000 + i % 900, Now.ToUnixTimeSeconds());
        var state = new StateEnvelope<CoordinationDocument> { Value = new() { Json = new JsonObject { ["repositoryId"] = "repo", ["taskRevision"] = 5_000, ["tasks"] = tasks, ["archivedTasks"] = archived, ["archiveFloor"] = 1_000 }.ToJsonString() } };
        List<JsonObject> All(long since)
        {
            var items = new List<JsonObject>();
            long? cursor = 0;
            while (cursor is { } at)
            {
                var args = new JsonObject { ["sinceRevision"] = since, ["limit"] = RelayLimits.MaxPageSize };
                if (at > 0) { args["cursor"] = at; args["snapshotRevision"] = 5_000; }
                var page = Call(state, "board", "task_get", args);
                Assert.True(page.Ok, page.Code);
                items.AddRange(Data(page)["items"]!.AsArray().Select(item => item!.AsObject()));
                cursor = Data(page)["nextCursor"]?.GetValue<long>();
            }
            return items;
        }
        // A reader from 0 gets the live tasks only, every one of them.
        var fromZero = All(0);
        Assert.Equal(1_500, fromZero.Count);
        Assert.DoesNotContain(fromZero, item => item["archived"] is not null);
        // A checkpoint at the floor gets every live change and every remembered removal, past what one cursor range covered before.
        var fromFloor = All(1_000);
        Assert.Equal(1_500 + RelayLimits.MaxArchivedIds, fromFloor.Count);
        // One older than the floor cannot learn every removal: it restarts from 0.
        var stale = Call(state, "board", "task_get", new() { ["sinceRevision"] = 999 });
        Assert.Equal("DeltaRestartRequired", stale.Code);
        Assert.Equal((0L, 1_000L), (Data(stale)["sinceRevision"]!.GetValue<long>(), Data(stale)["archiveFloor"]!.GetValue<long>()));
    }

    [Fact]
    public void An_archived_dependency_the_board_forgot_counts_as_completed_once_the_hub_names_it()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        // Without the hub's lookup, an ID the board no longer knows is unknown.
        Assert.Equal("DependencyUnknown", Board(state, "task_update", new() { ["taskId"] = "b", ["expectedVersion"] = 0, ["workflow"] = DependentWorkflow("ready", "long-gone") }).Code);
        // The hub found it in the archive table: it is remembered, and counts as completed for offers and claims.
        Assert.True(Board(state, "task_update", new() { ["taskId"] = "b", ["expectedVersion"] = 0, ["workflow"] = DependentWorkflow("ready", "long-gone"), ["archivedDependsOn"] = new JsonArray("long-gone", "unrelated") }).Ok);
        var remembered = StateJson(state)["archivedTasks"]!.AsObject();
        Assert.Equal(["long-gone"], remembered.Select(x => x.Key));
        Assert.Equal("b", Data(Board(state, "__next_work", new() { ["sessionId"] = "w" }))["offer"]!["taskId"]!.GetValue<string>());
        Assert.True(Board(state, "task_claim", new() { ["taskId"] = "b", ["sessionId"] = "w", ["expectedVersion"] = 1 }).Ok);
        // While b depends on it, the sweep never forgets it, however old; no delta lists it as a removal.
        Assert.True(Board(state, Operations.TaskArchive, new(), days: RelayLimits.ArchivedIdDays + 5).Ok);
        Assert.True(StateJson(state)["archivedTasks"]!.AsObject().ContainsKey("long-gone"));
        Assert.DoesNotContain(Data(Board(state, "task_get", new() { ["sinceRevision"] = 1 }, days: 40))["items"]!.AsArray(), item => item!["taskId"]!.GetValue<string>() == "long-gone");

        // A task archived here that a live task depends on: after 30 days it leaves delta memory (the floor rises past it), so no delta
        // lists it, but it stays remembered as completed for its dependent.
        Assert.True(Board(state, "task_update", new() { ["taskId"] = "a", ["expectedVersion"] = 0, ["workflow"] = DependentWorkflow("completed") }, days: 40).Ok);
        Assert.True(Board(state, "task_update", new() { ["taskId"] = "c", ["expectedVersion"] = 0, ["workflow"] = DependentWorkflow("ready", "a") }, days: 40).Ok);
        Assert.Equal(1, Data(Board(state, Operations.TaskArchive, new(), days: 71))["archived"]!.GetValue<int>());
        var revision = StateJson(state)["archivedTasks"]!["a"]![0]!.GetValue<long>();
        Assert.True(revision > 0);
        Assert.True(Board(state, Operations.TaskArchive, new(), days: 102).Ok);
        var after = StateJson(state);
        Assert.Equal(0, after["archivedTasks"]!["a"]![0]!.GetValue<long>());
        Assert.Equal(revision, after["archiveFloor"]!.GetValue<long>());
        Assert.DoesNotContain(Data(Board(state, "task_get", new() { ["sinceRevision"] = revision }, days: 102))["items"]!.AsArray(), item => item!["archived"] is not null);
        Assert.True(Board(state, "task_claim", new() { ["taskId"] = "c", ["sessionId"] = "w", ["expectedVersion"] = 1 }, days: 102).Ok);
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
        // The task still waiting on b gets nothing yet (beyond the board's request, when it was targeted, to hear if worker-c closes).
        Assert.DoesNotContain(state.Effects, effect => effect.TargetKey == "worker-c" && effect.Operation != Operations.SessionTargetWatch);
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

    [Fact]
    public void Claims_and_action_starts_wait_for_every_dependency_as_offers_do()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        Assert.True(WriteWorkflow(state, "a", 0, DependentWorkflow("ready")).Ok);
        Assert.True(WriteWorkflow(state, "b", 0, DependentWorkflow("ready", "a")).Ok);
        RelayReply Claim(string requestId, long version) => Call(state, "board", "task_claim", new() { ["taskId"] = "b", ["sessionId"] = "worker", ["requestId"] = requestId, ["expectedVersion"] = version, ["leaseSeconds"] = RelayLimits.MaxLeaseSeconds });
        RelayReply Begin(long version) => Call(state, "board", "action_begin", new() { ["taskId"] = "b", ["sessionId"] = "worker", ["requestId"] = Guid.NewGuid().ToString("N"), ["expectedVersion"] = version, ["actionId"] = "deploy", ["inputFingerprint"] = "f" });
        string[] Unfinished(RelayReply reply) { Assert.Equal("DependenciesIncomplete", reply.Code); return Data(reply)["dependsOn"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray(); }

        var before = state.Value.Json;
        var refused = Claim("early", 1);
        Assert.Equal(["a"], Unfinished(refused));
        Assert.Equal(1, refused.Version);
        Assert.Equal(RelayErrorKind.Conflict, RelayErrorCodes.Classify(refused.Code));
        Assert.Equal(before, state.Value.Json);

        Assert.True(WriteWorkflow(state, "a", 1, DependentWorkflow("completed")).Ok);
        var claimed = Claim("claim", 1);
        Assert.True(claimed.Ok, claimed.Code);
        // a is reopened: b keeps its claim, but its retried claim and a new action start are refused until a completes again.
        Assert.True(WriteWorkflow(state, "a", 2, DependentWorkflow("ready")).Ok);
        Assert.Equal(["a"], Unfinished(Claim("claim", 1)));
        Assert.Equal(["a"], Unfinished(Begin(2)));
        Assert.True(WriteWorkflow(state, "a", 3, DependentWorkflow("completed")).Ok);
        Assert.Equal(claimed, Claim("claim", 1));
        var begun = Begin(2);
        Assert.True(begun.Ok, begun.Code);
    }

    [Fact]
    public void Claims_read_unknown_and_legacy_dependsOn_exactly_as_offers_do()
    {
        // A string-array dependsOn naming a task not on the board is unfinished; any other shape reads as no dependencies, so it never throws.
        var tasks = new JsonObject();
        foreach (var (id, dependsOn) in new (string, JsonNode?)[] { ("unknown", new JsonArray("gone")), ("string", JsonValue.Create("gone")), ("numbers", new JsonArray(1, 2)), ("objects", new JsonArray(new JsonObject { ["id"] = "gone" })) })
        {
            var workflow = DependentWorkflow("ready"); workflow["dependsOn"] = dependsOn;
            tasks[id] = new JsonObject { ["taskId"] = id, ["version"] = 1, ["changeRevision"] = tasks.Count + 1, ["actions"] = new JsonObject(), ["workflow"] = workflow };
        }
        var state = new StateEnvelope<CoordinationDocument> { Value = new() { Json = new JsonObject { ["repositoryId"] = "repo", ["taskRevision"] = tasks.Count, ["tasks"] = tasks }.ToJsonString() } };
        // Offers skip all four: the unknown ID is unfinished, and the legacy shapes fail workflow validation.
        Assert.Empty(Data(Call(state, "board", "__next_work", new() { ["sessionId"] = "worker", ["limit"] = RelayLimits.NextWorkCandidates }))["candidates"]!.AsArray());
        RelayReply Claim(string id) => Call(state, "board", "task_claim", new() { ["taskId"] = id, ["sessionId"] = "worker", ["requestId"] = "claim-" + id, ["expectedVersion"] = 1 });
        var unknown = Claim("unknown");
        Assert.Equal("DependenciesIncomplete", unknown.Code);
        Assert.Equal(["gone"], Data(unknown)["dependsOn"]!.AsArray().Select(x => x!.GetValue<string>()));
        foreach (var legacy in new[] { "string", "numbers", "objects" }) Assert.True(Claim(legacy).Ok, legacy);
    }

    [Fact]
    public void Archiving_closes_every_open_item_of_its_tasks_in_one_batch()
    {
        var session = AttentionSession();
        Assert.True(RaiseAttention(session, "review_ready", "t1").Ok);
        Assert.True(RaiseAttention(session, "failed", "t2").Ok);
        Assert.True(RaiseAttention(session, "next_work", "t3").Ok);
        var closed = Call(session, "session", "__attention_close", new() { ["taskIds"] = new JsonArray("t1", "t2"), ["revision"] = 9 });
        Assert.Equal(2, Data(closed)["closed"]!.GetValue<int>());
        Assert.Equal(["t3"], AttentionItems(session).Select(item => item.Value!["taskId"]!.GetValue<string>()));
        // A late raise from before the archive, with an older revision, stays closed.
        Assert.True(RaiseAttention(session, "review_ready", "t1", new() { ["revision"] = 5 }).Ok);
        Assert.Equal(["t3"], AttentionItems(session).Select(item => item.Value!["taskId"]!.GetValue<string>()));
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

    /// <summary>
    /// The attention effects a board write added, read as (target, operation, payload); a manager-bound item is unwrapped from its route.
    /// A target's request to hear when it closes is not attention, so it is left out.
    /// </summary>
    private static List<(string Target, string Operation, JsonObject Item)> NewEffects(StateEnvelope<CoordinationDocument> state, ref int seen)
    {
        var added = state.Effects.Skip(seen).Where(effect => effect.Operation != Operations.SessionTargetWatch).Select(effect =>
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
        Assert.True(WriteWorkflow(state, "b", 0, DependentWorkflow("ready"), "worker-b").Ok);
        Assert.Empty(NewEffects(state, ref seen));
        // b's worker claims it; a's claim by another worker means a blocks someone else's claimed task. A claim needs every dependency
        // completed, so b takes its dependency on a after it is claimed.
        Assert.True(Call(state, "board", "task_claim", new() { ["taskId"] = "b", ["sessionId"] = "worker-b", ["requestId"] = "claim-b", ["expectedVersion"] = 1 }).Ok);
        var claimed = NewEffects(state, ref seen);
        Assert.Equal(("worker-b", "__attention_close", "next_work"), (claimed.Single().Target, claimed.Single().Operation, claimed.Single().Item["kinds"]![0]!.GetValue<string>()));
        var dependent = DependentWorkflow("ready", "a"); dependent["targetSessionId"] = "worker-b";
        Assert.True(Call(state, "board", "task_update", new() { ["taskId"] = "b", ["sessionId"] = "worker-b", ["requestId"] = "depend-b", ["expectedVersion"] = 2, ["workflow"] = dependent }).Ok);
        Assert.Empty(NewEffects(state, ref seen));
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
        Assert.True(Call(control, "control", Operations.SessionActive, new() { ["sessionId"] = active, ["activeAt"] = Now.AddHours(-1).ToString("O") }).Ok);
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
        Assert.True(Lock("__block", new() { ["actionId"] = "print", ["actionKey"] = "print/1", ["sessionId"] = "holder" }).Ok);
        Assert.True(Data(Lock("__order_release", release.DeepClone().AsObject()))["failed"]!.GetValue<bool>());
        var back = Assert.Single(NewEffects(printer, ref lockSeen));
        Assert.Equal(("repo/default", "__order_failed", "o-9", 7L), (back.Target, back.Operation, back.Item["orderId"]!.GetValue<string>(), back.Item["run"]!.GetValue<long>()));
        Assert.True(Data(Lock("__order_release", release.DeepClone().AsObject()))["repeated"]!.GetValue<bool>());
        Assert.Empty(NewEffects(printer, ref lockSeen));
        Assert.True(Lock("__unblock", new() { ["actionId"] = "print", ["actionKey"] = "print/1", ["sessionId"] = "holder" }).Ok);
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
        Assert.False(Data(OrderCall(board, "board", "__order_failed", new OrderFailed("repo", AggregateKinds.DefaultBoard, "o-1", 1, "late").ToJson()))["counted"]!.GetValue<bool>());

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

    private static JsonObject Payload(DurableEffect effect) => JsonNode.Parse(effect.PayloadJson)!.AsObject();

    private static JsonObject StaleRecord(string id, DateTimeOffset seen) => new()
    {
        ["sessionId"] = id, ["principalId"] = "alice", ["repositoryId"] = "repo", ["observedAt"] = seen.ToString("O"), ["freshUntil"] = seen.ToString("O")
    };

    /// <summary>A hub whose directory holds <paramref name="others"/> sessions besides those a test opens, each last seen at <paramref name="seen"/>.</summary>
    private static AggregateHub HubWithDirectory(int others, DateTimeOffset seen)
    {
        var hub = new AggregateHub();
        var sessions = new JsonObject();
        for (var i = 1; i <= others; i++) sessions["old-" + i] = StaleRecord("old-" + i, seen);
        hub.Of(AggregateKinds.Control, AggregateKinds.ControlKey).Value.Json = new JsonObject { ["sessions"] = sessions }.ToJsonString();
        return hub;
    }

    /// <summary>Task t on the hub's board claimed by <paramref name="session"/>, with action deploy begun at <paramref name="at"/>; nothing is delivered.</summary>
    private static void BeginDeploy(AggregateHub hub, RelayCaller worker, string session, DateTimeOffset at)
    {
        Assert.True(hub.Board("task_update", new() { ["taskId"] = "t", ["expectedVersion"] = 0 }, at, worker).Ok);
        Assert.True(hub.Board("task_claim", new() { ["taskId"] = "t", ["sessionId"] = session, ["expectedVersion"] = 1 }, at, worker).Ok);
        Assert.True(hub.Board("action_begin", new() { ["taskId"] = "t", ["sessionId"] = session, ["expectedVersion"] = 2, ["actionId"] = "deploy", ["inputFingerprint"] = "f" }, at, worker).Ok);
    }

    private static RelayReply ReportDeploy(AggregateHub hub, RelayCaller worker, string session, DateTimeOffset at) =>
        hub.Board("action_report", new() { ["taskId"] = "t", ["sessionId"] = session, ["expectedVersion"] = 3, ["actionId"] = "deploy", ["attempt"] = 1, ["outcome"] = "completed" }, at, worker);

    /// <summary>The board's queued report that <paramref name="session"/> starts or stops answering for an action.</summary>
    private static DurableEffect WorkReport(AggregateHub hub, string session) =>
        Assert.Single(hub.Queued, effect => effect.Operation == Operations.SessionWork && effect.TargetKind == AggregateKinds.Session && effect.TargetKey == session);

    /// <summary>A board's report to the session <paramref name="session"/> that it starts or stops answering for action <paramref name="action"/>.</summary>
    private static JsonObject ActionWork(string session, string action, bool open, DateTimeOffset at) =>
        new ActionWorkReport("repo", session, AggregateKinds.DefaultBoard, "t", action, 1, open, at).ToJson();

    [Fact]
    public void A_full_directory_never_expires_a_session_that_owns_an_unresolved_action()
    {
        var worker = Caller with { IsAdministrator = false };
        // A directory one short of full; every other session was last seen an hour after A, and all of them go stale.
        var hub = HubWithDirectory(RelayLimits.MaxRecords - 1, Now.AddHours(1));
        var a = hub.Open("a", Now).Id;
        hub.DeliverAll(Now);
        // A claims a task and begins an action; the board tells A's session, which tells the directory that A answers for it.
        BeginDeploy(hub, worker, a, Now);
        Assert.True(Payload(WorkReport(hub, a))["open"]!.GetValue<bool>());
        hub.DeliverAll(Now);
        Assert.NotNull(hub.Directory()[a]!["ownsWork"]);

        // A day and more later A has gone quiet with its action still invoking. A new open fills the directory and drops the least
        // recently seen stale sessions; A is the least recently seen, and it stays.
        var later = Now.AddHours(RelayLimits.StaleSessionHours + 6);
        Assert.True(hub.Apply(AggregateKinds.Control, AggregateKinds.ControlKey, "session_open", new() { ["requestId"] = "open-b" }, worker, later).Ok);
        var directory = hub.Directory();
        Assert.True(directory.ContainsKey(a));
        Assert.Equal(RelayLimits.RetainedEntriesAfterPrune + 1, directory.Count);
        Assert.DoesNotContain(hub.Queued, effect => effect.Operation == Operations.SessionExpire && effect.TargetKey == a);
        Assert.DoesNotContain("ownsWork", hub.Apply(AggregateKinds.Control, AggregateKinds.ControlKey, "worker_list", new(), worker, later).DataJson);
        hub.DeliverAll(later);
        // So A can still report it. Once it is resolved, the directory may drop A like any other stale session.
        Assert.True(ReportDeploy(hub, worker, a, later).Ok);
        Assert.False(Payload(WorkReport(hub, a))["open"]!.GetValue<bool>());
        hub.DeliverAll(later);
        Assert.Null(hub.Directory()[a]!["ownsWork"]);

        // A session's ledger ignores a close for an action it never saw open (one begun before it tracked actions), so that close cannot
        // lift the exemption of an action that is still open.
        var counted = new AggregateHub();
        var s = counted.Open("s", Now).Id;
        JsonObject Report(int i, bool open, int seconds = 0) => Data(counted.Apply(AggregateKinds.Session, s, Operations.SessionWork, ActionWork(s, "k" + i, open, Now.AddSeconds(seconds)),
            AggregateHub.BoardCaller, Now.AddSeconds(seconds)));
        JsonNode? Ledger() => counted.State(AggregateKinds.Session, s)["work"];
        Assert.True(Report(0, true)["owns"]!.GetValue<bool>());
        var legacy = Data(counted.Apply(AggregateKinds.Session, s, Operations.SessionWork, ActionWork(s, "begun-before-tracking", false, Now), AggregateHub.BoardCaller, Now));
        Assert.Equal((false, true), (legacy["tracked"]!.GetValue<bool>(), legacy["owns"]!.GetValue<bool>()));
        Assert.False(Report(0, false)["owns"]!.GetValue<bool>());
        Assert.Null(Ledger()!["openActions"]);
        // A late open for an action already closed (reordered delivery) is ignored.
        Assert.False(Report(0, true)["tracked"]!.GetValue<bool>());
        Assert.Null(Ledger()!["openActions"]);
        // The ledger is bounded: past the limit the session is marked, and the mark lapses a week after the last overflow.
        for (var i = 1; i <= RelayLimits.MaxSessionOpenActions + 1; i++) Assert.True(Report(i, true)["owns"]!.GetValue<bool>());
        Assert.Equal((RelayLimits.MaxSessionOpenActions, Now.ToUnixTimeSeconds()), (Ledger()!["openActions"]!.AsObject().Count, Ledger()!["openActionsOverflow"]!.GetValue<long>()));
        Assert.False(Ledger()!["openActions"]!.AsObject().ContainsKey("k1"));
        for (var i = 1; i <= RelayLimits.MaxSessionOpenActions + 1; i++) Report(i, false);
        Assert.Null(Ledger()!["openActions"]);
        Assert.NotNull(Ledger()!["openActionsOverflow"]);
        Assert.False(Report(999, false, (RelayLimits.SessionWorkRetentionDays * 24 + 1) * 3600)["owns"]!.GetValue<bool>());
        Assert.Null(Ledger()?["openActionsOverflow"]);
        // A report to a session never opened here is accepted and ignored.
        Assert.False(Data(counted.Apply(AggregateKinds.Session, "gone", Operations.SessionWork, ActionWork("gone", "k", true, Now), AggregateHub.BoardCaller, Now))["tracked"]!.GetValue<bool>());

        // When stale sessions that own unresolved actions alone fill the directory, an open is refused, says why, and expires nobody.
        var held = new JsonObject();
        for (var i = 0; i < RelayLimits.MaxRecords; i++)
        {
            var heldRecord = StaleRecord("h-" + i, Now.AddDays(-3)); heldRecord["ownsWork"] = Now.ToUnixTimeSeconds();
            held["h-" + i] = heldRecord;
        }
        var full = new StateEnvelope<CoordinationDocument> { Value = new() { Json = new JsonObject { ["sessions"] = held }.ToJsonString() } };
        var refused = Call(full, "control", "session_open", new() { ["requestId"] = "full" }, worker);
        Assert.Equal("CapacityExceeded", refused.Code);
        Assert.Equal(RelayLimits.MaxRecords, Data(refused)["heldByUnresolvedActions"]!.GetValue<int>());
        Assert.Contains("recover", Data(refused)["hint"]!.GetValue<string>());
        Assert.Empty(full.Effects);
        Assert.Equal(RelayLimits.MaxRecords, StateJson(full)["sessions"]!.AsObject().Count);
    }

    [Fact]
    public void A_late_open_after_its_close_and_a_forgotten_open_never_keep_a_session_from_expiring()
    {
        var worker = Caller with { IsAdministrator = false };
        // One short of full, every other session last seen an hour after the one under test.
        var hub = HubWithDirectory(RelayLimits.MaxRecords - 1, Now.AddHours(1));
        var a = hub.Open("a", Now).Id;
        hub.DeliverAll(Now);
        // A begins and finishes an action. The open's delivery to A's session fails, the close is delivered, and then the open's retry arrives.
        BeginDeploy(hub, worker, a, Now);
        var open = WorkReport(hub, a);
        Assert.True(ReportDeploy(hub, worker, a, Now.AddSeconds(60)).Ok);
        var close = Assert.Single(hub.Queued, effect => effect.Operation == Operations.SessionWork && effect.Id != open.Id);
        Assert.True(hub.Deliver(close, Now.AddSeconds(70)).Ok);
        var late = hub.Deliver(open, Now.AddSeconds(80));
        Assert.True(late.Ok);
        Assert.False(Data(late)["tracked"]!.GetValue<bool>());
        hub.DeliverAll(Now.AddSeconds(80));
        Assert.Null(hub.State(AggregateKinds.Session, a)["work"]!["openActions"]);
        Assert.Null(hub.Directory()[a]!["ownsWork"]);
        // So A is not protected: a full directory a day later drops it with the other stale sessions.
        Assert.True(hub.Apply(AggregateKinds.Control, AggregateKinds.ControlKey, "session_open", new() { ["requestId"] = "open" }, worker, Now.AddHours(RelayLimits.StaleSessionHours + 6)).Ok);
        Assert.False(hub.Directory().ContainsKey(a));
        Assert.Contains(hub.Queued, effect => effect.Operation == Operations.SessionExpire && effect.TargetKey == a);

        // An open whose close never arrives protects its session only for SessionWorkRetentionDays without a report.
        AggregateHub Forgotten(out string b)
        {
            var forgotten = HubWithDirectory(RelayLimits.MaxRecords - 1, Now.AddHours(1));
            b = forgotten.Open("b", Now).Id;
            Assert.True(forgotten.Apply(AggregateKinds.Session, b, Operations.SessionWork, ActionWork(b, "lost", true, Now), AggregateHub.BoardCaller, Now).Ok);
            forgotten.DeliverAll(Now);
            return forgotten;
        }
        var early = Forgotten(out var b);
        Assert.True(early.Apply(AggregateKinds.Control, AggregateKinds.ControlKey, "session_open", new() { ["requestId"] = "early" }, worker, Now.AddHours(RelayLimits.StaleSessionHours + 6)).Ok);
        Assert.True(early.Directory().ContainsKey(b));
        var week = Forgotten(out var c);
        Assert.True(week.Apply(AggregateKinds.Control, AggregateKinds.ControlKey, "session_open", new() { ["requestId"] = "late" }, worker, Now.AddHours(RelayLimits.SessionWorkRetentionDays * 24 + 1)).Ok);
        Assert.False(week.Directory().ContainsKey(c));
        // An open delivered later than the window is ignored outright, and the lapsed one is forgotten.
        var lapse = Now.AddHours(RelayLimits.SessionWorkRetentionDays * 24 + 1);
        var stale = Data(early.Apply(AggregateKinds.Session, b, Operations.SessionWork, ActionWork(b, "delayed", true, Now), AggregateHub.BoardCaller, lapse));
        Assert.Equal((false, false), (stale["tracked"]!.GetValue<bool>(), stale["owns"]!.GetValue<bool>()));
        Assert.Null(early.State(AggregateKinds.Session, b)["work"]?["openActions"]);
    }

    private static JsonObject StrandedReport(string sessionId, long version) => new()
    {
        ["taskId"] = "t", ["sessionId"] = sessionId, ["requestId"] = Guid.NewGuid().ToString("N"), ["expectedVersion"] = version, ["actionId"] = "deploy", ["attempt"] = 1
    };

    [Fact]
    public void A_stranded_action_is_recovered_by_an_administrator_and_its_task_is_free_once_the_lease_has_ended()
    {
        var worker = Caller with { IsAdministrator = false };
        var admin = Caller with { PrincipalId = "bob" };
        var mallory = new RelayCaller("mallory", false, ["repo"], true);
        var board = new StateEnvelope<CoordinationDocument>();
        var resource = new StateEnvelope<CoordinationDocument>();
        Assert.True(Call(board, "board", "task_update", new() { ["taskId"] = "t", ["requestId"] = "create", ["expectedVersion"] = 0 }, worker).Ok);
        Assert.True(Call(board, "board", "task_claim", new() { ["taskId"] = "t", ["sessionId"] = "a", ["requestId"] = "claim", ["expectedVersion"] = 1, ["resourceIds"] = new JsonArray("R"), ["leaseSeconds"] = 120 }, worker).Ok);
        Assert.True(Call(resource, "lock", "claim_acquire", new() { ["sessionId"] = "a", ["requestId"] = "lock", ["leaseSeconds"] = 120 }, worker).Ok);
        Assert.True(Call(board, "board", "action_begin", new() { ["taskId"] = "t", ["sessionId"] = "a", ["requestId"] = "begin", ["expectedVersion"] = 2, ["actionId"] = "deploy", ["inputFingerprint"] = "f" }, worker).Ok);
        Assert.True(Call(resource, "lock", "__block", Payload(board.Effects.Single(effect => effect.Operation == "__block")), worker).Ok);
        board.Effects.Clear();

        // The session ends (an explicit end, or a directory from before this fix) while the action is still invoking.
        var session = new StateEnvelope<CoordinationDocument>();
        Assert.True(Call(session, "session", "__open", new() { ["principalId"] = "alice", ["sessionToken"] = "secret", ["sessionId"] = "a" }, worker).Ok);
        Assert.True(Call(session, "session", Operations.SessionExpire, new SessionExpire("repo", "a", 1).ToJson(), worker).Ok);
        Assert.Equal("SessionUnknown", Call(session, "session", "__verify", new() { ["sessionToken"] = "secret" }, worker).Code);
        // Its chat reopens as another session; a plain report from there is still not the writer's.
        var reopened = StrandedReport("a2", 3); reopened["outcome"] = "completed";
        Assert.Equal("ActionDenied", Call(board, "board", "action_report", reopened, worker, 200).Code);
        // The administrator's cleanup records its acceptance on an action still invoking (it failed with InvalidOutcome). The writer could
        // still finish it, so it keeps the task: past its lease, another worker is still refused.
        var acceptance = StrandedReport("admin-session", 3); acceptance["restoration"] = "accepted";
        var accepted = Call(board, "board", "action_report", acceptance, admin, 200);
        Assert.True(accepted.Ok, accepted.Code);
        Assert.Equal(("invoking", "accepted"), (Data(accepted)["actions"]!["deploy"]!["status"]!.GetValue<string>(), Data(accepted)["actions"]!["deploy"]!["restoration"]!.GetValue<string>()));
        Assert.Equal("Busy", Call(board, "board", "task_claim", new() { ["taskId"] = "t", ["sessionId"] = "w", ["requestId"] = "w-1", ["expectedVersion"] = 4 }, worker, 300).Code);

        // Another principal may not recover it without administrator rights, and an administrator may not invent its outcome.
        var forged = StrandedReport("mallory-session", 4); forged["recover"] = true;
        Assert.Equal("ActionDenied", Call(board, "board", "action_report", forged, mallory, 300).Code);
        var invented = StrandedReport("admin-session", 4); invented["recover"] = true; invented["outcome"] = "completed";
        Assert.Equal("ActionDenied", Call(board, "board", "action_report", invented, admin, 300).Code);
        // The administrator recovers it: abandoned, with who and when, its resource unblocked, and its session released in the directory.
        var recover = StrandedReport("admin-session", 4); recover["recover"] = true;
        var recovered = Call(board, "board", "action_report", recover, admin, 300);
        Assert.True(recovered.Ok, recovered.Code);
        var action = Data(recovered)["actions"]!["deploy"]!;
        Assert.Equal(("abandoned", "a"), (action["status"]!.GetValue<string>(), action["sessionId"]!.GetValue<string>()));
        Assert.Equal(("bob", "admin-session", true, Now.AddSeconds(300).ToString("O"), "invoking"), (action["recovery"]!["principalId"]!.GetValue<string>(), action["recovery"]!["sessionId"]!.GetValue<string>(),
            action["recovery"]!["administrator"]!.GetValue<bool>(), action["recovery"]!["at"]!.GetValue<string>(), action["recovery"]!["priorStatus"]!.GetValue<string>()));
        Assert.True(Data(Call(board, "board", "task_get", new() { ["taskId"] = "t", ["section"] = "reports" }, worker, 300))["items"]!.AsArray().Last()!["recovered"]!.GetValue<bool>());
        var unblock = Assert.Single(board.Effects, effect => effect.Operation == "__unblock");
        Assert.True(Call(resource, "lock", "__unblock", Payload(unblock), admin, 300).Ok);
        // (The acceptance before it refreshed A's record; the recovery closes it.)
        var released = board.Effects.Last(effect => effect.Operation == Operations.SessionWork);
        Assert.Equal(("a", false), (Payload(released)["sessionId"]!.GetValue<string>(), Payload(released)["open"]!.GetValue<bool>()));
        // Its lease ended long ago, so the next worker claims the task and its resource.
        Assert.True(Call(board, "board", "task_claim", new() { ["taskId"] = "t", ["sessionId"] = "w", ["requestId"] = "w-2", ["expectedVersion"] = 5, ["resourceIds"] = new JsonArray("R") }, worker, 300).Ok);
        Assert.True(Call(resource, "lock", "claim_acquire", new() { ["sessionId"] = "w", ["requestId"] = "lock-w" }, worker, 300).Ok);
        // An action already resolved has nothing to recover.
        var again = StrandedReport("admin-session", 6); again["recover"] = true;
        Assert.Equal("ActionConflict", Call(board, "board", "action_report", again, admin, 300).Code);
    }

    [Fact]
    public void Its_own_principal_recovers_a_stranded_action_once_the_claim_lapses_and_the_restoration_stays_owed()
    {
        var worker = Caller with { IsAdministrator = false };
        var board = new StateEnvelope<CoordinationDocument>();
        Assert.True(Call(board, "board", "task_update", new() { ["taskId"] = "t", ["requestId"] = "create", ["expectedVersion"] = 0 }, worker).Ok);
        Assert.True(Call(board, "board", "task_claim", new() { ["taskId"] = "t", ["sessionId"] = "a", ["requestId"] = "claim", ["expectedVersion"] = 1, ["leaseSeconds"] = 120 }, worker).Ok);
        Assert.True(Call(board, "board", "action_begin", new() { ["taskId"] = "t", ["sessionId"] = "a", ["requestId"] = "begin", ["expectedVersion"] = 2, ["actionId"] = "deploy", ["inputFingerprint"] = "f", ["requiresRestoration"] = true }, worker).Ok);
        board.Effects.Clear();
        // While the writer still holds its claim it is not stranded, so its own principal on another session must wait.
        var early = StrandedReport("a2", 3); early["recover"] = true;
        Assert.Equal("OwnerConflict", Call(board, "board", "action_report", early, worker, 60).Code);
        // After the lease, it takes the action over: abandoned, restoration still pending, and the new session now answers for it.
        var recover = StrandedReport("a2", 3); recover["recover"] = true;
        var recovered = Call(board, "board", "action_report", recover, worker, 200);
        Assert.True(recovered.Ok, recovered.Code);
        var action = Data(recovered)["actions"]!["deploy"]!;
        Assert.Equal(("abandoned", "pending", "a2", false), (action["status"]!.GetValue<string>(), action["restoration"]!.GetValue<string>(), action["recovery"]!["sessionId"]!.GetValue<string>(), action["recovery"]!["administrator"]!.GetValue<bool>()));
        Assert.DoesNotContain(board.Effects, effect => effect.Operation == "__unblock");
        Assert.Equal([("a", false), ("a2", true)], board.Effects.Where(effect => effect.Operation == Operations.SessionWork).Select(effect => (Payload(effect)["sessionId"]!.GetValue<string>(), Payload(effect)["open"]!.GetValue<bool>())));
        board.Effects.Clear();
        // The task is free for the next worker, but nothing new runs on it until the restoration is reported.
        Assert.True(Call(board, "board", "task_claim", new() { ["taskId"] = "t", ["sessionId"] = "w", ["requestId"] = "w", ["expectedVersion"] = 4 }, worker, 200).Ok);
        JsonObject Next(long version) => new() { ["taskId"] = "t", ["sessionId"] = "w", ["requestId"] = Guid.NewGuid().ToString("N"), ["expectedVersion"] = version, ["actionId"] = "next", ["inputFingerprint"] = "n" };
        Assert.Equal("ActionUncertain", Call(board, "board", "action_begin", Next(5), worker, 200).Code);
        // The recovering session reports the restoration, and the next action may begin.
        var restored = StrandedReport("a2", 5); restored["recover"] = true; restored["restoration"] = "verified";
        Assert.True(Call(board, "board", "action_report", restored, worker, 220).Ok);
        Assert.Equal([("a2", false)], board.Effects.Where(effect => effect.Operation == Operations.SessionWork).Select(effect => (Payload(effect)["sessionId"]!.GetValue<string>(), Payload(effect)["open"]!.GetValue<bool>())));
        Assert.True(Call(board, "board", "action_begin", Next(6), worker, 220).Ok);
    }

    [Fact]
    public void The_writer_still_there_replaces_an_abandoned_status_with_its_real_outcome()
    {
        var worker = Caller with { IsAdministrator = false };
        var admin = Caller with { PrincipalId = "bob" };
        var board = new StateEnvelope<CoordinationDocument>();
        Assert.True(Call(board, "board", "task_update", new() { ["taskId"] = "t", ["requestId"] = "create", ["expectedVersion"] = 0 }, worker).Ok);
        Assert.True(Call(board, "board", "task_claim", new() { ["taskId"] = "t", ["sessionId"] = "a", ["requestId"] = "claim", ["expectedVersion"] = 1, ["leaseSeconds"] = 600 }, worker).Ok);
        Assert.True(Call(board, "board", "action_begin", new() { ["taskId"] = "t", ["sessionId"] = "a", ["requestId"] = "begin", ["expectedVersion"] = 2, ["actionId"] = "deploy", ["inputFingerprint"] = "f" }, worker).Ok);
        board.Effects.Clear();
        // An administrator abandons the action while its writer's lease is still live.
        var recover = StrandedReport("admin-session", 3); recover["recover"] = true;
        Assert.True(Call(board, "board", "action_report", recover, admin, 60).Ok);
        // Its external effects are unknown, so the managers get a failed item for it.
        var raised = board.Effects.Where(effect => effect.Operation == Operations.AttentionRoute).Select(effect => Payload(effect)["item"]!).Single(item => item["kind"]!.GetValue<string>() == AttentionKinds.Failed);
        Assert.Contains("deploy abandoned", raised["note"]!.GetValue<string>());
        // Another principal cannot replace it, and an outcome that would put it back in flight is refused.
        var other = StrandedReport("mallory-session", 4); other["outcome"] = "completed";
        Assert.Equal("ActionDenied", Call(board, "board", "action_report", other, new RelayCaller("mallory", false, ["repo"], true), 90).Code);
        var unknown = StrandedReport("a", 4); unknown["outcome"] = "unknown";
        Assert.Equal("ActionConflict", Call(board, "board", "action_report", unknown, worker, 90).Code);
        // The writer's real outcome replaces abandoned, and the action records what it replaced.
        var real = StrandedReport("a", 4); real["outcome"] = "completed"; real["report"] = "deployed after all";
        var replaced = Call(board, "board", "action_report", real, worker, 90);
        Assert.True(replaced.Ok, replaced.Code);
        var action = Data(replaced)["actions"]!["deploy"]!;
        Assert.Equal("completed", action["status"]!.GetValue<string>());
        Assert.Null(action["recovery"]);
        Assert.Equal(("abandoned", "bob", "a", Now.AddSeconds(90).ToString("O")), (action["overridden"]!["status"]!.GetValue<string>(), action["overridden"]!["recovery"]!["principalId"]!.GetValue<string>(),
            action["overridden"]!["sessionId"]!.GetValue<string>(), action["overridden"]!["at"]!.GetValue<string>()));
        var entry = Data(Call(board, "board", "task_get", new() { ["taskId"] = "t", ["section"] = "reports" }, worker, 90))["items"]!.AsArray().Last()!;
        Assert.Equal(("completed", "abandoned"), (entry["outcome"]!.GetValue<string>(), entry["overrode"]!.GetValue<string>()));
        // Once replaced it is an ordinary terminal outcome: another one conflicts.
        var again = StrandedReport("a", 5); again["outcome"] = "failed";
        Assert.Equal("ActionConflict", Call(board, "board", "action_report", again, worker, 90).Code);
    }

    [Fact]
    public void A_reply_stored_under_the_old_fingerprint_still_replays_for_the_same_arguments()
    {
        var board = new StateEnvelope<CoordinationDocument>();
        Assert.True(Call(board, "board", "task_update", new() { ["taskId"] = "a", ["requestId"] = "create-a", ["expectedVersion"] = 0, ["workflow"] = DependentWorkflow("completed") }).Ok);
        // A write the hub sent with an injected archivedDependsOn, stored before this change: its fingerprint covered every argument but hubIncarnation.
        JsonObject Write(string incarnation) => new()
        {
            ["taskId"] = "b", ["requestId"] = "write-b", ["expectedVersion"] = 0, ["workflow"] = DependentWorkflow("ready", "a"), ["archivedDependsOn"] = new JsonArray("a"), ["hubIncarnation"] = incarnation
        };
        var first = Call(board, "board", "task_update", Write("h1"));
        Assert.True(first.Ok, first.Code);
        var legacy = Write("h1"); legacy["repositoryId"] = "repo"; legacy.Remove("hubIncarnation");
        var state = StateJson(board);
        state["requests"]!["alice:task_update:write-b"]!["fingerprint"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(legacy.ToJsonString())));
        board.Value = new() { Json = state.ToJsonString() };
        var stored = board.Value.Json;
        // The same request from a restarted hub replays the stored reply and changes nothing.
        Assert.Equal(first, Call(board, "board", "task_update", Write("h2")));
        Assert.Equal(stored, board.Value.Json);
        // The old fingerprint still names its injected arguments, so only an identical request matches it; a changed one conflicts.
        var changed = Write("h2"); changed["workflow"]!["priority"] = 3;
        Assert.Equal("RequestConflict", Call(board, "board", "task_update", changed).Code);
    }

    [Fact]
    public async Task A_retry_with_unchanged_public_arguments_replays_even_when_the_hub_adds_different_internal_ones()
    {
        var archive = new FakeArchive();
        var application = new RelayApplication(DispatchProxy.Create<IGrainFactory, TestGrainFactory>(), archive: archive);
        Task<RelayReply> Invoke(string tool, JsonObject args) { args["repositoryId"] = "repo"; return application.ExecuteAsync(Caller with { IsInternal = false }, tool, JsonSerializer.SerializeToElement(args)); }
        var session = Data(await Invoke("session_open", new() { ["requestId"] = "open" }));
        JsonObject For(string requestId) => new() { ["sessionId"] = session["sessionId"]!.DeepClone(), ["sessionToken"] = session["sessionToken"]!.DeepClone(), ["requestId"] = requestId };
        var a = For("create-a"); a["taskId"] = "a"; a["expectedVersion"] = 0; a["workflow"] = DependentWorkflow("completed");
        Assert.True((await Invoke("task_update", a)).Ok);
        // A write that depends on a, then a is archived before its retry: the hub now names a in archivedDependsOn.
        var write = For("create-b"); write["taskId"] = "b"; write["expectedVersion"] = 0; write["workflow"] = DependentWorkflow("ready", "a");
        var first = await Invoke("task_update", write.DeepClone().AsObject());
        Assert.True(first.Ok, first.Code);
        archive.Archived.Add("a");
        var retried = await Invoke("task_update", write.DeepClone().AsObject());
        Assert.True(retried.Ok, retried.Code);
        Assert.Equal((first.DataJson, first.Version), (retried.DataJson, retried.Version));
        // The same for the verified binding the hub adds to action_begin: the session rebinds between a begin and its retry.
        var claim = For("claim-b"); claim["taskId"] = "b"; claim["expectedVersion"] = 1;
        Assert.True((await Invoke("task_claim", claim)).Ok);
        var begin = For("begin"); begin["taskId"] = "b"; begin["expectedVersion"] = 2; begin["actionId"] = "x"; begin["inputFingerprint"] = "f";
        Assert.Equal("Ok", (await Invoke("action_begin", begin.DeepClone().AsObject())).Code); // The hub blocks the locks itself, so even this fake confirms them.
        var bind = For("bind"); bind["expectedVersion"] = 0; bind["binding"] = new JsonObject { ["solution"] = "other.sln" };
        Assert.True((await Invoke("session_bind", bind)).Ok);
        var again = await Invoke("action_begin", begin.DeepClone().AsObject());
        Assert.True(again.Ok, again.Code);
        Assert.True(Data(again)["existing"]!.GetValue<bool>());
        // Changed public arguments still conflict.
        write["workflow"]!["priority"] = 3;
        Assert.Equal("RequestConflict", (await Invoke("task_update", write)).Code);
    }

    [Fact]
    public async Task A_claim_resolves_its_own_resources_by_the_sessions_machine_like_the_declared_ones()
    {
        var application = new RelayApplication(DispatchProxy.Create<IGrainFactory, TestGrainFactory>());
        Task<RelayReply> Invoke(string tool, JsonObject args) { args["repositoryId"] = "repo"; args["requestId"] = Guid.NewGuid().ToString("N"); return application.ExecuteAsync(Caller with { IsInternal = false }, tool, JsonSerializer.SerializeToElement(args)); }
        var session = Data(await Invoke("session_open", new() { ["machineId"] = "pc" }));
        JsonObject For() => new() { ["sessionId"] = session["sessionId"]!.DeepClone(), ["sessionToken"] = session["sessionToken"]!.DeepClone(), ["taskId"] = "t" };
        Assert.True((await Invoke("control_request", new() { ["command"] = "register_resource", ["resourceId"] = "rig-1", ["aliases"] = new JsonArray("bench"), ["machineId"] = "pc" })).Ok);
        var workflow = DependentWorkflow("ready"); workflow["packet"]!["resources"] = new JsonArray("bench");
        var create = For(); create["expectedVersion"] = 0; create["workflow"] = workflow;
        Assert.True((await Invoke("task_update", create)).Ok);
        // The call names another machine, where "bench" means nothing; both lists still resolve by the session's machine, to rig-1.
        var claim = For(); claim["expectedVersion"] = 1; claim["resourceIds"] = new JsonArray("bench"); claim["machineId"] = "elsewhere";
        var claimed = await Invoke("task_claim", claim);
        Assert.True(claimed.Ok, claimed.Code + " " + claimed.DataJson);
        Assert.Equal(["rig-1"], Data(claimed)["resourceIds"]!.AsArray().Select(id => id!.GetValue<string>()));
    }

    private static long TaskVersion(StateEnvelope<CoordinationDocument> board) => StateJson(board)["tasks"]!["t"]!["version"]!.GetValue<long>();
    private static IEnumerable<(string Session, bool Open)> Tracking(StateEnvelope<CoordinationDocument> board) =>
        board.Effects.Where(effect => effect.Operation == Operations.SessionWork).Select(effect => (Payload(effect)["sessionId"]!.GetValue<string>(), Payload(effect)["open"]!.GetValue<bool>()));

    /// <summary>A task t claimed by session a for <paramref name="lease"/> seconds, with action deploy invoking (and owing a restoration).</summary>
    private static StateEnvelope<CoordinationDocument> Invoking(RelayCaller worker, int lease, bool restoration = true)
    {
        var board = new StateEnvelope<CoordinationDocument>();
        Assert.True(Call(board, "board", "task_update", new() { ["taskId"] = "t", ["requestId"] = "create", ["expectedVersion"] = 0 }, worker).Ok);
        Assert.True(Call(board, "board", "task_claim", new() { ["taskId"] = "t", ["sessionId"] = "a", ["requestId"] = "claim", ["expectedVersion"] = 1, ["leaseSeconds"] = lease }, worker).Ok);
        Assert.True(Call(board, "board", "action_begin", new() { ["taskId"] = "t", ["sessionId"] = "a", ["requestId"] = "begin", ["expectedVersion"] = 2, ["actionId"] = "deploy", ["inputFingerprint"] = "f", ["requiresRestoration"] = restoration }, worker).Ok);
        board.Effects.Clear();
        return board;
    }

    [Fact]
    public void Another_session_that_takes_back_an_abandoned_action_answers_for_it_and_its_restoration()
    {
        var worker = Caller with { IsAdministrator = false };
        var admin = Caller with { PrincipalId = "bob" };
        var board = Invoking(worker, 120);
        var recover = StrandedReport("admin-session", 3); recover["recover"] = true;
        Assert.True(Call(board, "board", "action_report", recover, admin, 200).Ok);
        board.Effects.Clear();
        // A new session of the writer's principal reports the real outcome; the writer's own session is gone.
        var real = StrandedReport("a3", 4); real["outcome"] = "completed";
        var replaced = Call(board, "board", "action_report", real, worker, 210);
        Assert.True(replaced.Ok, replaced.Code);
        var action = Data(replaced)["actions"]!["deploy"]!;
        Assert.Equal(("completed", "pending", "a3", true), (action["status"]!.GetValue<string>(), action["restoration"]!.GetValue<string>(),
            action["recovery"]!["sessionId"]!.GetValue<string>(), action["recovery"]!["override"]!.GetValue<bool>()));
        // The directory moves the action from the administrator's session to a3, not back to the writer's dead session.
        Assert.Equal([("admin-session", false), ("a3", true)], Tracking(board));
        // Nothing is abandoned any more, so the managers' failed item is closed rather than left saying the effects are unknown.
        Assert.Contains(board.Effects, effect => effect.Operation == Operations.AttentionRoute && Payload(effect)["operation"]!.GetValue<string>() == Operations.AttentionClose
            && Payload(effect)["item"]!["kinds"]!.AsArray().Any(kind => kind!.GetValue<string>() == AttentionKinds.Failed));
        // a3 reports the owed restoration like any session that answers for an action.
        var restored = StrandedReport("a3", 5); restored["outcome"] = "completed"; restored["restoration"] = "verified";
        Assert.True(Call(board, "board", "action_report", restored, worker, 220).Ok);
    }

    [Fact]
    public void The_writer_that_takes_back_an_abandoned_action_holds_its_task_until_the_restoration_is_reported()
    {
        var worker = Caller with { IsAdministrator = false };
        var admin = Caller with { PrincipalId = "bob" };
        var board = Invoking(worker, 600);
        var recover = StrandedReport("admin-session", 3); recover["recover"] = true;
        Assert.True(Call(board, "board", "action_report", recover, admin, 60).Ok);
        var real = StrandedReport("a", 4); real["outcome"] = "completed";
        var replaced = Call(board, "board", "action_report", real, worker, 90);
        Assert.True(replaced.Ok, replaced.Code);
        Assert.Null(Data(replaced)["actions"]!["deploy"]!["recovery"]);
        // Its writer answers for it again, so the owed restoration keeps the claim, as for any of its writer's actions.
        var release = Call(board, "board", "claim_release", new() { ["taskId"] = "t", ["sessionId"] = "a", ["claimGeneration"] = 1, ["requestId"] = "release", ["expectedVersion"] = TaskVersion(board) }, worker, 100);
        Assert.Equal("UnresolvedAction", release.Code);
    }

    [Fact]
    public void A_recovery_waits_for_the_claim_of_the_session_that_recovered_the_action_and_settles_it()
    {
        var worker = Caller with { IsAdministrator = false };
        var board = Invoking(worker, 120);
        var first = StrandedReport("a2", 3); first["recover"] = true;
        Assert.True(Call(board, "board", "action_report", first, worker, 200).Ok);
        // a2 answers for the action and claims the task to work on the restoration.
        Assert.True(Call(board, "board", "task_claim", new() { ["taskId"] = "t", ["sessionId"] = "a2", ["requestId"] = "a2-claim", ["expectedVersion"] = TaskVersion(board), ["leaseSeconds"] = 60 }, worker, 200).Ok);
        // While a2's claim is live, another session of the same principal cannot take the action from it; a2 itself still can.
        var steal = StrandedReport("a3", TaskVersion(board)); steal["recover"] = true;
        Assert.Equal("OwnerConflict", Call(board, "board", "action_report", steal, worker, 210).Code);
        // A recovery settles the action: it never leaves it in flight under a session that did not start it.
        var running = StrandedReport("a3", TaskVersion(board)); running["recover"] = true; running["outcome"] = "started";
        Assert.Equal("InvalidOutcome", Call(board, "board", "action_report", running, worker, 300).Code);
        // Once a2's claim has lapsed, a3 may take it over.
        var later = StrandedReport("a3", TaskVersion(board)); later["recover"] = true;
        Assert.True(Call(board, "board", "action_report", later, worker, 300).Ok);
    }

    [Fact]
    public void A_session_the_action_comes_back_to_is_tracked_again_but_a_reordered_open_is_not()
    {
        var hub = new AggregateHub();
        var a = hub.Open("a", Now).Id;
        JsonObject Work(bool open, int seconds, int delivered) =>
            Data(hub.Apply(AggregateKinds.Session, a, Operations.SessionWork, ActionWork(a, "k", open, Now.AddSeconds(seconds)), AggregateHub.BoardCaller, Now.AddSeconds(delivered)));
        Assert.True(Work(true, 0, 0)["owns"]!.GetValue<bool>());
        Assert.False(Work(false, 10, 10)["owns"]!.GetValue<bool>());
        // A refresh reported before the close, delivered after it, changes nothing.
        Assert.False(Work(true, 5, 20)["tracked"]!.GetValue<bool>());
        Assert.Null(hub.State(AggregateKinds.Session, a)["work"]!["openActions"]);
        // An open reported after the close is real (the writer took the action back), and opens it again.
        Assert.True(Work(true, 30, 30)["tracked"]!.GetValue<bool>());
        Assert.Single(hub.State(AggregateKinds.Session, a)["work"]!["openActions"]!.AsObject());
        hub.DeliverAll(Now.AddSeconds(30));
        Assert.NotNull(hub.Directory()[a]!["ownsWork"]);
    }

    [Fact]
    public void Progress_on_a_long_action_refreshes_the_directory_at_most_once_a_day()
    {
        var worker = Caller with { IsAdministrator = false };
        var board = Invoking(worker, RelayLimits.MaxLeaseSeconds, restoration: false);
        JsonObject Progress(string outcome) { var report = StrandedReport("a", TaskVersion(board)); report["outcome"] = outcome; return report; }
        Assert.True(Call(board, "board", "action_report", Progress("started"), worker, 60).Ok);
        Assert.Empty(Tracking(board));
        Assert.True(Call(board, "board", "action_report", Progress("incomplete"), worker, (RelayLimits.SessionWorkRefreshHours + 1) * 3600).Ok);
        Assert.Equal([("a", true)], Tracking(board));
    }

    [Fact]
    public void Only_a_task_that_declares_resources_asks_the_hub_to_resolve_them_and_a_held_lock_refusal_records_nothing()
    {
        var worker = Caller with { IsAdministrator = false };
        var board = new StateEnvelope<CoordinationDocument>();
        var workflow = DependentWorkflow("ready"); workflow["packet"]!["resources"] = new JsonArray("rig-1");
        Assert.True(Call(board, "board", "task_update", new() { ["taskId"] = "t", ["requestId"] = "create", ["expectedVersion"] = 0, ["workflow"] = workflow }, worker).Ok);
        Assert.True(Call(board, "board", "task_update", new() { ["taskId"] = "plain", ["requestId"] = "create-plain", ["expectedVersion"] = 0 }, worker).Ok);
        // A task without declared resources answers the hub's request at once.
        Assert.True(Call(board, "board", "task_claim", new() { ["taskId"] = "plain", ["sessionId"] = "a", ["requestId"] = "plain-claim", ["expectedVersion"] = 1, [Operations.ResolveDeclaredField] = true }, worker).Ok);
        var needed = Call(board, "board", "task_claim", new() { ["taskId"] = "t", ["sessionId"] = "a", ["requestId"] = "claim", ["expectedVersion"] = 1, ["resourceIds"] = new JsonArray(), [Operations.ResolveDeclaredField] = true }, worker);
        Assert.Equal(RelayErrorCodes.DeclaredResourcesNeeded, needed.Code);
        Assert.Equal(["rig-1"], Data(needed)["declared"]!.AsArray().Select(name => name!.GetValue<string>()));
        Assert.True(Call(board, "board", "task_claim", new() { ["taskId"] = "t", ["sessionId"] = "a", ["requestId"] = "claim", ["expectedVersion"] = 1, ["resourceIds"] = new JsonArray("rig-1") }, worker).Ok);
        JsonObject Begin(string requestId, JsonObject? refusal)
        {
            var args = new JsonObject { ["taskId"] = "t", ["sessionId"] = "a", ["requestId"] = requestId, ["expectedVersion"] = 2, ["actionId"] = "deploy", ["inputFingerprint"] = "f", ["declaredResources"] = new JsonObject { ["rig-1"] = "rig-1" } };
            if (refusal is not null) args[Operations.LockRefusalField] = refusal;
            return args;
        }
        // The hub found rig-1 held by another session: Busy, and no action is recorded.
        Assert.Equal("Busy", Call(board, "board", "action_begin", Begin("busy", new JsonObject { ["code"] = "Busy", ["resourceId"] = "rig-1" }), worker).Code);
        Assert.Empty(StateJson(board)["tasks"]!["t"]!["actions"]!.AsObject());
        Assert.True(Call(board, "board", "action_begin", Begin("begin", null), worker).Ok);
        // An action already recorded is returned as it stands, whatever its locks look like now.
        var existing = Call(board, "board", "action_begin", Begin("again", new JsonObject { ["code"] = "ClaimRequired", ["resourceId"] = "rig-1" }), worker);
        Assert.True(existing.Ok, existing.Code);
        Assert.True(Data(existing)["existing"]!.GetValue<bool>());
    }

    [Fact]
    public void A_claim_retry_is_identified_by_the_clients_own_resource_names_and_a_grant_retry_ignores_the_declared_map()
    {
        var board = new StateEnvelope<CoordinationDocument>();
        Assert.True(Call(board, "board", "task_update", new() { ["taskId"] = "t", ["requestId"] = "create", ["expectedVersion"] = 0 }).Ok);
        JsonObject Claim(string canonical, string named) => new()
        {
            ["taskId"] = "t", ["sessionId"] = "a", ["requestId"] = "claim", ["expectedVersion"] = 1, ["resourceIds"] = new JsonArray(canonical),
            [ServerArguments.RequestedResources] = new JsonArray(named)
        };
        Assert.True(Call(board, "board", "task_claim", Claim("rig-1", "bench")).Ok);
        // The alias now names another resource: the same request still replays.
        Assert.True(Call(board, "board", "task_claim", Claim("rig-9", "bench")).Ok);
        Assert.Equal("RequestConflict", Call(board, "board", "task_claim", Claim("rig-1", "other")).Code);
        var lockState = new StateEnvelope<CoordinationDocument>();
        JsonObject Acquire(string declared) => new() { ["sessionId"] = "a", ["resourceId"] = "rig-1", ["requestId"] = "claim:resource:rig-1", ["declaredResources"] = new JsonObject { ["x"] = declared } };
        Assert.True(Call(lockState, "lock", "claim_acquire", Acquire("rig-1")).Ok);
        Assert.True(Call(lockState, "lock", "claim_acquire", Acquire("rig-9")).Ok);
    }

    private sealed class FakeArchive : ITaskArchive
    {
        public HashSet<string> Archived { get; } = new(StringComparer.Ordinal);
        public Task<string?> ReadArchivedTaskAsync(string repositoryId, string boardId, string taskId, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<IReadOnlySet<string>> ArchivedIdsAsync(string repositoryId, string boardId, IReadOnlyCollection<string> taskIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<string>>(taskIds.Where(Archived.Contains).ToHashSet(StringComparer.Ordinal));
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
