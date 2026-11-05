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

    private static RelayReply Board(StateEnvelope<CoordinationDocument> state, string op, JsonObject args, double days = 0, int archiveAfterDays = RelayLimits.DefaultArchiveAfterDays)
    {
        args["repositoryId"] = "repo";
        return CoordinationStateMachine.Apply(state, "board", "repo/default", Caller, op, args, Now.AddDays(days), archiveAfterDays: archiveAfterDays);
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

    private static JsonObject StrandedReport(string sessionId, long version) => new()
    {
        ["taskId"] = "t", ["sessionId"] = sessionId, ["requestId"] = Guid.NewGuid().ToString("N"), ["expectedVersion"] = version, ["actionId"] = "deploy", ["attempt"] = 1
    };

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
