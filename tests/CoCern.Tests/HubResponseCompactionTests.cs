using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Domain.State;
using CoCern.Host;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace CoCern.Tests;

/// <summary>Read projections never retire sessions or discard actionable task detail.</summary>
public sealed class HubResponseCompactionTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-04T00:00:00Z");
    private static readonly RelayCaller Caller = new("alice", true, ["repo"], true);

    private void MeasureSavings(string kind, JsonObject compact, JsonObject full)
    {
        var counter = new O200kTokenCounter();
        var small = compact.ToJsonString();
        var large = full.ToJsonString();
        var smallBytes = Encoding.UTF8.GetByteCount(small);
        var largeBytes = Encoding.UTF8.GetByteCount(large);
        var smallTokens = counter.Count(small);
        var largeTokens = counter.Count(large);
        output.WriteLine("{0}: full={1} bytes/{2} tokens, compact={3} bytes/{4} tokens (same fixture).", kind, largeBytes, largeTokens, smallBytes, smallTokens);
        Assert.True(smallBytes < largeBytes);
        Assert.True(smallTokens < largeTokens);
    }

    private static RelayReply Call(StateEnvelope<CoordinationDocument> state, string kind, string tool, JsonObject args, DateTimeOffset? now = null)
    {
        args["repositoryId"] = "repo";
        return CoordinationStateMachine.Apply(state, kind, "key", Caller, tool, args, now ?? Now);
    }

    private static JsonObject Data(RelayReply reply)
    {
        Assert.True(reply.Ok, reply.Code + ": " + reply.DataJson);
        return JsonNode.Parse(reply.DataJson)!.AsObject();
    }

    private static (StateEnvelope<CoordinationDocument> State, string Id) Worker()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        var opened = Data(Call(state, "control", RelayTools.SessionOpen, new()
        {
            ["requestId"] = "open", ["machineId"] = "machine", ["label"] = "ordinary chat",
            ["binding"] = new JsonObject
            {
                ["workspacePath"] = @"C:\fixture\repo",
                ["roslyn"] = new JsonObject { ["solutionPath"] = @"C:\fixture\repo\Repo.slnx" },
                ["visualStudio"] = new JsonObject { ["solutionPath"] = @"C:\fixture\repo\Repo.slnx" }
            }
        }));
        state.Effects.Clear();
        return (state, opened["sessionId"]!.GetValue<string>());
    }

    private static void ChangeWorker(StateEnvelope<CoordinationDocument> state, string id, Action<JsonObject> change)
    {
        var document = JsonNode.Parse(state.Value.Json)!.AsObject();
        change(document[ControlState.SessionsKey]![id]!.AsObject());
        state.Value = new() { Json = document.ToJsonString() };
    }

    private static StateEnvelope<CoordinationDocument> Board()
    {
        JsonObject Task(string id, string? workflow, bool claim = false, bool action = false) => new()
        {
            ["taskId"] = id, ["version"] = 3, ["changeRevision"] = 1,
            ["owner"] = claim ? "owner" : "", ["claimGeneration"] = claim ? 2 : 0,
            ["leaseUntil"] = (claim ? Now.AddMinutes(5) : Now.AddMinutes(-5)).ToString("O"),
            ["workflow"] = workflow is null ? null : new JsonObject { ["state"] = workflow, ["priority"] = 0, ["blockers"] = new JsonArray() },
            ["facts"] = new JsonObject { ["receipt"] = "retain detail" },
            ["actions"] = action ? new JsonObject { ["pending"] = new JsonObject { ["actionId"] = "pending", ["status"] = "unknown", ["attempt"] = 1, ["restoration"] = "pending" } } : new JsonObject()
        };
        return new()
        {
            Value = new() { Json = new JsonObject
            {
                ["repositoryId"] = "repo", ["taskRevision"] = 1, [ReadModels.RequestsKey] = new JsonObject(),
                ["tasks"] = new JsonObject
                {
                    ["a-finished"] = Task("a-finished", "completed"),
                    ["b-claimed"] = Task("b-claimed", "completed", claim: true),
                    ["c-unresolved"] = Task("c-unresolved", "completed", action: true),
                    ["d-open"] = Task("d-open", "ready"),
                    ["e-legacy"] = Task("e-legacy", null)
                }
            }.ToJsonString() }
        };
    }

    private static JsonObject HostData(JsonElement reply)
    {
        Assert.True(reply.GetProperty("ok").GetBoolean(), reply.GetRawText());
        return JsonNode.Parse(reply.GetProperty("dataJson").GetString()!)!.AsObject();
    }

    [Theory]
    [InlineData(true, "principal")]
    [InlineData(true, "administrator")]
    [InlineData(true, "repository")]
    [InlineData(false, "principal")]
    [InlineData(false, "administrator")]
    public void Compact_continuations_do_not_accept_a_tag_from_another_caller_or_repository(bool workers, string change)
    {
        var state = workers ? Worker().State : Board();
        var baseline = new RelayCaller("alice", true, ["repo", "other"], true);
        var changed = change switch
        {
            "principal" => new RelayCaller("bob", true, ["repo", "other"], true),
            "administrator" => new RelayCaller("alice", false, ["repo", "other"], true),
            _ => baseline
        };
        RelayReply Read(RelayCaller caller, string repository, int cursor, string? tag = null)
        {
            var arguments = new JsonObject { ["repositoryId"] = repository, ["limit"] = 1, ["cursor"] = cursor };
            if (!workers) arguments["openOnly"] = true;
            if (tag is not null) arguments["snapshotTag"] = tag;
            return CoordinationStateMachine.Apply(state, workers ? "control" : "board", "key", caller,
                workers ? RelayTools.WorkerList : RelayTools.TaskGet, arguments, Now);
        }
        var first = Data(Read(baseline, "repo", 0));
        var tag = first["snapshotTag"]!.GetValue<string>();
        var repository = change == "repository" ? "other" : "repo";
        var reused = Read(changed, repository, 1, tag);
        Assert.False(reused.Ok);
        Assert.Equal("ListRestartRequired", reused.Code);
        var fresh = Data(Read(changed, repository, 0));
        Assert.NotEqual(tag, fresh["snapshotTag"]!.GetValue<string>());
        if (change == "repository") Assert.Empty(fresh["items"]!.AsArray());
        Assert.Empty(state.Effects);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Compact_list_metadata_and_oversized_pieces_fit_and_reassemble_without_skipping_the_next_item(bool workers)
    {
        StateEnvelope<CoordinationDocument> state;
        string oversizedId;
        const int resources = 400;
        var largeLabel = new string('\\', RelayLimits.AggregateReplyBytes) + "tail";
        if (workers)
        {
            var worker = Worker(); state = worker.State;
            var second = Data(Call(state, "control", RelayTools.SessionOpen, new() { ["requestId"] = "piece-next", ["label"] = "next chat" }));
            oversizedId = new[] { worker.Id, second["sessionId"]!.GetValue<string>() }.Order(StringComparer.Ordinal).First();
            ChangeWorker(state, oversizedId, row => row["label"] = largeLabel);
        }
        else
        {
            state = Board(); oversizedId = "b-claimed";
            var document = JsonNode.Parse(state.Value.Json)!.AsObject();
            document["tasks"]![oversizedId]!["resourceIds"] = new JsonArray(Enumerable.Range(0, resources)
                .Select(index => (JsonNode)JsonValue.Create($"resource-{index:D4}-" + new string('x', 96))!).ToArray());
            state.Value = new() { Json = document.ToJsonString() };
        }
        state.Effects.Clear();
        var before = state.Value.Json;
        var assembled = new StringBuilder();
        long? offset = 0;
        string? tag = null;
        var pieces = 0;
        while (offset is { } at)
        {
            var arguments = new JsonObject { ["cursor"] = 0, ["limit"] = 1 };
            if (!workers) arguments["openOnly"] = true;
            if (tag is not null) arguments["snapshotTag"] = tag;
            if (at > 0) arguments["partOffset"] = at;
            var reply = Call(state, workers ? "control" : "board", workers ? RelayTools.WorkerList : RelayTools.TaskGet, arguments);
            Assert.True(reply.Ok, reply.Code);
            Assert.True(Replies.FitsWire(reply, RelayLimits.AggregateReplyBytes));
            var page = Data(reply);
            var returnedTag = page["snapshotTag"]!.GetValue<string>();
            if (tag is not null) Assert.Equal(tag, returnedTag);
            tag = returnedTag;
            var piece = Assert.Single(page["items"]!.AsArray())!.AsObject();
            Assert.Equal(oversizedId, piece[workers ? "sessionId" : "taskId"]!.GetValue<string>());
            Assert.Equal(at, piece["partOffset"]!.GetValue<long>());
            Assert.Equal(1, page["nextCursor"]!.GetValue<int>());
            var part = piece["part"]!.GetValue<string>();
            Assert.NotEmpty(part);
            assembled.Append(part);
            offset = piece["nextPartOffset"]?.GetValue<long>();
            Assert.True(++pieces < 100, "Oversized list item did not make bounded forward progress.");
        }
        Assert.True(pieces > 1);
        var restored = JsonNode.Parse(assembled.ToString())!.AsObject();
        if (workers) { Assert.Equal(largeLabel, restored["label"]!.GetValue<string>()); Assert.Null(restored["binding"]); }
        else Assert.Equal(resources, restored["resourceIds"]!.AsArray().Count);
        var next = new JsonObject { ["cursor"] = 1, ["snapshotTag"] = tag };
        if (!workers) next["openOnly"] = true;
        var remainder = Data(Call(state, workers ? "control" : "board", workers ? RelayTools.WorkerList : RelayTools.TaskGet, next));
        Assert.NotEmpty(remainder["items"]!.AsArray());
        Assert.DoesNotContain(remainder["items"]!.AsArray(), row => row![workers ? "sessionId" : "taskId"]!.GetValue<string>() == oversizedId);
        Assert.Equal(before, state.Value.Json);
        Assert.Empty(state.Effects);
    }

    [Fact]
    public async Task Semantic_status_does_not_reuse_another_sessions_or_selections_tag()
    {
        await using var host = new RelayHostFixture(settings: new Dictionary<string, string?> { ["Relay:Clients:0:IsAdministrator"] = "true" });
        using var client = host.AuthorizedClient();
        var first = HostData(await host.Call(client, RelayTools.SessionOpen, new { repositoryId = "repo", requestId = "first-status-open" }));
        var second = HostData(await host.Call(client, RelayTools.SessionOpen, new { repositoryId = "repo", requestId = "second-status-open" }));
        async Task<JsonObject> Status(JsonObject session, string tag, int hold) => HostData(await host.Call(client, RelayTools.GetStatus, new
        {
            repositoryId = "repo", sessionId = session["sessionId"]!.GetValue<string>(), sessionToken = session["sessionToken"]!.GetValue<string>(),
            attentionDetail = true, maxHoldMinutes = hold, statusTag = tag
        }));
        var full = await Status(first, "", 5);
        Assert.NotNull(full["statusTag"]);
        var tag = full["statusTag"]!.GetValue<string>();
        Assert.Null((await Status(second, tag, 5))["unchanged"]);
        Assert.Null((await Status(first, tag, 6))["unchanged"]);

        // The live hub's administrator watchers repeatedly returned full replies once a manager had been appointed.
        HostData(await host.Call(client, RelayTools.ControlRequest, new
        {
            repositoryId = "repo", sessionId = first["sessionId"]!.GetValue<string>(), sessionToken = first["sessionToken"]!.GetValue<string>(),
            requestId = "status-manager", command = "appoint_manager", targetSessionId = first["sessionId"]!.GetValue<string>(),
            expectedManagerGeneration = 0, humanWords = "Appoint this fixture session."
        }));
        var summary = await Status(first, "", 5);
        Assert.Equal(first["sessionId"]!.GetValue<string>(), summary["control"]!["manager"]!["current"]!["sessionId"]!.GetValue<string>());
        Assert.Empty(summary["control"]!["manager"]!["history"]!.AsArray());
        var managerTag = summary["statusTag"]!.GetValue<string>();
        var compact = await Status(first, managerTag, 5);
        Assert.True(compact["unchanged"]!.GetValue<bool>());
        MeasureSavings("administrator background status", compact, summary);
        var history = HostData(await host.Call(client, RelayTools.GetStatus, new
        {
            repositoryId = "repo", sessionId = first["sessionId"]!.GetValue<string>(), sessionToken = first["sessionToken"]!.GetValue<string>(),
            attentionDetail = true, statusTag = managerTag, managerHistoryOnly = true
        }));
        Assert.Null(history["unchanged"]);
        Assert.NotEmpty(history["control"]!["manager"]!["history"]!.AsArray());
    }

    [Theory]
    [InlineData("principal")]
    [InlineData("administrator")]
    [InlineData("repositories")]
    [InlineData("repository")]
    public async Task Semantic_status_reauthorizes_a_reused_tag_and_scopes_it_to_caller_privileges(string change)
    {
        var alternateToken = Guid.NewGuid().ToString("N");
        var settings = new Dictionary<string, string?>
        {
            ["Relay:Clients:1:Token"] = alternateToken,
            ["Relay:Clients:1:PrincipalId"] = change == "principal" ? "other-user" : "test-user",
            ["Relay:Clients:1:IsAdministrator"] = (change == "administrator").ToString(),
            ["Relay:Clients:1:Repositories:0"] = "repo"
        };
        if (change is "repository" or "repositories") settings["Relay:Clients:1:Repositories:1"] = "other";
        await using var host = new RelayHostFixture(settings: settings);
        using var original = host.AuthorizedClient();
        using var alternate = host.AuthorizedClient();
        alternate.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", alternateToken);
        var session = HostData(await host.Call(original, RelayTools.SessionOpen, new { repositoryId = "repo", requestId = "scope-open" }));
        var sessionId = session["sessionId"]!.GetValue<string>();
        var sessionToken = session["sessionToken"]!.GetValue<string>();
        var full = HostData(await host.Call(original, RelayTools.GetStatus, new { repositoryId = "repo", sessionId, sessionToken, attentionDetail = true, statusTag = "" }));
        var tag = full["statusTag"]!.GetValue<string>();
        using var response = await alternate.PostAsJsonAsync("/api/tools/" + RelayTools.GetStatus, new
        {
            repositoryId = change == "repository" ? "other" : "repo", sessionId, sessionToken, attentionDetail = true, statusTag = tag
        });
        if (change is "principal" or "repository")
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var denied = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(denied.GetProperty("ok").GetBoolean());
        }
        else
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var changed = HostData(await response.Content.ReadFromJsonAsync<JsonElement>());
            Assert.Null(changed["unchanged"]);
            Assert.NotNull(changed["statusTag"]);
            Assert.NotEqual(tag, changed["statusTag"]!.GetValue<string>());
            Assert.Equal(sessionId, changed["sessionId"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Semantic_status_recomputes_inbox_pause_and_cancellation_before_matching()
    {
        var administratorToken = Guid.NewGuid().ToString("N");
        await using var host = new RelayHostFixture(disableDispatcher: true, settings: new Dictionary<string, string?>
        {
            ["Relay:Clients:1:Token"] = administratorToken,
            ["Relay:Clients:1:PrincipalId"] = "status-admin",
            ["Relay:Clients:1:IsAdministrator"] = "true",
            ["Relay:Clients:1:Repositories:0"] = "repo"
        });
        using var client = host.AuthorizedClient();
        using var administrator = host.AuthorizedClient();
        administrator.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", administratorToken);
        var session = HostData(await host.Call(client, RelayTools.SessionOpen, new { repositoryId = "repo", requestId = "changed-status-open" }));
        var other = HostData(await host.Call(client, RelayTools.SessionOpen, new { repositoryId = "repo", requestId = "changed-status-sender" }));
        var sessionId = session["sessionId"]!.GetValue<string>();
        var sessionToken = session["sessionToken"]!.GetValue<string>();
        async Task<JsonObject> Status(string tag) => HostData(await host.Call(client, RelayTools.GetStatus, new { repositoryId = "repo", sessionId, sessionToken, attentionDetail = true, statusTag = tag }));
        var first = await Status("");
        HostData(await host.Call(client, RelayTools.SendMessage, new
        {
            repositoryId = "repo", sessionId = other["sessionId"]!.GetValue<string>(), sessionToken = other["sessionToken"]!.GetValue<string>(),
            recipientSessionId = sessionId, requestId = "changed-status-mail", text = "fixture update", wake = false
        }));
        // A send queues delivery; complete that real outbox transition before checking the fresh status.
        var dispatch = await host.Services.GetRequiredService<Orleans.IGrainFactory>()
            .GetGrain<ISessionGrain>(other["sessionId"]!.GetValue<string>())
            .ExecuteAsync(new RelayCaller("test-user", false, ["repo"], true), "__dispatch", "{}");
        Assert.True(dispatch.Ok, dispatch.Code);
        var mail = await Status(first["statusTag"]!.GetValue<string>());
        Assert.Null(mail["unchanged"]);
        Assert.Equal(1, mail["attention"]!["inbox"]!.GetValue<int>());
        HostData(await host.Call(administrator, RelayTools.ControlRequest, new { repositoryId = "repo", requestId = "changed-status-pause", command = "pause" }));
        var paused = await Status(mail["statusTag"]!.GetValue<string>());
        Assert.Null(paused["unchanged"]);
        Assert.True(paused["control"]!["paused"]!.GetValue<bool>());
        HostData(await host.Call(administrator, RelayTools.ControlRequest, new { repositoryId = "repo", sessionId, requestId = "changed-status-cancel", command = "cancel", actionId = "fixture-action" }));
        var canceled = await Status(paused["statusTag"]!.GetValue<string>());
        Assert.Null(canceled["unchanged"]);
        Assert.Equal(sessionId + ":fixture-action", Assert.Single(canceled["control"]!["requests"]!.AsArray())!["target"]!.GetValue<string>());
    }

}
