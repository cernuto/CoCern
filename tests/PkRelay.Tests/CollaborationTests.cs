using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orleans;
using PkRelay.Core;
using PkRelay.Domain;
using Xunit;

namespace PkRelay.Tests;

public sealed class CollaborationTests
{
    private static readonly RelayCaller Caller = new("alice", true, ["repo"], true);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-29T00:00:00Z");
    private static JsonObject Data(RelayReply reply) => JsonNode.Parse(reply.DataJson)!.AsObject();
    private static RelayReply Call(StateEnvelope<CoordinationDocument> state, string operation, JsonObject args, int seconds = 0)
    {
        args["repositoryId"] = "repo";
        return CoordinationStateMachine.Apply(state, "board", "repo:default", Caller, operation, args, Now.AddSeconds(seconds));
    }
    private static JsonObject Workflow(string state = "ready") => new()
    {
        ["state"] = state,
        ["packet"] = new JsonObject { ["goal"] = "Close useful leg", ["target"] = "repo at revision abc", ["authorityRef"] = "human-grant:17", ["restoration"] = "Remove owned fixture", ["stopCondition"] = "Stop if target differs" },
        ["criteria"] = new JsonArray(new JsonObject { ["id"] = "leg", ["description"] = "Reported leg passes", ["result"] = "not_run" }),
        ["blockers"] = new JsonArray()
    };
    private static JsonObject Update(string id, long version, JsonObject? workflow = null) => new() { ["taskId"] = id, ["expectedVersion"] = version, ["requestId"] = Guid.NewGuid().ToString("N"), ["workflow"] = workflow?.DeepClone() };

