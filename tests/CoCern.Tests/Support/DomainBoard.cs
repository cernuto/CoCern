using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain;
using CoCern.Domain.Messages;
using Xunit;

namespace CoCern.Tests;

/// <summary>
/// One board holding every kind of entry that readers of stored board state name, built through <see cref="CoordinationStateMachine.Apply"/>
/// as the hub builds it:
/// <list type="bullet">
/// <item>a planned task whose packet declares resources;</item>
/// <item>a task claimed and worked, whose action an administrator's recovery abandoned and its writer then completed after all, with a message;</item>
/// <item>standing lines, grants and a setting; proposals of each kind, some that would replace a line or grant, one approved; and their history;</item>
/// <item>a standing order on a task, and one on a resource that ran and then failed until it was disabled; and the order log.</item>
/// </list>
/// <see cref="BuildWithEveryKey"/> takes the same board further, until it holds every key a board stores.
/// </summary>
internal static class DomainBoard
{
    public static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-03T00:00:00Z");
    public static readonly RelayCaller Writer = new("alice", false, ["repo"], true);
    public static readonly RelayCaller Administrator = new("bob", true, ["repo"], true);
    /// <summary>The identity the hub proposes its own standing lines under.</summary>
    public static readonly RelayCaller HubProposer = new(SessionRoles.HubProposer, false, ["repo"], true);
    public static readonly string Key = AggregateKinds.BoardKey("repo", AggregateKinds.DefaultBoard);

    /// <summary>The expiry of the grant the administrator put, and of the grants proposed.</summary>
    public static readonly DateTimeOffset GrantExpiry = Now.AddDays(1);
    public static readonly DateTimeOffset ProposedGrantExpiry = Now.AddDays(2);

    /// <summary>The proposal IDs the board generated: a new line, a line replacing <c>css</c>, a grant replacing <c>rigs</c>, and the grant that was approved.</summary>
    public sealed record Proposals(string NewLine, string ReplacingLine, string ReplacingGrant, string ApprovedGrant);

    /// <summary>Applies one operation to <paramref name="board"/> at <paramref name="seconds"/> past <see cref="Now"/>, as the hub would, and asserts it succeeded.</summary>
    private static JsonObject Apply(StateEnvelope<CoordinationDocument> board, RelayCaller caller, string operation, JsonObject args, int seconds)
    {
        args["repositoryId"] = "repo";
        args["requestId"] ??= Guid.NewGuid().ToString("N");
        var reply = CoordinationStateMachine.Apply(board, AggregateKinds.Board, Key, caller, operation, args, Now.AddSeconds(seconds), ordersEnabled: true);
        Assert.True(reply.Ok, $"{operation}: {reply.Code} {reply.DataJson}");
        return JsonNode.Parse(reply.DataJson)!.AsObject();
    }

