using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orleans;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
using Xunit;

namespace CoCern.Tests;

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

    private static JsonObject IneligibleWorkflow(string change)
    {
        var workflow = Workflow(change == "retargeted" ? "ready" : change);
        if (change == "retargeted") workflow["targetSessionId"] = "another-session";
        if (change == "blocked") workflow["blockers"]!.AsArray().Add(new JsonObject { ["id"] = "missing", ["kind"] = "prerequisite", ["summary"] = "Prerequisite missing", ["recheckCondition"] = "Prerequisite changes" });
        if (change == "completed") { workflow["criteria"]![0]!["result"] = "passed"; workflow["criteria"]![0]!["evidenceRef"] = "artifact:reported"; }
        return workflow;
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

    public class NextFailureFactory : CoordinationTests.TestGrainFactory
    {
        public bool Fail { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Fail && targetMethod?.IsGenericMethod == true && targetMethod.GetGenericArguments()[0] == typeof(IControlGrain) ? new FailingControl() : base.Invoke(targetMethod, args);
    }

    private sealed class FailingControl : IControlGrain
    {
        public Task<RelayReply> ExecuteAsync(RelayCaller caller, string operation, string argumentsJson) => throw new InvalidOperationException("Optional control read unavailable");
    }

    private static JsonObject WithResources(int priority, params string[] resources)
    {
        var workflow = Workflow(); workflow["priority"] = priority;
        workflow["packet"]!["resources"] = new JsonArray(resources.Select(id => (JsonNode)id).ToArray());
        return workflow;
    }

    /// <summary>Counts lock claim-state lookups and control resolves, single and batched.</summary>
    public class LockCountingFactory : CoordinationTests.TestGrainFactory
    {
        private int lookups, resolves, batchResolves;
        public int Lookups => Volatile.Read(ref lookups);
        public int Resolves => Volatile.Read(ref resolves);
        public int BatchResolves => Volatile.Read(ref batchResolves);
        internal void Count(string operation)
        {
            if (operation == Operations.ClaimState) Interlocked.Increment(ref lookups);
            else if (operation == Operations.Resolve) Interlocked.Increment(ref resolves);
            else if (operation == Operations.ResolveAll) Interlocked.Increment(ref batchResolves);
        }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var grain = base.Invoke(targetMethod, args);
            var type = targetMethod!.GetGenericArguments()[0];
            return type == typeof(ILockGrain) || type == typeof(IControlGrain) ? new CountingGrain((IRelayAggregateGrain)grain!, this) : grain;
        }
    }

    private sealed class CountingGrain(IRelayAggregateGrain inner, LockCountingFactory owner) : ILockGrain, IControlGrain
    {
        public Task<RelayReply> ExecuteAsync(RelayCaller caller, string operation, string argumentsJson)
        {
            owner.Count(operation);
            return inner.ExecuteAsync(caller, operation, argumentsJson);
        }
    }

    private static RelayReply Standing(StateEnvelope<CoordinationDocument> state, string operation, JsonObject args, RelayCaller? caller = null, int seconds = 0)
    {
        args["repositoryId"] = "repo"; args["requestId"] ??= Guid.NewGuid().ToString("N");
        return CoordinationStateMachine.Apply(state, "board", "repo:default", caller ?? Caller, operation, args, Now.AddSeconds(seconds));
    }
    private static JsonObject Put(long revision, string field, JsonNode value) => new() { ["expectedRevision"] = revision, [field] = value };
    private static JsonObject Grant(string id, string scope, int expiresInSeconds, params string[] targets)
    {
        var grant = new JsonObject { ["id"] = id, ["scope"] = scope, ["expiresAt"] = Now.AddSeconds(expiresInSeconds).ToString("O") };
        if (targets.Length > 0) grant["targets"] = new JsonArray(targets.Select(target => (JsonNode)target).ToArray());
        return grant;
    }
    private static JsonObject Authorized(string authorityRef, params string[] resources)
    {
        var workflow = Workflow(); workflow["packet"]!["authorityRef"] = authorityRef;
        if (resources.Length > 0) workflow["packet"]!["resources"] = new JsonArray(resources.Select(id => (JsonNode)id).ToArray());
        return workflow;
    }

    [Fact]
    public void Readiness_refuses_an_unknown_expired_or_out_of_scope_grant_in_authorityRef()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        var grants = new JsonArray(Grant("rigs", "resource", 3600, "rig-1", "rig-2"), Grant("repo-wide", "repository", 3600), Grant("deploy", "action", 3600, "deploy-css"), Grant("short", "resource", 60, "rig-1"));
        Assert.True(Standing(state, "standing_put", Put(0, "grants", grants)).Ok);
        string Reason(RelayReply reply) { Assert.Equal("GrantInvalid", reply.Code); return Data(reply)["reason"]!.GetValue<string>(); }

        Assert.True(Call(state, "task_update", Update("covered", 0, Authorized("grant:rigs", "rig-1", "rig-2"))).Ok);
        Assert.True(Call(state, "task_update", Update("repository", 0, Authorized("grant:repo-wide"))).Ok);
        Assert.True(Call(state, "task_update", Update("prose", 0, Workflow())).Ok);
        Assert.Equal("scope", Reason(Call(state, "task_update", Update("other-rig", 0, Authorized("grant:rigs", "rig-1", "rig-3")))));
        Assert.Equal("scope", Reason(Call(state, "task_update", Update("no-resources", 0, Authorized("grant:rigs")))));
        Assert.Equal("scope", Reason(Call(state, "task_update", Update("action", 0, Authorized("grant:deploy", "rig-1")))));
        Assert.Equal("unknown", Reason(Call(state, "task_update", Update("missing", 0, Authorized("grant:missing")))));
        // A blocked or completed packet is not a readiness claim, so its grant is not checked.
        var blocked = Authorized("grant:missing"); blocked["state"] = "blocked";
        blocked["blockers"]!.AsArray().Add(new JsonObject { ["id"] = "hold", ["kind"] = "coordinator_hold", ["summary"] = "Waiting", ["recheckCondition"] = "Released" });
        Assert.True(Call(state, "task_update", Update("held", 0, blocked)).Ok);

        // A grant that was valid when the task became ready stops it being offered once it expires, and refuses a new ready write.
        Assert.True(Call(state, "task_update", Update("expiring", 0, Authorized("grant:short", "rig-1"))).Ok);
        string Offered(int seconds)
        {
            var reply = Data(Call(state, "__next_work", new() { ["sessionId"] = "worker", ["limit"] = 5 }, seconds));
            return string.Join(",", reply["candidates"]!.AsArray().Select(candidate => candidate!["taskId"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        }
        Assert.Equal("covered,expiring,prose,repository", Offered(0));
        Assert.Equal("covered,prose,repository", Offered(120));
        Assert.Equal("expired", Reason(Call(state, "task_update", Update("expiring", 1, Authorized("grant:short", "rig-1")), 120)));

        // Removing a grant makes the tasks that name it unknown, and they are no longer offered.
        Assert.True(Standing(state, "standing_put", Put(1, "remove", new JsonArray("rigs"))).Ok);
        Assert.Equal("expiring,prose,repository", Offered(0));
    }

    [Fact]
    public void Claim_and_action_begin_recheck_the_grant_named_in_authorityRef()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        Assert.True(Standing(state, "standing_put", Put(0, "grants", new JsonArray(Grant("short", "resource", 60, "rig-1"), Grant("rigs", "resource", 3600, "rig-2")))).Ok);
        foreach (var id in new[] { "early", "late", "begin-late", "retried" }) Assert.True(Call(state, "task_update", Update(id, 0, Authorized("grant:short", "rig-1"))).Ok);
        foreach (var id in new[] { "removed", "begin-removed", "rescoped" }) Assert.True(Call(state, "task_update", Update(id, 0, Authorized("grant:rigs", "rig-2"))).Ok);
        // Each claim takes the resource its packet declares.
        RelayReply Claim(string id, int seconds, string? requestId = null) =>
            Call(state, "task_claim", new() { ["taskId"] = id, ["sessionId"] = "worker", ["requestId"] = requestId ?? Guid.NewGuid().ToString("N"), ["expectedVersion"] = 1, ["leaseSeconds"] = RelayLimits.MaxLeaseSeconds,
                ["resourceIds"] = new JsonArray(id is "removed" or "begin-removed" or "rescoped" ? "rig-2" : "rig-1") }, seconds);
        RelayReply Begin(string id, int seconds) =>
            Call(state, "action_begin", new() { ["taskId"] = id, ["sessionId"] = "worker", ["requestId"] = Guid.NewGuid().ToString("N"), ["expectedVersion"] = 2, ["actionId"] = "deploy", ["inputFingerprint"] = "f" }, seconds);
        // The same code and reasons readiness gives.
        string Reason(RelayReply reply) { Assert.Equal("GrantInvalid", reply.Code); return Data(reply)["reason"]!.GetValue<string>(); }

        // Before the 60-second grant expires, claim and begin work.
        Assert.True(Claim("early", 0).Ok);
        Assert.True(Begin("early", 30).Ok);
        Assert.True(Claim("begin-late", 30).Ok);
        var retried = Claim("retried", 30, "claim-retried");
        Assert.True(retried.Ok);
        Assert.Equal(retried, Claim("retried", 59, "claim-retried"));
        // After it, a new claim, a retried claim and a new action start are all refused, and none changes state.
        var before = state.Value.Json;
        var late = Claim("late", 120);
        Assert.Equal("expired", Reason(late));
        Assert.Equal("grant:short", Data(late)["authorityRef"]!.GetValue<string>());
        Assert.Equal("expired", Reason(Claim("retried", 120, "claim-retried")));
        Assert.Equal("expired", Reason(Begin("begin-late", 120)));
        Assert.Equal(before, state.Value.Json);

        // A grant removed by standing_put refuses both as unknown; one rescoped to an action grant refuses as scope.
        Assert.True(Claim("begin-removed", 0).Ok);
        var replaced = Grant("rigs", "action", 3600, "deploy-css");
        Assert.True(Standing(state, "standing_put", Put(1, "grants", new JsonArray(replaced))).Ok);
        Assert.Equal("scope", Reason(Claim("rescoped", 0)));
        Assert.True(Standing(state, "standing_put", Put(2, "remove", new JsonArray("rigs"))).Ok);
        Assert.Equal("unknown", Reason(Claim("removed", 0)));
        Assert.Equal("unknown", Reason(Begin("begin-removed", 0)));
    }

    private static JsonObject WideGrant(string id) =>
        Grant(id, "resource", 3600, Enumerable.Range(0, RelayLimits.MaxGrantTargets).Select(i => id + "-" + i + "-" + new string('r', RelayLimits.MaxIdentifierLength - 12)).ToArray());

    [Fact]
    public void Standing_put_needs_an_administrator_and_the_expected_revision_within_limits()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        var line = new JsonArray(new JsonObject { ["id"] = "css", ["text"] = "Never deploy CSS without a restoration receipt." });
        Assert.Equal("AdministratorRequired", Standing(state, "standing_put", Put(0, "lines", line.DeepClone()), Caller with { IsAdministrator = false }).Code);
        Assert.Equal("VersionConflict", Standing(state, "standing_put", Put(1, "lines", line.DeepClone())).Code);
        Assert.Equal("InvalidArguments", Standing(state, "standing_put", new() { ["expectedRevision"] = 0 }).Code);
        Assert.Equal("InvalidArguments", Standing(state, "standing_put", Put(0, "lines", new JsonArray(new JsonObject { ["id"] = "long", ["text"] = new string('x', 201) }))).Code);
        Assert.Equal("CapacityExceeded", Standing(state, "standing_put", Put(0, "lines", new JsonArray(Enumerable.Range(0, 31).Select(i => (JsonNode)new JsonObject { ["id"] = "l" + i, ["text"] = "Line " + i }).ToArray()))).Code);
        Assert.Equal("InvalidArguments", Standing(state, "standing_put", Put(0, "grants", new JsonArray(Grant("past", "resource", -1, "rig-1")))).Code);
        Assert.Equal("InvalidArguments", Standing(state, "standing_put", Put(0, "grants", new JsonArray(Grant("elsewhere", "repository", 60, "other-repo")))).Code);
        Assert.Equal("InvalidArguments", Standing(state, "standing_put", Put(0, "settings", new JsonObject { ["unknown"] = true })).Code);
        Assert.Equal("NotFound", Standing(state, "standing_put", Put(0, "remove", new JsonArray("absent"))).Code);
        Assert.DoesNotContain("standing", state.Value.Json);

        var put = Standing(state, "standing_put", Put(0, "lines", line.DeepClone()));
        Assert.True(put.Ok); Assert.Equal(1, put.Version); Assert.Equal(1, Data(put)["revision"]!.GetValue<long>());
        Assert.Equal("InvalidArguments", Standing(state, "standing_put", Put(1, "grants", new JsonArray(Grant("css", "resource", 60, "rig-1")))).Code);

        // requireResources (build step 2) is the board setting standing_put writes.
        Assert.True(Call(state, "task_update", Update("before", 0, Workflow())).Ok);
        Assert.True(Standing(state, "standing_put", Put(1, "settings", new JsonObject { ["requireResources"] = true })).Ok);
        Assert.Equal("packet.resources", Data(Call(state, "task_update", Update("after", 0, Workflow())))["required"]!.GetValue<string>());
        Assert.True(Standing(state, "standing_put", Put(2, "settings", new JsonObject { ["requireResources"] = false })).Ok);
        Assert.True(Call(state, "task_update", Update("after", 0, Workflow())).Ok);

        // Every change advances the revision once; the history keeps the latest entries, newest first, with who and what.
        for (var revision = 3; revision < 3 + RelayLimits.StandingHistoryEntries + 2; revision++)
            Assert.True(Standing(state, "standing_put", Put(revision, "lines", new JsonArray(new JsonObject { ["id"] = "rev", ["text"] = "Revision " + revision }))).Ok);
        var view = Data(Call(state, "task_get", new() { ["section"] = "standing" }));
        var history = view["history"]!.AsArray();
        Assert.Equal(RelayLimits.StandingHistoryEntries, history.Count);
        Assert.Equal(3 + RelayLimits.StandingHistoryEntries + 2, history[0]!["revision"]!.GetValue<long>());
        Assert.Equal("alice", history[0]!["by"]!.GetValue<string>());
        Assert.Equal("~line:rev", history[0]!["changes"]![0]!.GetValue<string>());
        Assert.Equal(2, view["lines"]!.AsArray().Count);
        Assert.Equal("alice", view["lines"]![0]!["author"]!.GetValue<string>());
        Assert.False(view["settings"]!["requireResources"]!.GetValue<bool>());
    }

    [Fact]
    public void Proposals_take_effect_only_when_an_administrator_approves_them()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        var bob = new RelayCaller("bob", false, ["repo"], true);
        string Propose(JsonObject args, string session = "bob-session") { args["sessionId"] = session; var reply = Standing(state, "standing_propose", args, bob); Assert.True(reply.Ok, reply.Code); return Data(reply)["proposalId"]!.GetValue<string>(); }
        RelayReply Delivered(int seconds = 0) => Call(state, "__standing", new(), seconds);

        Assert.Equal("InvalidArguments", Standing(state, "standing_propose", new() { ["sessionId"] = "s" }, bob).Code);
        Assert.Equal("InvalidArguments", Standing(state, "standing_propose", new() { ["sessionId"] = "s", ["line"] = new JsonObject { ["text"] = "a" }, ["grant"] = Grant("g", "repository", 60) }, bob).Code);
        var line = Propose(new() { ["line"] = new JsonObject { ["text"] = "Run the smoke suite before every merge." } });
        var grant = Propose(new() { ["grant"] = Grant("bench", "resource", 3600, "rig-1") });
        var rejected = Propose(new() { ["line"] = new JsonObject { ["id"] = "rejected", ["text"] = "Skip review." } });
        var stale = Propose(new() { ["grant"] = Grant("brief", "resource", 60, "rig-1") });

        // Pending proposals are not standing: the revision does not move, nothing is delivered, and a named grant does not authorize work.
        Assert.Equal(0, Delivered().Version);
        Assert.Empty(Data(Delivered())["lines"]!.AsArray());
        Assert.Equal("unknown", Data(Call(state, "task_update", Update("t", 0, Authorized("grant:bench", "rig-1"))))["reason"]!.GetValue<string>());
        Assert.Equal(4, Data(Call(state, "task_get", new() { ["section"] = "standing" }))["proposals"]!.AsArray().Count);
        Assert.Equal("AdministratorRequired", Standing(state, "standing_put", Put(0, "approve", new JsonArray(line)), bob).Code);

        Assert.True(Standing(state, "standing_put", new() { ["expectedRevision"] = 0, ["approve"] = new JsonArray(line, grant), ["remove"] = new JsonArray(rejected) }).Ok);
        Assert.Equal("GrantInvalid", Standing(state, "standing_put", Put(1, "approve", new JsonArray(stale)), seconds: 120).Code);
        var delivered = Data(Delivered());
        Assert.Equal(1, delivered["revision"]!.GetValue<long>());
        Assert.Equal("Run the smoke suite before every merge.", Assert.Single(delivered["lines"]!.AsArray())!["text"]!.GetValue<string>());
        Assert.Equal("bench", Assert.Single(delivered["grants"]!.AsArray())!["id"]!.GetValue<string>());
        Assert.Null(delivered["lines"]![0]!["author"]);
        var view = Data(Call(state, "task_get", new() { ["section"] = "standing" }));
        Assert.Equal("bob", view["lines"]![0]!["author"]!.GetValue<string>());
        Assert.Equal("alice", view["lines"]![0]!["approvedBy"]!.GetValue<string>());
        Assert.Equal("bob", view["grants"]![0]!["proposedBy"]!.GetValue<string>());
        Assert.Equal("alice", view["grants"]![0]!["grantor"]!.GetValue<string>());
        Assert.Equal(stale, Assert.Single(view["proposals"]!.AsArray())!["id"]!.GetValue<string>());
        Assert.True(Call(state, "task_update", Update("t", 0, Authorized("grant:bench", "rig-1"))).Ok);
        // Expired grants stay on record for review but are no longer delivered.
        Assert.Empty(Data(Delivered(7200))["grants"]!.AsArray());
        Assert.True(Data(Call(state, "task_get", new() { ["section"] = "standing" }, 7200))["grants"]![0]!["expired"]!.GetValue<bool>());

        // One session may keep at most 5 proposals waiting; the board keeps at most 20 in all.
        for (var i = 1; i < RelayLimits.MaxStandingProposalsPerSession; i++) Propose(new() { ["line"] = new JsonObject { ["text"] = "Proposal " + i } });
        var sixth = Standing(state, "standing_propose", new() { ["sessionId"] = "bob-session", ["line"] = new JsonObject { ["text"] = "Sixth" } }, bob);
        Assert.Equal("CapacityExceeded", sixth.Code);
        Assert.Equal(RelayLimits.MaxStandingProposalsPerSession, Data(sixth)["maxPerSession"]!.GetValue<int>());
        for (var i = RelayLimits.MaxStandingProposalsPerSession; i < RelayLimits.MaxStandingProposals; i++) Propose(new() { ["line"] = new JsonObject { ["text"] = "Proposal " + i } }, "session-" + i);
        Assert.Equal("CapacityExceeded", Standing(state, "standing_propose", new() { ["sessionId"] = "s", ["line"] = new JsonObject { ["text"] = "One too many" } }, bob).Code);
    }

}
