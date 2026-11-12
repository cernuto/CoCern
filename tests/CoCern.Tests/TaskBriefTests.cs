using System.Text;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain;
using CoCern.Host;
using Xunit;
using Xunit.Abstractions;

namespace CoCern.Tests;

public sealed class TaskBriefTests(ITestOutputHelper output)
{
    [Fact]
    public void Brief_preserves_current_authority_fences_and_requires_exact_oversized_sections()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        var now = DateTimeOffset.Parse("2026-11-11T00:00:00Z");
        var caller = new RelayCaller("worker", false, ["repo"], true);
        RelayReply Call(string operation, JsonObject args, int seconds = 0)
        {
            args["repositoryId"] = "repo";
            args["boardId"] = "board-main";
            return CoordinationStateMachine.Apply(state, AggregateKinds.Board, "repo/board-main", caller, operation, args, now.AddSeconds(seconds));
        }
        static JsonObject Data(RelayReply reply) { Assert.True(reply.Ok, reply.Code); return JsonNode.Parse(reply.DataJson)!.AsObject(); }
        static JsonObject Read(string task, string? section = null) => new() { ["taskId"] = task, ["section"] = section };
        const string code = "```csharp\r\nvar id = \"Case-Sensitive-ID\";\r\nif (!authorized) return;\r\n```";
        var workflow = new JsonObject
        {
            ["state"] = "ready",
            ["packet"] = new JsonObject
            {
                ["goal"] = "Keep this exact code:\r\n" + code, ["target"] = "repo/board-main", ["authorityRef"] = "Paul's direct request",
                ["restoration"] = "Restore the fixture", ["stopCondition"] = "Stop on refusal", ["resources"] = new JsonArray("rig-A")
            },
            ["criteria"] = new JsonArray(new JsonObject { ["id"] = "fence-A", ["description"] = "Preserve current fences", ["result"] = "not_run" }),
            ["blockers"] = new JsonArray(), ["dependsOn"] = new JsonArray()
        };
        Data(Call(RelayTools.TaskUpdate, new() { ["taskId"] = "task-A", ["requestId"] = "create", ["expectedVersion"] = 0, ["workflow"] = workflow.DeepClone(), ["patch"] = new JsonObject { ["title"] = "Exact brief" } }));
        Data(Call(RelayTools.TaskClaim, new() { ["taskId"] = "task-A", ["requestId"] = "claim", ["expectedVersion"] = 1, ["sessionId"] = "session-A", ["resourceIds"] = new JsonArray("rig-A") }));
        Data(Call(RelayTools.ActionBegin, new() { ["taskId"] = "task-A", ["requestId"] = "begin", ["expectedVersion"] = 2, ["sessionId"] = "session-A", ["actionId"] = "action-A", ["inputFingerprint"] = "exact-fingerprint", ["requiresRestoration"] = true }));
        Data(Call(RelayTools.ActionReport, new() { ["taskId"] = "task-A", ["requestId"] = "report", ["expectedVersion"] = 3, ["sessionId"] = "session-A", ["actionId"] = "action-A", ["attempt"] = 1, ["outcome"] = "failed", ["restoration"] = "blocked", ["report"] = code }));
        for (var index = 0; index < 8; index++)
            Data(Call(RelayTools.TaskUpdate, new() { ["taskId"] = "task-A", ["requestId"] = "history-" + index, ["expectedVersion"] = 4 + index, ["sessionId"] = "session-A", ["message"] = new JsonObject { ["text"] = new string('x', 600) + code } }));
        var fullReply = Call(RelayTools.TaskGet, Read("task-A"));
        var full = Data(fullReply);
        var briefReply = Call(RelayTools.TaskGet, Read("task-A", "brief"));
        var brief = Data(briefReply);
        Assert.True(brief["brief"]!.GetValue<bool>());
        Assert.True(brief["complete"]!.GetValue<bool>());
        foreach (var field in new[] { "taskId", "version", "changeRevision", "owner", "claimGeneration", "leaseUntil", "resourceIds", "workflow" })
            Assert.True(JsonNode.DeepEquals(full[field], brief[field]), field);
        Assert.True(JsonNode.DeepEquals(full["actions"]!["action-A"], Assert.Single(brief["unresolvedActions"]!.AsArray())));
        Assert.Equal(code, brief["unresolvedActions"]![0]!["report"]!.GetValue<string>());
        Assert.Empty(brief["detailFetchRequired"]!.AsArray());
        Assert.Null(brief["messages"]);
        Assert.Equal(8, brief["messagesTotal"]!.GetValue<int>());
        Assert.Equal("repo", brief["refs"]!["repositoryId"]!.GetValue<string>());
        Assert.Equal("board-main", brief["refs"]!["boardId"]!.GetValue<string>());
        Assert.Equal("task-A", brief["refs"]!["taskId"]!.GetValue<string>());
        Assert.True(Encoding.UTF8.GetByteCount(briefReply.DataJson) < Encoding.UTF8.GetByteCount(fullReply.DataJson));
        var tokens = new O200kTokenCounter();
        Assert.True(tokens.Count(briefReply.DataJson) < tokens.Count(fullReply.DataJson));
        output.WriteLine($"Task brief versus actual default Recent: {Encoding.UTF8.GetByteCount(fullReply.DataJson)} -> {Encoding.UTF8.GetByteCount(briefReply.DataJson)} bytes; {tokens.Count(fullReply.DataJson)} -> {tokens.Count(briefReply.DataJson)} estimated o200k tokens.");
        Assert.Equal("Busy", Call(RelayTools.TaskClaim, new() { ["taskId"] = "task-A", ["requestId"] = "replacement", ["expectedVersion"] = 12, ["sessionId"] = "replacement", ["resourceIds"] = new JsonArray("rig-A") }, seconds: 500).Code);
        Assert.Equal("RepositoryDenied", CoordinationStateMachine.Apply(state, AggregateKinds.Board, "repo/board-main", new RelayCaller("other", false, ["other"], true), RelayTools.TaskGet,
            new() { ["repositoryId"] = "repo", ["taskId"] = "task-A", ["section"] = "brief" }, now).Code);
        Assert.Equal("InvalidArguments", Call(RelayTools.TaskGet, new() { ["section"] = "brief" }).Code);

        var largeWorkflow = workflow.DeepClone().AsObject();
        largeWorkflow["criteria"] = new JsonArray(Enumerable.Range(0, 4).Select(index => (JsonNode)new JsonObject
        { ["id"] = "exact-" + index, ["description"] = code + new string('z', 1_400), ["result"] = "not_run" }).ToArray());
        Data(Call(RelayTools.TaskUpdate, new() { ["taskId"] = "task-large", ["requestId"] = "large", ["expectedVersion"] = 0, ["workflow"] = largeWorkflow.DeepClone() }));
        var limited = Data(Call(RelayTools.TaskGet, Read("task-large", "brief")));
        Assert.False(limited["complete"]!.GetValue<bool>());
        Assert.Null(limited["workflow"]);
        Assert.Contains(limited["detailFetchRequired"]!.AsArray(), value => value!.GetValue<string>() == "workflow");
        Assert.True(JsonNode.DeepEquals(largeWorkflow, Data(Call(RelayTools.TaskGet, Read("task-large", "workflow")))));
        Data(Call(RelayTools.TaskUpdate, new() { ["taskId"] = "task-small", ["requestId"] = "small", ["expectedVersion"] = 0 }));
        Assert.True(JsonNode.DeepEquals(Data(Call(RelayTools.TaskGet, Read("task-small"))), Data(Call(RelayTools.TaskGet, Read("task-small", "brief")))));
    }
}