    public static (StateEnvelope<CoordinationDocument> Board, Proposals Proposals) Build()
    {
        var board = new StateEnvelope<CoordinationDocument>();
        JsonObject Apply(RelayCaller caller, string operation, JsonObject args, int seconds = 0) => DomainBoard.Apply(board, caller, operation, args, seconds);
        string Proposed(JsonObject reply) => reply["proposalId"]!.GetValue<string>();

        var workflow = new JsonObject
        {
            ["state"] = "ready",
            ["packet"] = new JsonObject
            {
                ["goal"] = "Deploy the stylesheet", ["target"] = "repo at revision abc", ["authorityRef"] = "human-grant:17", ["restoration"] = "Restore the previous stylesheet",
                ["stopCondition"] = "Stop if the target differs", ["resources"] = new JsonArray("rig-1")
            },
            ["criteria"] = new JsonArray(new JsonObject { ["id"] = "live", ["description"] = "The new stylesheet is live", ["result"] = "not_run" }),
            ["blockers"] = new JsonArray()
        };
        Apply(Writer, RelayTools.TaskUpdate, new() { ["taskId"] = "plan", ["expectedVersion"] = 0, ["workflow"] = workflow });
        Apply(Writer, RelayTools.TaskUpdate, new() { ["taskId"] = "t", ["expectedVersion"] = 0 });
        Apply(Writer, RelayTools.TaskClaim, new() { ["taskId"] = "t", ["sessionId"] = "a", ["expectedVersion"] = 1, ["leaseSeconds"] = 600 });
        Apply(Writer, RelayTools.ActionBegin, new() { ["taskId"] = "t", ["sessionId"] = "a", ["expectedVersion"] = 2, ["actionId"] = "deploy", ["inputFingerprint"] = "f" });
        Apply(Administrator, RelayTools.ActionReport, new() { ["taskId"] = "t", ["sessionId"] = "admin-session", ["expectedVersion"] = 3, ["actionId"] = "deploy", ["attempt"] = 1, ["recover"] = true }, 60);
        Apply(Writer, RelayTools.ActionReport, new() { ["taskId"] = "t", ["sessionId"] = "a", ["expectedVersion"] = 4, ["actionId"] = "deploy", ["attempt"] = 1, ["outcome"] = "completed", ["report"] = "Deployed after all." }, 90);
        Apply(Writer, RelayTools.SendMessage, new() { ["taskId"] = "t", ["sessionId"] = "a", ["text"] = "Deployed; please review." }, 100);

        Apply(Administrator, RelayTools.StandingPut, new()
        {
            ["expectedRevision"] = 0, ["lines"] = new JsonArray(new JsonObject { ["id"] = "css", ["text"] = "Never deploy CSS without a restoration receipt." }),
            ["grants"] = new JsonArray(new JsonObject { ["id"] = "rigs", ["scope"] = "resource", ["targets"] = new JsonArray("rig-1"), ["expiresAt"] = GrantExpiry.ToString("O") }),
            ["settings"] = new JsonObject { ["requireResources"] = true }
        }, 110);
        var newLine = Proposed(Apply(Writer, RelayTools.StandingPropose, new() { ["sessionId"] = "a", ["line"] = new JsonObject { ["text"] = "Report before releasing a rig." } }, 120));
        var replacingLine = Proposed(Apply(Writer, RelayTools.StandingPropose, new() { ["sessionId"] = "a", ["line"] = new JsonObject { ["id"] = "css", ["text"] = "Deploy CSS only with a receipt." } }, 121));
        var replacingGrant = Proposed(Apply(Writer, RelayTools.StandingPropose, new()
        {
            ["sessionId"] = "a", ["grant"] = new JsonObject { ["id"] = "rigs", ["scope"] = "resource", ["targets"] = new JsonArray("rig-1", "rig-2"), ["expiresAt"] = ProposedGrantExpiry.ToString("O") }
        }, 122));
        var approvedGrant = Proposed(Apply(Writer, RelayTools.StandingPropose, new()
        {
            ["sessionId"] = "a", ["grant"] = new JsonObject { ["scope"] = "resource", ["targets"] = new JsonArray("rig-3"), ["expiresAt"] = ProposedGrantExpiry.ToString("O") }
        }, 123));
        Apply(Administrator, RelayTools.StandingPut, new() { ["expectedRevision"] = 1, ["approve"] = new JsonArray(approvedGrant) }, 124);

        Apply(Administrator, RelayTools.OrderPut, new()
        {
            ["orderId"] = "o-1", ["trigger"] = new JsonObject { ["on"] = OrderTriggers.WorkflowCompleted, ["taskId"] = "plan" },
            ["actions"] = new JsonArray(new JsonObject { ["do"] = OrderActions.ReleaseClaims }), ["defaultExpiryDays"] = 7
        }, 130);
        Apply(Administrator, RelayTools.OrderPut, new()
        {
            ["orderId"] = "o-2", ["trigger"] = new JsonObject { ["on"] = OrderTriggers.ClaimReleased, ["resourceId"] = "rig-1" },
            ["actions"] = new JsonArray(new JsonObject { ["do"] = OrderActions.Offer, ["taskId"] = "plan", ["sessionId"] = "b" }, new JsonObject { ["do"] = OrderActions.Notify, ["sessionId"] = "c" })
        }, 131);
        // The lock tells the board its claim was released, and the order runs; then the lock reports, three times, that it cannot watch.
        Assert.True(Apply(Writer, OrderFire.Operation, new OrderFire("repo", AggregateKinds.DefaultBoard, "rig-1", 1).ToJson(), 140)["ran"]!.GetValue<bool>());
        for (var attempt = 1; attempt <= RelayLimits.MaxOrderFailures; attempt++)
            Assert.True(Apply(Writer, OrderWatchFailed.Operation, new OrderWatchFailed("repo", AggregateKinds.DefaultBoard, "o-2", "watch refused " + attempt).ToJson(), 140 + attempt)["counted"]!.GetValue<bool>());
        return (board, new(newLine, replacingLine, replacingGrant, approvedGrant));
    }