    [Fact]
    public void Legacy_duplicate_claim_keeps_original_grant_and_revision()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        var create = Update("legacy", 0); create.Remove("workflow");
        Assert.True(Call(state, "task_update", create).Ok);
        var claim = new JsonObject { ["taskId"] = "legacy", ["sessionId"] = "s", ["expectedVersion"] = 1, ["requestId"] = "claim" };
        var granted = Call(state, "task_claim", claim); Assert.True(granted.Ok);
        var persisted = state.Value.Json;
        Assert.Equal(granted, Call(state, "task_claim", claim, 1));
        Assert.Equal(persisted, state.Value.Json);
    }

    private static JsonObject IneligibleWorkflow(string change)
    {
        var workflow = Workflow(change == "retargeted" ? "ready" : change);
        if (change == "retargeted") workflow["targetSessionId"] = "another-session";
        if (change == "blocked") workflow["blockers"]!.AsArray().Add(new JsonObject { ["id"] = "missing", ["kind"] = "prerequisite", ["summary"] = "Prerequisite missing", ["recheckCondition"] = "Prerequisite changes" });
        if (change == "completed") { workflow["criteria"]![0]!["result"] = "passed"; workflow["criteria"]![0]!["evidenceRef"] = "artifact:reported"; }
        return workflow;
    }

    [Theory]
    [InlineData("blocked")]
    [InlineData("completed")]
    [InlineData("retargeted")]
    public void Cached_claim_rechecks_current_workflow_without_mutating_grant_or_revision(string change)
    {
        var state = new StateEnvelope<CoordinationDocument>();
        Assert.True(Call(state, "task_update", Update("t", 0, Workflow())).Ok);
        var claim = new JsonObject { ["taskId"] = "t", ["sessionId"] = "s", ["expectedVersion"] = 1, ["requestId"] = "claim" };
        Assert.True(Call(state, "task_claim", claim).Ok);
        var update = Update("t", 2, IneligibleWorkflow(change)); update["sessionId"] = "s";
        Assert.True(Call(state, "task_update", update).Ok);
        var persisted = state.Value.Json;
        Assert.Equal("WorkflowNotReady", Call(state, "task_claim", claim, 1).Code);
        Assert.Equal(persisted, state.Value.Json);
    }

    [Theory]
    [InlineData("blocked")]
    [InlineData("completed")]
    [InlineData("retargeted")]
    public void Current_workflow_blocks_new_action_and_retry_but_allows_existing_reconciliation(string change)
    {
        var state = new StateEnvelope<CoordinationDocument>();
        Assert.True(Call(state, "task_update", Update("t", 0, Workflow())).Ok);
        Assert.True(Call(state, "task_claim", new() { ["taskId"] = "t", ["sessionId"] = "s", ["expectedVersion"] = 1 }).Ok);
        var begin = new JsonObject { ["taskId"] = "t", ["sessionId"] = "s", ["expectedVersion"] = 2, ["requestId"] = "begin", ["actionId"] = "external", ["inputFingerprint"] = "abc" };
        Assert.True(Call(state, "action_begin", begin).Ok);
        Assert.True(Call(state, "action_report", new() { ["taskId"] = "t", ["sessionId"] = "s", ["expectedVersion"] = 3, ["actionId"] = "external", ["attempt"] = 1, ["outcome"] = "completed" }).Ok);
        var update = Update("t", 4, IneligibleWorkflow(change)); update["sessionId"] = "s";
        Assert.True(Call(state, "task_update", update).Ok);
        var persisted = state.Value.Json;
        var existing = Data(Call(state, "action_begin", begin));
        Assert.True(existing["existing"]!.GetValue<bool>()); Assert.False(existing["replayAllowed"]!.GetValue<bool>());
        Assert.Equal(persisted, state.Value.Json);
        var reconcile = begin.DeepClone().AsObject(); reconcile.Remove("requestId");
        var reconciled = Data(Call(state, "action_begin", reconcile));
        Assert.True(reconciled["existing"]!.GetValue<bool>()); Assert.False(reconciled["replayAllowed"]!.GetValue<bool>());
        Assert.Equal(persisted, state.Value.Json);
        var next = new JsonObject { ["taskId"] = "t", ["sessionId"] = "s", ["expectedVersion"] = 5, ["actionId"] = "next", ["inputFingerprint"] = "next" };
        Assert.Equal("WorkflowNotReady", Call(state, "action_begin", next).Code);
        Assert.Equal(persisted, state.Value.Json);
        next["actionId"] = "external"; next["inputFingerprint"] = "abc"; next["attempt"] = 2; next["explicitRetry"] = true;
        Assert.Equal("WorkflowNotReady", Call(state, "action_begin", next).Code);
        Assert.Equal(persisted, state.Value.Json);
        Assert.True(Call(state, "action_report", new() { ["taskId"] = "t", ["sessionId"] = "s", ["expectedVersion"] = 5, ["actionId"] = "external", ["attempt"] = 1, ["outcome"] = "completed", ["report"] = "Late evidence remains reportable" }).Ok);
    }

    [Fact]
    public void Blocked_workflow_preserves_original_outcome_and_administrative_cleanup_reports()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        Assert.True(Call(state, "task_update", Update("t", 0, Workflow())).Ok);
        Assert.True(Call(state, "task_claim", new() { ["taskId"] = "t", ["sessionId"] = "worker", ["expectedVersion"] = 1 }).Ok);
        Assert.True(Call(state, "action_begin", new() { ["taskId"] = "t", ["sessionId"] = "worker", ["expectedVersion"] = 2, ["actionId"] = "external", ["inputFingerprint"] = "abc", ["requiresRestoration"] = true }).Ok);
        var block = Update("t", 3, IneligibleWorkflow("blocked")); block["sessionId"] = "worker";
        Assert.True(Call(state, "task_update", block).Ok);
        Assert.True(Call(state, "action_report", new() { ["taskId"] = "t", ["sessionId"] = "worker", ["expectedVersion"] = 4, ["actionId"] = "external", ["attempt"] = 1, ["outcome"] = "completed", ["restoration"] = "pending" }).Ok);
        Assert.True(Call(state, "action_report", new() { ["taskId"] = "t", ["sessionId"] = "admin-own-session", ["expectedVersion"] = 5, ["actionId"] = "external", ["attempt"] = 1, ["restoration"] = "accepted" }).Ok);
        var task = Data(Call(state, "task_get", new() { ["taskId"] = "t" }));
        Assert.Equal("blocked", task["workflow"]!["state"]!.GetValue<string>());
        Assert.Equal("completed", task["actions"]!["external"]!["status"]!.GetValue<string>());
        Assert.Equal("accepted", task["actions"]!["external"]!["restoration"]!.GetValue<string>());
        Assert.Equal("worker", task["actions"]!["external"]!["sessionId"]!.GetValue<string>());
    }

    [Fact]
    public void Delta_tracks_committed_changes_retry_renewal_context_and_restart()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        var create = Update("a", 0); create.Remove("workflow");
        Assert.True(Call(state, "task_update", create).Ok);
        var first = Data(Call(state, "task_get", new() { ["sinceRevision"] = 0 }));
        Assert.Equal(1, first["snapshotRevision"]!.GetValue<long>());
        Assert.Equal(1, first["items"]![0]!["changeRevision"]!.GetValue<long>());
        Assert.True(Call(state, "task_update", create).Ok);
        Assert.Equal("VersionConflict", Call(state, "task_update", new() { ["taskId"] = "a", ["expectedVersion"] = 0 }).Code);
        Assert.True(Call(state, "context_put", new() { ["contextId"] = "note", ["expectedVersion"] = 0, ["content"] = "context" }).Ok);
        Assert.Empty(Data(Call(state, "task_get", new() { ["sinceRevision"] = 1 }))["items"]!.AsArray());
        Assert.True(Call(state, "task_claim", new() { ["taskId"] = "a", ["sessionId"] = "s", ["expectedVersion"] = 1 }).Ok);
        Assert.True(Call(state, "claim_renew", new() { ["taskId"] = "a", ["sessionId"] = "s", ["claimGeneration"] = 1, ["leaseSeconds"] = 120 }, 1).Ok);
        var reopened = new StateEnvelope<CoordinationDocument> { Value = new() { Json = state.Value.Json } };
        var delta = Data(Call(reopened, "task_get", new() { ["sinceRevision"] = 1 }));
        Assert.Equal(3, delta["snapshotRevision"]!.GetValue<long>());
        Assert.Equal(2, delta["items"]![0]!["version"]!.GetValue<long>());
    }

    [Fact]
    public void Delta_pins_pages_and_seeds_legacy_tasks_without_changing_versions()
    {
        var state = new StateEnvelope<CoordinationDocument> { Value = new() { Json = "{\"tasks\":{\"legacy\":{\"taskId\":\"legacy\",\"version\":7}}}" } };
        var initial = Data(Call(state, "task_get", new() { ["sinceRevision"] = 0 }));
        Assert.Equal(7, initial["items"]![0]!["version"]!.GetValue<long>());
        var create = Update("new", 0); create.Remove("workflow"); Assert.True(Call(state, "task_update", create).Ok);
        var page = Data(Call(state, "task_get", new() { ["sinceRevision"] = 0, ["limit"] = 1 }));
        Assert.NotNull(page["nextCursor"]);
        var continuation = new JsonObject { ["sinceRevision"] = 0, ["limit"] = 1, ["cursor"] = page["nextCursor"]!.DeepClone(), ["snapshotRevision"] = page["snapshotRevision"]!.DeepClone() };
        Assert.Single(Data(Call(state, "task_get", continuation))["items"]!.AsArray());
        create = Update("later", 0); create.Remove("workflow"); Assert.True(Call(state, "task_update", create).Ok);
        Assert.Equal("DeltaRestartRequired", Call(state, "task_get", continuation).Code);
        Assert.Equal("InvalidArguments", Call(state, "task_get", new() { ["sinceRevision"] = -1 }).Code);
        Assert.Equal("InvalidArguments", Call(state, "task_get", new() { ["sinceRevision"] = "invalid" }).Code);
    }

    [Fact]
    public void Workflow_is_opt_in_validated_and_blocked_recheck_is_explicit()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        var invalid = Workflow(); invalid["packet"]!.AsObject().Remove("authorityRef");
        Assert.Equal("InvalidWorkflow", Call(state, "task_update", Update("bad", 0, invalid)).Code);
        var blocked = Workflow("blocked"); blocked["blockers"]!.AsArray().Add(new JsonObject { ["id"] = "hold", ["kind"] = "coordinator_hold", ["summary"] = "Review existing grant", ["recheckCondition"] = "Applicable grant identified" });
        Assert.True(Call(state, "task_update", Update("t", 0, blocked)).Ok);
        Assert.Equal("WorkflowRecheckRequired", Call(state, "task_update", Update("t", 1, Workflow())).Code);
        var ready = Workflow(); ready["recheckEvidenceRef"] = "human-grant:17";
        Assert.True(Call(state, "task_update", Update("t", 1, ready), 3600).Ok);
        var stored = Data(Call(state, "task_get", new() { ["taskId"] = "t", ["section"] = "workflow" }));
        Assert.Equal("human-grant:17", stored["packet"]!["authorityRef"]!.GetValue<string>());
        var completed = Workflow("completed");
        Assert.Equal("WorkflowIncomplete", Call(state, "task_update", Update("t", 2, completed)).Code);
        completed["criteria"]![0]!["result"] = "passed"; completed["criteria"]![0]!["evidenceRef"] = "artifact:result";
        Assert.True(Call(state, "task_update", Update("t", 2, completed)).Ok);
        Assert.Equal("WorkflowNotReady", Call(state, "task_claim", new() { ["taskId"] = "t", ["sessionId"] = "s", ["expectedVersion"] = 3 }).Code);
    }

    [Fact]
    public void Administrator_can_recover_only_abandoned_workflow_without_impersonating_action_writer()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        Assert.True(Call(state, "task_update", Update("t", 0, Workflow())).Ok);
        Assert.True(Call(state, "task_claim", new() { ["taskId"] = "t", ["sessionId"] = "old", ["expectedVersion"] = 1, ["leaseSeconds"] = 1 }).Ok);
        var blocked = Workflow("blocked"); blocked["blockers"]!.AsArray().Add(new JsonObject { ["id"] = "hold", ["kind"] = "coordinator_hold", ["summary"] = "Unnecessary hold", ["recheckCondition"] = "Existing authority reviewed" });
        var update = Update("t", 2, blocked); update["sessionId"] = "old";
        Assert.True(Call(state, "task_update", update).Ok);
        var ready = Workflow(); ready["recheckEvidenceRef"] = "human-grant:17";
        update = Update("t", 3, ready); update["sessionId"] = "admin-own-session";
        Assert.Equal("OwnerConflict", Call(state, "task_update", update).Code);
        update["patch"] = new JsonObject { ["spoof"] = true };
        Assert.Equal("OwnerConflict", Call(state, "task_update", update, 10).Code);
        update.Remove("patch"); Assert.True(Call(state, "task_update", update, 10).Ok);
        Assert.Equal("old", Data(Call(state, "task_get", new() { ["taskId"] = "t" }))["owner"]!.GetValue<string>());
        Assert.True(Call(state, "task_claim", new() { ["taskId"] = "t", ["sessionId"] = "new", ["expectedVersion"] = 4 }, 10).Ok);
        Assert.True(Call(state, "action_begin", new() { ["taskId"] = "t", ["sessionId"] = "new", ["expectedVersion"] = 5, ["actionId"] = "external", ["inputFingerprint"] = "abc" }, 10).Ok);
        update = Update("t", 6, Workflow()); update["sessionId"] = "admin-own-session";
        Assert.Equal("UnresolvedAction", Call(state, "task_update", update, 5000).Code);
    }

    [Fact]
    public void Workflow_completion_preserves_action_restoration_and_recheck_may_replace_authority()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        var ready = Workflow(); ready["priority"] = null; ready["targetSessionId"] = null; ready["recheckEvidenceRef"] = null; ready["criteria"]![0]!["evidenceRef"] = null;
        Assert.True(Call(state, "task_update", Update("t", 0, ready)).Ok);
        Assert.True(Call(state, "task_claim", new() { ["taskId"] = "t", ["sessionId"] = "s", ["expectedVersion"] = 1 }).Ok);
        Assert.True(Call(state, "action_begin", new() { ["taskId"] = "t", ["sessionId"] = "s", ["expectedVersion"] = 2, ["actionId"] = "test", ["inputFingerprint"] = "abc", ["requiresRestoration"] = true }).Ok);
        var completed = Workflow("completed"); completed["criteria"]![0]!["result"] = "passed"; completed["criteria"]![0]!["evidenceRef"] = "artifact:reported";
        var finish = Update("t", 3, completed); finish["sessionId"] = "s";
        Assert.Equal("WorkflowIncomplete", Call(state, "task_update", finish).Code);
        finish["actionReports"] = new JsonArray(new JsonObject { ["actionId"] = "test", ["attempt"] = 1, ["outcome"] = "completed", ["restoration"] = "pending" });
        Assert.Equal("WorkflowIncomplete", Call(state, "task_update", finish).Code);
        finish["actionReports"]![0]!["restoration"] = "verified";
        Assert.True(Call(state, "task_update", finish).Ok);
        var blocked = Workflow("blocked"); blocked["blockers"]!.AsArray().Add(new JsonObject { ["id"] = "grant", ["kind"] = "coordinator_hold", ["summary"] = "Review grant", ["recheckCondition"] = "Grant found" });
        Assert.True(Call(state, "task_update", Update("other", 0, blocked)).Ok);
        ready = Workflow(); ready["packet"]!["authorityRef"] = "human-grant:18"; ready["recheckEvidenceRef"] = "human-grant:18";
        Assert.True(Call(state, "task_update", Update("other", 1, ready)).Ok);
        Assert.Equal("human-grant:18", Data(Call(state, "task_get", new() { ["taskId"] = "other", ["section"] = "workflow" }))["packet"]!["authorityRef"]!.GetValue<string>());
    }

    [Fact]
    public void Advisory_offer_filters_owner_target_unknown_actions_and_uses_stable_priority()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        foreach (var id in new[] { "current", "a", "b", "busy", "unknown", "targeted", "legacy" })
        {
            var workflow = Workflow(); workflow["priority"] = id is "busy" or "unknown" or "targeted" ? 100 : 1;
            if (id == "unknown") workflow["priority"] = 200;
            if (id == "targeted") workflow["targetSessionId"] = "another";
            var update = Update(id, 0, workflow);
            if (id == "legacy") { update.Remove("workflow"); update["patch"] = new JsonObject { ["status"] = "ready", ["workflow"] = Workflow() }; }
            Assert.True(Call(state, "task_update", update).Ok);
        }
        foreach (var id in new[] { "busy", "unknown" }) Assert.True(Call(state, "task_claim", new() { ["taskId"] = id, ["sessionId"] = "another", ["expectedVersion"] = 1, ["leaseSeconds"] = 10 }).Ok);
        Assert.True(Call(state, "action_begin", new() { ["taskId"] = "unknown", ["sessionId"] = "another", ["expectedVersion"] = 2, ["actionId"] = "external", ["inputFingerprint"] = "abc" }).Ok);
        var args = new JsonObject { ["taskId"] = "current", ["sessionId"] = "worker" };
        Assert.Equal("a", Data(Call(state, "__next_work", args))["offer"]!["taskId"]!.GetValue<string>());
        Assert.Equal("busy", Data(Call(state, "__next_work", args, 60))["offer"]!["taskId"]!.GetValue<string>());
        Assert.Null(Data(Call(state, "task_get", new() { ["taskId"] = "a" }))["owner"]);
        Assert.Equal("WorkflowNotReady", Call(state, "task_claim", new() { ["taskId"] = "targeted", ["sessionId"] = "worker", ["expectedVersion"] = 1 }).Code);
    }

    [Fact]
    public void Oversized_offer_returns_fetch_marker_and_delta_checkpoint_does_not_change_on_read()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        var workflow = Workflow(); workflow["packet"]!["goal"] = new string('x', 2048);
        Assert.True(Call(state, "task_update", Update("large", 0, workflow)).Ok);
        var offer = Data(Call(state, "__next_work", new() { ["sessionId"] = "worker" }))["offer"]!;
        Assert.True(offer["detailFetchRequired"]!.GetValue<bool>()); Assert.Null(offer["workflow"]);
        Assert.Equal(1, Data(Call(state, "task_get", new()))["boardRevision"]!.GetValue<long>());
        Assert.Empty(Data(Call(state, "task_get", new() { ["sinceRevision"] = 1 }))["items"]!.AsArray());
        Assert.Equal("RepositoryDenied", CoordinationStateMachine.Apply(state, "board", "key", new("other", false, ["foreign"], true), "__next_work", new() { ["repositoryId"] = "repo", ["sessionId"] = "worker" }, Now).Code);
    }

    [Fact]
    public async Task Optional_offer_read_failure_keeps_committed_milestone_successful()
    {
        var factory = DispatchProxy.Create<IGrainFactory, NextFailureFactory>();
        var app = new RelayApplication(factory);
        async Task<RelayReply> Invoke(string tool, JsonObject args)
        {
            args["repositoryId"] = "repo"; args["requestId"] = Guid.NewGuid().ToString("N");
            return await app.ExecuteAsync(Caller with { IsInternal = false }, tool, JsonSerializer.SerializeToElement(args));
        }
        var session = Data(await Invoke("session_open", new()));
        var milestone = new JsonObject { ["sessionId"] = session["sessionId"]!.DeepClone(), ["sessionToken"] = session["sessionToken"]!.DeepClone(), ["taskId"] = "report", ["expectedVersion"] = 0, ["includeNextWork"] = true, ["patch"] = new JsonObject { ["milestone"] = "reported once" } };
        ((NextFailureFactory)(object)factory).Fail = true;
        var result = await Invoke("task_update", milestone);
        Assert.True(result.Ok); Assert.Equal(1, result.Version);
        Assert.Equal("Unavailable", Data(result)["nextWork"]!["code"]!.GetValue<string>());
        Assert.False(Data(result)["nextWork"]!["ok"]!.GetValue<bool>());
        Assert.Equal("reported once", Data(await Invoke("task_get", new() { ["taskId"] = "report" }))["facts"]!["milestone"]!.GetValue<string>());
    }

    public class NextFailureFactory : CoordinationTests.TestGrainFactory
    {
        public bool Fail { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Fail && targetMethod?.IsGenericMethod == true && targetMethod.GetGenericArguments()[0] == typeof(IControlGrain) ? new FailingControl() : base.Invoke(targetMethod, args);
    }

    private sealed class FailingControl : IControlGrain
    {
        public Task<RelayReply> ExecuteAsync(RelayCaller caller, string operation, string argumentsJson) => throw new InvalidOperationException("Optional control read unavailable");
    }

    [Fact]
    public void Board_requireResources_refuses_ready_without_packet_resources()
    {
        var plain = new StateEnvelope<CoordinationDocument>();
        Assert.True(Call(plain, "task_update", Update("t", 0, Workflow())).Ok);

        var state = new StateEnvelope<CoordinationDocument> { Value = new() { Json = "{\"standing\":{\"settings\":{\"requireResources\":true}}}" } };
        var refused = Call(state, "task_update", Update("t", 0, Workflow()));
        Assert.Equal("InvalidWorkflow", refused.Code);
        Assert.Equal("packet.resources", Data(refused)["required"]!.GetValue<string>());
        var blocked = Workflow("blocked"); blocked["blockers"]!.AsArray().Add(new JsonObject { ["id"] = "hold", ["kind"] = "coordinator_hold", ["summary"] = "Waiting", ["recheckCondition"] = "Released" });
        Assert.True(Call(state, "task_update", Update("held", 0, blocked)).Ok);
        var ready = Workflow(); ready["packet"]!["resources"] = new JsonArray("rig-1");
        Assert.True(Call(state, "task_update", Update("t", 0, ready)).Ok);
        var none = Workflow(); none["packet"]!["resources"] = new JsonArray();
        Assert.True(Call(state, "task_update", Update("no-resources", 0, none)).Ok);
        var duplicate = Workflow(); duplicate["packet"]!["resources"] = new JsonArray("rig-1", "rig-1");
        Assert.Equal("InvalidWorkflow", Call(state, "task_update", Update("bad", 0, duplicate)).Code);
        var tooMany = Workflow(); tooMany["packet"]!["resources"] = new JsonArray(Enumerable.Range(0, 17).Select(i => (JsonNode)("rig-" + i)).ToArray());
        Assert.Equal("InvalidWorkflow", Call(state, "task_update", Update("bad", 0, tooMany)).Code);
    }

    private static JsonObject WithResources(int priority, params string[] resources)
    {
        var workflow = Workflow(); workflow["priority"] = priority;
        workflow["packet"]!["resources"] = new JsonArray(resources.Select(id => (JsonNode)id).ToArray());
        return workflow;
    }

    [Fact]
    public async Task Next_work_prefers_free_resources_at_equal_priority_and_still_requires_claim()
    {
        var factory = DispatchProxy.Create<IGrainFactory, LockCountingFactory>();
        var app = new RelayApplication(factory);
        async Task<RelayReply> Invoke(string tool, JsonObject args)
        {
            args["repositoryId"] = "repo"; args["requestId"] = Guid.NewGuid().ToString("N");
            return await app.ExecuteAsync(Caller with { IsInternal = false }, tool, JsonSerializer.SerializeToElement(args));
        }
        var worker = Data(await Invoke("session_open", new())); var holder = Data(await Invoke("session_open", new()));
        JsonObject For(JsonObject session) => new() { ["sessionId"] = session["sessionId"]!.DeepClone(), ["sessionToken"] = session["sessionToken"]!.DeepClone(), ["machineId"] = "pc" };
        foreach (var resource in new[] { "held", "free" }) Assert.True((await Invoke("control_request", new() { ["command"] = "register_resource", ["resourceId"] = resource, ["machineId"] = "pc" })).Ok);
        var hold = For(holder); hold["resourceId"] = "held";
        var grant = Data(await Invoke("claim_acquire", hold));
        async Task Create(string id, JsonObject? workflow)
        {
            var args = For(worker); args["taskId"] = id; args["expectedVersion"] = 0;
            if (workflow is not null) args["workflow"] = workflow;
            Assert.True((await Invoke("task_update", args)).Ok);
        }
        await Create("current", null);
        await Create("a", WithResources(5, "held"));
        await Create("b", WithResources(5, "free"));
        await Create("c", WithResources(1));
        var milestone = For(worker); milestone["taskId"] = "current"; milestone["expectedVersion"] = 1; milestone["includeNextWork"] = true;
        var lookups = ((LockCountingFactory)(object)factory).Lookups;
        var reply = await Invoke("task_update", milestone);
        var next = Data(reply)["nextWork"]!;
        Assert.Equal("b", next["offer"]!["taskId"]!.GetValue<string>());
        Assert.True(next["offer"]!["claimRequired"]!.GetValue<bool>());
        Assert.Null(next["candidates"]);
        Assert.Equal(2, ((LockCountingFactory)(object)factory).Lookups - lookups);
        Assert.Null(Data(await Invoke("task_get", new() { ["taskId"] = "b" }))["owner"]);

        // Once the resource is free again, task ID breaks the tie as before.
        var release = For(holder); release["resourceId"] = "held"; release["generation"] = grant["generation"]!.DeepClone();
        Assert.True((await Invoke("claim_release", release)).Ok);
        milestone["expectedVersion"] = 2;
        Assert.Equal("a", Data(await Invoke("task_update", milestone))["nextWork"]!["offer"]!["taskId"]!.GetValue<string>());

        // The offer acquired nothing: the worker still claims the task and its resources itself.
        var claim = For(worker); claim["taskId"] = "b"; claim["expectedVersion"] = 1; claim["resourceIds"] = new JsonArray("free");
        Assert.True((await Invoke("task_claim", claim)).Ok);
    }

    [Fact]
    public async Task Next_work_resource_lookups_are_capped_per_offer()
    {
        var factory = DispatchProxy.Create<IGrainFactory, LockCountingFactory>();
        var app = new RelayApplication(factory);
        async Task<RelayReply> Invoke(string tool, JsonObject args)
        {
            args["repositoryId"] = "repo"; args["requestId"] = Guid.NewGuid().ToString("N");
            return await app.ExecuteAsync(Caller with { IsInternal = false }, tool, JsonSerializer.SerializeToElement(args));
        }
        var worker = Data(await Invoke("session_open", new()));
        JsonObject For() => new() { ["sessionId"] = worker["sessionId"]!.DeepClone(), ["sessionToken"] = worker["sessionToken"]!.DeepClone(), ["machineId"] = "pc" };
        var create = For(); create["taskId"] = "current"; create["expectedVersion"] = 0; Assert.True((await Invoke("task_update", create)).Ok);
        for (var task = 0; task < 6; task++)
        {
            var resources = Enumerable.Range(0, 16).Select(i => $"r{task}-{i}").ToArray();
            foreach (var resource in resources) Assert.True((await Invoke("control_request", new() { ["command"] = "register_resource", ["resourceId"] = resource, ["machineId"] = "pc" })).Ok);
            var args = For(); args["taskId"] = "t" + task; args["expectedVersion"] = 0; args["workflow"] = WithResources(1, resources);
            Assert.True((await Invoke("task_update", args)).Ok);
        }
        var milestone = For(); milestone["taskId"] = "current"; milestone["expectedVersion"] = 1; milestone["includeNextWork"] = true;
        var reply = await Invoke("task_update", milestone);
        Assert.Equal("t0", Data(reply)["nextWork"]!["offer"]!["taskId"]!.GetValue<string>());
        Assert.Equal(32, ((LockCountingFactory)(object)factory).Lookups);
    }

    public class LockCountingFactory : CoordinationTests.TestGrainFactory
    {
        private int lookups;
        public int Lookups => Volatile.Read(ref lookups);
        internal void Count() => Interlocked.Increment(ref lookups);
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var grain = base.Invoke(targetMethod, args);
            return targetMethod!.GetGenericArguments()[0] == typeof(ILockGrain) ? new CountingLock((ILockGrain)grain!, this) : grain;
        }
    }

    private sealed class CountingLock(ILockGrain inner, LockCountingFactory owner) : ILockGrain
    {
        public Task<RelayReply> ExecuteAsync(RelayCaller caller, string operation, string argumentsJson)
        {
            if (operation == Operations.ClaimState) owner.Count();
            return inner.ExecuteAsync(caller, operation, argumentsJson);
        }
    }

    [Fact]
    public async Task Report_offer_is_advisory_targeted_paused_authenticated_and_bounded()
    {
        var factory = DispatchProxy.Create<IGrainFactory, CoordinationTests.TestGrainFactory>();
        var app = new RelayApplication(factory);
        async Task<RelayReply> Invoke(string tool, JsonObject args)
        {
            args["repositoryId"] = "repo"; args["requestId"] = Guid.NewGuid().ToString("N");
            return await app.ExecuteAsync(Caller with { IsInternal = false }, tool, JsonSerializer.SerializeToElement(args));
        }
        var session = Data(await Invoke("session_open", new()));
        JsonObject For() => new() { ["sessionId"] = session["sessionId"]!.DeepClone(), ["sessionToken"] = session["sessionToken"]!.DeepClone() };
        async Task Create(string id, JsonObject? workflow)
        {
            var args = For(); args["taskId"] = id; args["expectedVersion"] = 0;
            if (workflow is not null) args["workflow"] = workflow;
            Assert.True((await Invoke("task_update", args)).Ok);
        }
        await Create("current", null); await Create("legacy", null);
        var foreign = Workflow(); foreign["targetSessionId"] = "different"; foreign["priority"] = 100;
        await Create("foreign", foreign); await Create("ready", Workflow());
        var milestone = For(); milestone["taskId"] = "current"; milestone["expectedVersion"] = 1; milestone["includeNextWork"] = true;
        var reported = await Invoke("task_update", milestone); Assert.True(reported.Ok);
        var offer = Data(reported)["nextWork"]!["offer"]!;
        Assert.Equal("ready", offer["taskId"]!.GetValue<string>()); Assert.True(offer["claimRequired"]!.GetValue<bool>());
        Assert.Equal(1, offer["expectedVersion"]!.GetValue<long>());
        Assert.Null(Data(await Invoke("task_get", new() { ["taskId"] = "ready" }))["owner"]);
        Assert.True((await Invoke("control_request", new() { ["command"] = "pause" })).Ok);
        milestone["expectedVersion"] = 2;
        Assert.Equal("Paused", Data(await Invoke("task_update", milestone))["nextWork"]!["code"]!.GetValue<string>());
        milestone["expectedVersion"] = 3; milestone["sessionToken"] = "forged";
        Assert.Equal("SessionDenied", (await Invoke("task_update", milestone)).Code);
        milestone["sessionToken"] = session["sessionToken"]!.DeepClone(); milestone["notes"] = new string('"', 2000);
        var bounded = await Invoke("task_update", milestone); Assert.True(bounded.Ok, bounded.Code);
        Assert.True(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(bounded)) <= 32768);
    }
}
