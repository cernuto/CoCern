using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain;
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
/// </summary>
internal static class DomainBoard
{
    public static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-03T00:00:00Z");
    public static readonly RelayCaller Writer = new("alice", false, ["repo"], true);
    public static readonly RelayCaller Administrator = new("bob", true, ["repo"], true);
    public static readonly string Key = AggregateKinds.BoardKey("repo", AggregateKinds.DefaultBoard);

    /// <summary>The expiry of the grant the administrator put, and of the grants proposed.</summary>
    public static readonly DateTimeOffset GrantExpiry = Now.AddDays(1);
    public static readonly DateTimeOffset ProposedGrantExpiry = Now.AddDays(2);

    /// <summary>The proposal IDs the board generated: a new line, a line replacing <c>css</c>, a grant replacing <c>rigs</c>, and the grant that was approved.</summary>
    public sealed record Proposals(string NewLine, string ReplacingLine, string ReplacingGrant, string ApprovedGrant);

    public static (StateEnvelope<CoordinationDocument> Board, Proposals Proposals) Build()
    {
        var board = new StateEnvelope<CoordinationDocument>();
        JsonObject Apply(RelayCaller caller, string operation, JsonObject args, int seconds = 0)
        {
            args["repositoryId"] = "repo";
            args["requestId"] ??= Guid.NewGuid().ToString("N");
            var reply = CoordinationStateMachine.Apply(board, AggregateKinds.Board, Key, caller, operation, args, Now.AddSeconds(seconds), ordersEnabled: true);
            Assert.True(reply.Ok, $"{operation}: {reply.Code} {reply.DataJson}");
            return JsonNode.Parse(reply.DataJson)!.AsObject();
        }
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
        Assert.True(Apply(Writer, Operations.OrderFire, new() { ["resourceId"] = "rig-1", ["generation"] = 1 }, 140)["ran"]!.GetValue<bool>());
        for (var attempt = 1; attempt <= RelayLimits.MaxOrderFailures; attempt++)
            Assert.True(Apply(Writer, Operations.OrderFailed, new() { ["orderId"] = "o-2", ["watch"] = true, ["reason"] = "watch refused " + attempt }, 140 + attempt)["counted"]!.GetValue<bool>());
        return (board, new(newLine, replacingLine, replacingGrant, approvedGrant));
    }
}