    /// <summary>
    /// The board <see cref="Build"/> builds, taken further so that it holds every key a board stores (see <see cref="Domain.State.BoardState"/>),
    /// besides the tombstones only an overfull request map keeps:
    /// <list type="bullet">
    /// <item>a standing put of more changes than one history entry keeps, and the hub's own proposal; a context note;</item>
    /// <item>two tasks completed with evidence, an order on the second whose notice carries a note, and a blocked task, targeted and
    /// prioritized and rechecked once, that depends on both, and one blocked on Paul and on a hold;</item>
    /// <item>a task whose failed action was retried, with a late report on the first attempt;</item>
    /// <item>a task whose session closed with its action in flight, recovered by another session of its principal and completed by a third;</item>
    /// <item>two archive sweeps a month apart: the completed tasks leave for the archive, and their IDs stay remembered as dependencies.</item>
    /// </list>
    /// The tasks it archived are in the envelope's <see cref="StateEnvelope{T}.PendingArchive"/>.
    /// </summary>
    public static StateEnvelope<CoordinationDocument> BuildWithEveryKey()
    {
        var (board, _) = Build();
        JsonObject Apply(RelayCaller caller, string operation, JsonObject args, int seconds) => DomainBoard.Apply(board, caller, operation, args, seconds);
        static JsonObject Workflow(string state, string resource, JsonArray? blockers = null) => new()
        {
            ["state"] = state,
            ["packet"] = new JsonObject
            {
                ["goal"] = "Flash the rig", ["target"] = "rig at revision abc", ["authorityRef"] = "human-grant:17", ["restoration"] = "Flash the previous image",
                ["stopCondition"] = "Stop if the rig differs", ["resources"] = new JsonArray(resource)
            },
            ["criteria"] = new JsonArray(state == "completed"
                ? new JsonObject { ["id"] = "flashed", ["description"] = "The rig runs the new image", ["result"] = "passed", ["evidenceRef"] = "log:flash" }
                : new JsonObject { ["id"] = "flashed", ["description"] = "The rig runs the new image", ["result"] = "not_run" }),
            ["blockers"] = blockers ?? new JsonArray()
        };

        Apply(Administrator, RelayTools.StandingPut, new()
        {
            ["expectedRevision"] = 2,
            ["lines"] = new JsonArray(Enumerable.Range(1, RelayLimits.StandingHistoryChanges + 1).Select(n => (JsonNode)new JsonObject { ["id"] = "l-" + n, ["text"] = "Convention " + n + "." }).ToArray())
        }, 200);
        var pending = Apply(HubProposer, HubStandingProposal.Operation, new HubStandingProposal("repo", null, "tip-board-reread", "tip-x", "Read the board as changes since your last revision.").ToJson(), 201);
        // Exercise the real reservation so strict state-shape coverage includes every presentation marker.
        const string presentationRequest = "check_inbox:shape-fixture";
        var presentation = StandingPresentation.ReadPending(JsonNode.Parse(board.Value.Json)!["standing"]!.AsObject(), AggregateKinds.DefaultBoard,
            pending["proposalId"]!.GetValue<string>(), Writer.PrincipalId, "a", presentationRequest);
        Assert.NotNull(presentation);
        var shown = Apply(Writer, Operations.PresentStanding, new()
        {
            ["requestId"] = "", ["sessionId"] = "a", ["boardId"] = AggregateKinds.DefaultBoard, ["proposalId"] = presentation.ProposalId,
            ["presentationRequest"] = presentationRequest, ["expectedEffect"] = JsonNode.Parse(presentation.SummaryJson)
        }, 201);
        Assert.Equal(presentation.ProposalId, shown["attention"]?["id"]?.GetValue<string>());
        Apply(Writer, RelayTools.ContextPut, new()
        {
            ["contextId"] = "runbook", ["expectedVersion"] = 0, ["content"] = new JsonObject { ["steps"] = new JsonArray("flash", "verify") }, ["provenance"] = new JsonObject { ["source"] = "wiki" }
        }, 202);

        Apply(Writer, RelayTools.TaskUpdate, new() { ["taskId"] = "old", ["expectedVersion"] = 0, ["workflow"] = Workflow("ready", "rig-8") }, 210);
        Apply(Writer, RelayTools.TaskUpdate, new() { ["taskId"] = "old", ["expectedVersion"] = 1, ["workflow"] = Workflow("completed", "rig-8") }, 211);
        Apply(Writer, RelayTools.TaskUpdate, new() { ["taskId"] = "dep", ["expectedVersion"] = 0, ["workflow"] = Workflow("ready", "rig-9") }, 212);
        Apply(Administrator, RelayTools.OrderPut, new()
        {
            ["orderId"] = "o-3", ["trigger"] = new JsonObject { ["on"] = OrderTriggers.WorkflowCompleted, ["taskId"] = "dep" },
            ["actions"] = new JsonArray(new JsonObject { ["do"] = OrderActions.Notify, ["sessionId"] = "c", ["note"] = "dep is done" }), ["defaultExpiryDays"] = 7
        }, 213);
        Apply(Writer, RelayTools.TaskUpdate, new() { ["taskId"] = "dep", ["expectedVersion"] = 1, ["workflow"] = Workflow("completed", "rig-9") }, 214);
        var blocked = Workflow("blocked", "rig-9", new JsonArray(new JsonObject
        {
            ["id"] = "fixture", ["kind"] = "missing_fixture", ["summary"] = "The test fixture is missing", ["recheckCondition"] = "When the fixture is back"
        }));
        blocked["dependsOn"] = new JsonArray("dep", "old"); blocked["targetSessionId"] = "w"; blocked["priority"] = 5; blocked["recheckEvidenceRef"] = "check:1";
        Apply(Writer, RelayTools.TaskUpdate, new() { ["taskId"] = "waiting", ["expectedVersion"] = 0, ["workflow"] = blocked }, 215);
        Apply(Writer, RelayTools.TaskUpdate, new()
        {
            ["taskId"] = "asks-paul", ["expectedVersion"] = 0,
            ["workflow"] = Workflow("blocked", "rig-10", new JsonArray(
                new JsonObject { ["id"] = "paul", ["kind"] = BlockerKinds.HumanDecision, ["summary"] = "Which rig should it flash?", ["recheckCondition"] = "Paul answered" },
                new JsonObject { ["id"] = "hold", ["kind"] = BlockerKinds.CoordinatorHold, ["summary"] = "Held for the answer", ["recheckCondition"] = "Released" }))
        }, 216);

        Apply(Writer, RelayTools.TaskUpdate, new() { ["taskId"] = "retry", ["expectedVersion"] = 0 }, 220);
        Apply(Writer, RelayTools.TaskClaim, new() { ["taskId"] = "retry", ["sessionId"] = "a", ["expectedVersion"] = 1, ["leaseSeconds"] = 600 }, 221);
        Apply(Writer, RelayTools.ActionBegin, new() { ["taskId"] = "retry", ["sessionId"] = "a", ["expectedVersion"] = 2, ["actionId"] = "flash", ["inputFingerprint"] = "f" }, 222);
        Apply(Writer, RelayTools.ActionReport, new() { ["taskId"] = "retry", ["sessionId"] = "a", ["expectedVersion"] = 3, ["actionId"] = "flash", ["attempt"] = 1, ["outcome"] = "failed", ["report"] = "It did not take." }, 223);
        Apply(Writer, RelayTools.ActionBegin, new()
        {
            ["taskId"] = "retry", ["sessionId"] = "a", ["expectedVersion"] = 4, ["actionId"] = "flash", ["inputFingerprint"] = "f", ["explicitRetry"] = true, ["attempt"] = 2
        }, 224);
        Apply(Writer, RelayTools.ActionReport, new() { ["taskId"] = "retry", ["sessionId"] = "a", ["expectedVersion"] = 5, ["actionId"] = "flash", ["attempt"] = 1, ["outcome"] = "failed", ["report"] = "The first attempt failed for good." }, 225);

        Apply(Writer, RelayTools.TaskUpdate, new() { ["taskId"] = "lost", ["expectedVersion"] = 0 }, 230);
        Apply(Writer, RelayTools.TaskClaim, new() { ["taskId"] = "lost", ["sessionId"] = "c1", ["expectedVersion"] = 1, ["leaseSeconds"] = 600 }, 231);
        Apply(Writer, RelayTools.ActionBegin, new() { ["taskId"] = "lost", ["sessionId"] = "c1", ["expectedVersion"] = 2, ["actionId"] = "push", ["inputFingerprint"] = "f" }, 232);
        Apply(Writer, SessionClosed.Operation, new SessionClosed("repo", AggregateKinds.DefaultBoard, "c1").ToJson(), 233);
        Apply(Writer, RelayTools.ActionReport, new() { ["taskId"] = "lost", ["sessionId"] = "c2", ["expectedVersion"] = 4, ["actionId"] = "push", ["attempt"] = 1, ["recover"] = true }, 234);
        Apply(Writer, RelayTools.ActionReport, new() { ["taskId"] = "lost", ["sessionId"] = "c3", ["expectedVersion"] = 5, ["actionId"] = "push", ["attempt"] = 1, ["outcome"] = "completed", ["report"] = "Pushed after all." }, 235);

        const int Day = 24 * 60 * 60;
        Apply(Writer, TaskArchiveSweep.Operation, new TaskArchiveSweep("repo", null).ToJson(), (RelayLimits.DefaultArchiveAfterDays + 1) * Day);
        Apply(Writer, TaskArchiveSweep.Operation, new TaskArchiveSweep("repo", null).ToJson(), (RelayLimits.DefaultArchiveAfterDays + RelayLimits.ArchivedIdDays + 2) * Day);
        return board;
    }
}
