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

    [Fact]
    public void Compact_workers_keep_identity_and_presence_without_repeating_binding_and_full_view_recovers_it()
    {
        var (state, id) = Worker();
        var compact = Data(Call(state, "control", RelayTools.WorkerList, new()));
        var row = Assert.Single(compact["items"]!.AsArray())!.AsObject();
        Assert.Equal(id, row["sessionId"]!.GetValue<string>());
        Assert.Equal("repo", row["repositoryId"]!.GetValue<string>());
        Assert.Equal("machine", row["machineId"]!.GetValue<string>());
        Assert.NotNull(row["bindingVersion"]);
        Assert.NotNull(row["stale"]);
        Assert.Null(row["binding"]);
        var full = Data(Call(state, "control", RelayTools.WorkerList, new() { ["includeStale"] = true }));
        Assert.IsType<JsonObject>(Assert.Single(full["items"]!.AsArray())!["binding"]);
        Assert.DoesNotContain("sessionToken", full.ToJsonString());
        Assert.Empty(state.Effects);
    }

    [Theory]
    [InlineData("none", false)]
    [InlineData("recent", true)]
    [InlineData("work", true)]
    [InlineData("oldWork", true)]
    [InlineData("mail", true)]
    [InlineData("invalidTime", true)]
    [InlineData("invalidWork", true)]
    public void Compact_workers_omit_only_definitely_idle_rows_and_preserve_uncertain_or_marked_rows(string marker, bool kept)
    {
        var (state, id) = Worker();
        ChangeWorker(state, id, row =>
        {
            var old = Now.AddDays(-2).ToString("O");
            row[DirectoryEntry.ObservedAtKey] = old;
            row[DirectoryEntry.FreshUntilKey] = old;
            row[DirectoryEntry.ActiveAtKey] = old;
            if (marker == "recent") row[DirectoryEntry.ActiveAtKey] = Now.AddHours(-23).ToString("O");
            if (marker == "work") row[DirectoryEntry.OwnsWorkKey] = Now.ToUnixTimeSeconds();
            if (marker == "oldWork") row[DirectoryEntry.OwnsWorkKey] = Now.AddDays(-60).ToUnixTimeSeconds();
            if (marker == "mail") row[DirectoryEntry.MailHeldAtKey] = Now.AddDays(-2).ToString("O");
            if (marker == "invalidTime") row[DirectoryEntry.ActiveAtKey] = "unreadable";
            if (marker == "invalidWork") row[DirectoryEntry.OwnsWorkKey] = "unknown";
            // Display labels are never evidence that this session is disposable.
            row[DirectoryEntry.LabelKey] = "throwaway fixture";
        });
        var before = state.Value.Json;
        var compact = Data(Call(state, "control", RelayTools.WorkerList, new()));
        Assert.Equal(kept ? 1 : 0, compact["items"]!.AsArray().Count);
        Assert.NotNull(compact["omittedCount"]);
        Assert.Equal(kept ? 0 : 1, compact["omittedCount"]!.GetValue<int>());
        var full = Data(Call(state, "control", RelayTools.WorkerList, new() { ["includeStale"] = true }));
        Assert.Equal(id, Assert.Single(full["items"]!.AsArray())!["sessionId"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(before), JsonNode.Parse(state.Value.Json)));
        Assert.Empty(state.Effects);
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
                ["repositoryId"] = "repo", ["taskRevision"] = 1,
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

    [Fact]
    public void Compact_board_list_collapses_only_safe_completed_tasks_and_preserves_detail()
    {
        var state = Board();
        var list = Data(Call(state, "board", RelayTools.TaskGet, new()));
        var rows = list["items"]!.AsArray().Select(x => x!.AsObject()).ToDictionary(x => x["taskId"]!.GetValue<string>());
        Assert.Equal(2, rows["a-finished"].Count);
        Assert.Equal("completed", rows["a-finished"]["state"]!.GetValue<string>());
        Assert.All(rows.Values, row => Assert.Null(row["sections"]));
        Assert.Equal("owner", rows["b-claimed"]["owner"]!.GetValue<string>());
        Assert.Single(rows["c-unresolved"]["unresolvedActions"]!.AsArray());
        var detail = Data(Call(state, "board", RelayTools.TaskGet, new() { ["taskId"] = "a-finished" }));
        Assert.Equal("retain detail", detail["facts"]!["receipt"]!.GetValue<string>());
        Assert.NotNull(detail["workflow"]);
        var full = Data(Call(state, "board", RelayTools.TaskGet, new() { ["includeDetails"] = true }));
        Assert.All(full["items"]!.AsArray(), row => Assert.NotNull(row!["sections"]));
        MeasureSavings("board", list, full);
    }

    [Fact]
    public void Open_only_board_list_keeps_claims_unresolved_actions_and_workflowless_tasks()
    {
        var state = Board();
        var list = Data(Call(state, "board", RelayTools.TaskGet, new() { ["openOnly"] = true }));
        Assert.Equal(new[] { "b-claimed", "c-unresolved", "d-open", "e-legacy" }, list["items"]!.AsArray().Select(x => x!["taskId"]!.GetValue<string>()));
    }

    [Fact]
    public void Open_only_continuation_restarts_when_a_lease_expires_without_a_board_write()
    {
        var state = Board();
        var first = Data(Call(state, "board", RelayTools.TaskGet, new() { ["openOnly"] = true, ["limit"] = 1 }));
        Assert.NotNull(first["snapshotTag"]);
        var snapshot = first["snapshotTag"]!.GetValue<string>();
        var cursor = first["nextCursor"]!.GetValue<int>();
        var next = Call(state, "board", RelayTools.TaskGet, new()
        {
            ["openOnly"] = true, ["limit"] = 1, ["cursor"] = cursor, ["snapshotTag"] = snapshot
        }, Now.AddMinutes(6));
        Assert.False(next.Ok);
        Assert.Equal("ListRestartRequired", next.Code);
    }

    [Fact]
    public void Compact_worker_continuation_restarts_when_time_alone_changes_membership()
    {
        var (state, id) = Worker();
        Data(Call(state, "control", RelayTools.SessionOpen, new() { ["requestId"] = "open-other", ["label"] = "second chat" }));
        ChangeWorker(state, id, row =>
        {
            var old = Now.AddHours(-24).AddMinutes(1).ToString("O");
            row[DirectoryEntry.ObservedAtKey] = old;
            row[DirectoryEntry.FreshUntilKey] = old;
            row[DirectoryEntry.ActiveAtKey] = old;
        });
        state.Effects.Clear();
        var first = Data(Call(state, "control", RelayTools.WorkerList, new() { ["limit"] = 1 }));
        Assert.NotNull(first["snapshotTag"]);
        var next = Call(state, "control", RelayTools.WorkerList, new()
        {
            ["limit"] = 1, ["cursor"] = first["nextCursor"]!.GetValue<int>(), ["snapshotTag"] = first["snapshotTag"]!.GetValue<string>()
        }, Now.AddMinutes(2));
        Assert.False(next.Ok);
        Assert.Equal("ListRestartRequired", next.Code);
        Assert.Empty(state.Effects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Compact_list_continuation_without_a_snapshot_tag_requires_restart(bool workers)
    {
        var state = workers ? Worker().State : Board();
        var args = new JsonObject { ["cursor"] = 1, ["limit"] = 1 };
        if (!workers) args["openOnly"] = true;

        var reply = Call(state, workers ? "control" : "board", workers ? RelayTools.WorkerList : RelayTools.TaskGet, args);

        Assert.False(reply.Ok);
        Assert.Equal("ListRestartRequired", reply.Code);
        Assert.Empty(state.Effects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Full_list_continuation_keeps_its_existing_numeric_cursor_contract(bool workers)
    {
        var state = workers ? Worker().State : Board();
        var args = new JsonObject { ["cursor"] = 1, ["limit"] = 1 };
        if (workers) args["includeStale"] = true;
        else args["includeDetails"] = true;

        var reply = Call(state, workers ? "control" : "board", workers ? RelayTools.WorkerList : RelayTools.TaskGet, args);

        Assert.True(reply.Ok, reply.Code);
        Assert.Empty(state.Effects);
    }

    [Theory]
    [InlineData("taskId", "d-open")]
    [InlineData("section", "standing")]
    [InlineData("sinceRevision", 0)]
    public void Open_only_is_rejected_for_a_detail_section_or_delta(string field, object value)
    {
        var args = new JsonObject { ["repositoryId"] = "repo", ["openOnly"] = true, [field] = JsonSerializer.SerializeToNode(value) };
        using var raw = JsonDocument.Parse(args.ToJsonString());
        Assert.True(RelayToolCatalog.TryGet(RelayTools.TaskGet, out var definition));
        var rejected = RelayRequestGuard.Validate(Caller, definition, raw.RootElement, out _);
        Assert.NotNull(rejected);
        Assert.Equal("InvalidArguments", rejected.Code);
    }

    private static JsonObject HostData(JsonElement reply)
    {
        Assert.True(reply.GetProperty("ok").GetBoolean(), reply.GetRawText());
        return JsonNode.Parse(reply.GetProperty("dataJson").GetString()!)!.AsObject();
    }

    [Fact]
    public async Task Semantic_status_is_opt_in_and_identical_poll_returns_only_the_scoped_tag()
    {
        await using var host = new RelayHostFixture();
        using var client = host.AuthorizedClient();
        var session = HostData(await host.Call(client, RelayTools.SessionOpen, new { repositoryId = "repo", requestId = "status-open" }));
        var sessionId = session["sessionId"]!.GetValue<string>();
        var sessionToken = session["sessionToken"]!.GetValue<string>();
        var full = HostData(await host.Call(client, RelayTools.GetStatus, new
        {
            repositoryId = "repo", sessionId, sessionToken, attentionDetail = true, statusTag = ""
        }));
        Assert.NotNull(full["statusTag"]);
        var tag = full["statusTag"]!.GetValue<string>();
        var same = HostData(await host.Call(client, RelayTools.GetStatus, new
        {
            repositoryId = "repo", sessionId, sessionToken, attentionDetail = true, statusTag = tag
        }));
        Assert.Equal(2, same.Count);
        Assert.True(same["unchanged"]!.GetValue<bool>());
        Assert.Equal(tag, same["statusTag"]!.GetValue<string>());
        MeasureSavings("status", same, full);
        var ordinary = HostData(await host.Call(client, RelayTools.GetStatus, new { repositoryId = "repo", sessionId, sessionToken, attentionDetail = true }));
        Assert.Null(ordinary["statusTag"]);
        Assert.NotNull(ordinary["sessionId"]);
        using var invalid = await client.PostAsJsonAsync("/api/tools/" + RelayTools.GetStatus, new { repositoryId = "repo", sessionId, sessionToken = "wrong", statusTag = tag });
        Assert.Equal(HttpStatusCode.Forbidden, invalid.StatusCode);
        var denied = await invalid.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(denied.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Semantic_status_does_not_reuse_another_sessions_or_selections_tag()
    {
        await using var host = new RelayHostFixture();
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
    }

    [Fact]
    public void Compact_worker_wire_savings_preserve_the_same_authorized_population()
    {
        var (state, _) = Worker();
        for (var index = 1; index < 10; index++)
            Data(Call(state, "control", RelayTools.SessionOpen, new()
            {
                ["requestId"] = "worker-wire-" + index,
                ["label"] = "ordinary chat " + index,
                ["binding"] = new JsonObject
                {
                    ["workspacePath"] = @"C:\fixture\repo",
                    ["roslyn"] = new JsonObject { ["solutionPath"] = @"C:\fixture\repo\Repo.slnx" },
                    ["visualStudio"] = new JsonObject { ["solutionPath"] = @"C:\fixture\repo\Repo.slnx" }
                }
            }));
        state.Effects.Clear();
        var compact = Data(Call(state, "control", RelayTools.WorkerList, new() { ["limit"] = 20 }));
        var full = Data(Call(state, "control", RelayTools.WorkerList, new() { ["limit"] = 20, ["includeStale"] = true }));
        Assert.Equal(10, compact["items"]!.AsArray().Count);
        Assert.Equal(full["items"]!.AsArray().Select(row => row!["sessionId"]!.GetValue<string>()).Order(StringComparer.Ordinal),
            compact["items"]!.AsArray().Select(row => row!["sessionId"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        Assert.Empty(state.Effects);
        MeasureSavings("workers", compact, full);
    }

    [Fact]
    public async Task Semantic_status_recomputes_inbox_pause_and_cancellation_before_matching()
    {
        await using var host = new RelayHostFixture(disableDispatcher: true);
        using var client = host.AuthorizedClient();
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
        HostData(await host.Call(client, RelayTools.ControlRequest, new { repositoryId = "repo", requestId = "changed-status-pause", command = "pause" }));
        var paused = await Status(mail["statusTag"]!.GetValue<string>());
        Assert.Null(paused["unchanged"]);
        Assert.True(paused["control"]!["paused"]!.GetValue<bool>());
        HostData(await host.Call(client, RelayTools.ControlRequest, new { repositoryId = "repo", sessionId, requestId = "changed-status-cancel", command = "cancel", actionId = "fixture-action" }));
        var canceled = await Status(paused["statusTag"]!.GetValue<string>());
        Assert.Null(canceled["unchanged"]);
        Assert.Equal(sessionId + ":fixture-action", Assert.Single(canceled["control"]!["requests"]!.AsArray())!["target"]!.GetValue<string>());
    }

    [Fact]
    public async Task Semantic_status_history_and_piggyback_reads_keep_the_full_reply()
    {
        await using var host = new RelayHostFixture();
        using var client = host.AuthorizedClient();
        var session = HostData(await host.Call(client, RelayTools.SessionOpen, new { repositoryId = "repo", requestId = "fallback-status-open" }));
        var sessionId = session["sessionId"]!.GetValue<string>();
        var sessionToken = session["sessionToken"]!.GetValue<string>();
        var full = HostData(await host.Call(client, RelayTools.GetStatus, new { repositoryId = "repo", sessionId, sessionToken, attentionDetail = true, statusTag = "" }));
        var tag = full["statusTag"]!.GetValue<string>();
        var history = HostData(await host.Call(client, RelayTools.GetStatus, new { repositoryId = "repo", sessionId, sessionToken, attentionDetail = true, statusTag = tag, managerHistoryOnly = true }));
        Assert.Null(history["unchanged"]);
        Assert.Null(history["statusTag"]);
        Assert.NotNull(history["control"]);
        var piggyback = HostData(await host.Call(client, RelayTools.GetStatus, new { repositoryId = "repo", sessionId, sessionToken, attentionDetail = true, statusTag = tag, renewClaims = Array.Empty<object>() }));
        Assert.Null(piggyback["unchanged"]);
        Assert.Null(piggyback["statusTag"]);
        Assert.NotNull(piggyback["conveniences"]);
    }

    [Theory]
    [InlineData("includeStale", "worker_list")]
    [InlineData("openOnly", "task_get")]
    [InlineData("includeDetails", "task_get")]
    public void Compaction_flags_reject_non_boolean_values(string flag, string tool)
    {
        using var raw = JsonDocument.Parse(new JsonObject { ["repositoryId"] = "repo", [flag] = "true" }.ToJsonString());
        Assert.True(RelayToolCatalog.TryGet(tool, out var definition));
        var rejected = RelayRequestGuard.Validate(Caller, definition, raw.RootElement, out _);
        Assert.NotNull(rejected);
        Assert.Equal("InvalidArguments", rejected.Code);
    }
}
