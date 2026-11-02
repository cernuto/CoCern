using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Adapter;
using CoCern.Adapter.Node;
using CoCern.Adapter.Setup;
using CoCern.Contracts;
using CoCern.Domain;
using Xunit;

namespace CoCern.Tests;

/// <summary>Regression tests for the principal-engineer review: leases, report caps, replay scope, action blocks and the adapter's local state.</summary>
public sealed class PrincipalReviewTests : IDisposable
{
    private static readonly RelayCaller Machine = new("machine:pc-1", false, ["repo"], true);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-02T00:00:00Z");
    private readonly string root = Path.Combine(Path.GetTempPath(), "cocern-review-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    private static RelayReply Apply(StateEnvelope<CoordinationDocument> state, string kind, string operation, JsonObject args, int seconds = 0, string key = "key")
    {
        args["repositoryId"] = "repo";
        return CoordinationStateMachine.Apply(state, kind, key, Machine, operation, args, Now.AddSeconds(seconds));
    }

    private static JsonObject State(StateEnvelope<CoordinationDocument> state) => JsonNode.Parse(state.Value.Json)!.AsObject();

    private static JsonObject Workflow() => new()
    {
        ["state"] = "ready", ["packet"] = new JsonObject { ["goal"] = "g", ["target"] = "t", ["authorityRef"] = "x:y", ["restoration"] = "r", ["stopCondition"] = "s" },
        ["criteria"] = new JsonArray(new JsonObject { ["id"] = "c", ["description"] = "d", ["result"] = "not_run" }), ["blockers"] = new JsonArray()
    };

    [Fact]
    public void Only_running_leases_get_a_timer_so_ended_claims_cost_nothing_on_later_writes()
    {
        var board = new StateEnvelope<CoordinationDocument>();
        Assert.True(Apply(board, "board", RelayTools.TaskUpdate, new() { ["taskId"] = "t", ["requestId"] = "c", ["expectedVersion"] = 0, ["workflow"] = Workflow() }).Ok);
        Assert.True(Apply(board, "board", RelayTools.TaskClaim, new() { ["taskId"] = "t", ["sessionId"] = "s", ["requestId"] = "k", ["expectedVersion"] = 1, ["leaseSeconds"] = 60 }).Ok);
        Assert.Contains(board.DueWork, work => work.Id == "lease:t");
        // An hour later the lease has ended; another write must not schedule it again.
        Assert.True(Apply(board, "board", RelayTools.TaskUpdate, new() { ["taskId"] = "other", ["requestId"] = "o", ["expectedVersion"] = 0, ["patch"] = new JsonObject { ["n"] = 1 } }, seconds: 3600).Ok);
        Assert.DoesNotContain(board.DueWork, work => work.Kind == DueWorkKinds.Lease);
    }

    [Fact]
    public void A_full_report_history_never_stops_an_action_from_settling()
    {
        var board = new StateEnvelope<CoordinationDocument>();
        Assert.True(Apply(board, "board", RelayTools.TaskUpdate, new() { ["taskId"] = "t", ["requestId"] = "c", ["expectedVersion"] = 0, ["workflow"] = Workflow() }).Ok);
        Assert.True(Apply(board, "board", RelayTools.TaskClaim, new() { ["taskId"] = "t", ["sessionId"] = "s", ["requestId"] = "k", ["expectedVersion"] = 1 }).Ok);
        Assert.True(Apply(board, "board", RelayTools.ActionBegin, new() { ["taskId"] = "t", ["sessionId"] = "s", ["requestId"] = "b", ["expectedVersion"] = 2, ["actionId"] = "deploy", ["inputFingerprint"] = "f" }).Ok);
        // 256 old reports, each with a large body, fill both the history and most of the task's size budget.
        var state = State(board);
        var task = state["tasks"]!["t"]!.AsObject();
        task["reports"] = new JsonArray(Enumerable.Range(0, 256).Select(i => (JsonNode)new JsonObject { ["actionId"] = "old", ["attempt"] = 1, ["outcome"] = "completed", ["report"] = new string('x', 960) }).ToArray());
        board.Value = new() { Json = state.ToJsonString() };
        var version = task["version"]!.GetValue<long>();

        var settled = Apply(board, "board", RelayTools.ActionReport, new() { ["taskId"] = "t", ["sessionId"] = "s", ["requestId"] = "r", ["expectedVersion"] = version, ["actionId"] = "deploy", ["attempt"] = 1, ["outcome"] = "completed", ["report"] = new string('y', 8000) });
        Assert.True(settled.Ok, settled.Code + " " + settled.DataJson);
        var after = State(board)["tasks"]!["t"]!.AsObject();
        Assert.Equal("completed", after["actions"]!["deploy"]!["status"]!.GetValue<string>());
        Assert.True(JsonFields.ByteSize(after) <= RelayLimits.MaxTaskBytes);
        Assert.Equal("deploy", after["reports"]!.AsArray()[^1]!["actionId"]!.GetValue<string>());
        // A report that settles nothing is still capped.
        var extra = Apply(board, "board", RelayTools.ActionReport, new() { ["taskId"] = "t", ["sessionId"] = "s", ["requestId"] = "r2", ["expectedVersion"] = after["version"]!.GetValue<long>(), ["actionId"] = "deploy", ["attempt"] = 1, ["outcome"] = "completed" });
        Assert.Contains(extra.Code, new[] { RelayErrorCodes.CapacityExceeded, "Ok" });
    }

    [Fact]
    public void Two_chats_of_one_principal_with_the_same_request_id_each_get_their_own_write()
    {
        var board = new StateEnvelope<CoordinationDocument>();
        Assert.True(Apply(board, "board", RelayTools.TaskUpdate, new() { ["taskId"] = "a", ["sessionId"] = "chat-1", ["requestId"] = "r-1", ["expectedVersion"] = 0, ["patch"] = new JsonObject { ["n"] = 1 } }).Ok);
        Assert.True(Apply(board, "board", RelayTools.TaskUpdate, new() { ["taskId"] = "b", ["sessionId"] = "chat-2", ["requestId"] = "r-1", ["expectedVersion"] = 0, ["patch"] = new JsonObject { ["n"] = 2 } }).Ok);
        Assert.Equal(2, State(board)["tasks"]!.AsObject().Count);
        // The same chat's retry still replays.
        var retry = Apply(board, "board", RelayTools.TaskUpdate, new() { ["taskId"] = "a", ["sessionId"] = "chat-1", ["requestId"] = "r-1", ["expectedVersion"] = 0, ["patch"] = new JsonObject { ["n"] = 1 } });
        Assert.Equal(1, retry.Version);
    }

    [Fact]
    public void A_lock_with_an_action_in_flight_is_busy_for_a_new_action_even_from_its_holder()
    {
        var lockState = new StateEnvelope<CoordinationDocument>();
        Assert.True(Apply(lockState, "lock", RelayTools.ClaimAcquire, new() { ["sessionId"] = "s", ["requestId"] = "a" }).Ok);
        RelayReply ClaimState() => Apply(lockState, "lock", Operations.ClaimState, new() { ["sessionId"] = "s" });
        Assert.Equal((false, true), Flags(ClaimState()));
        Assert.True(Apply(lockState, "lock", Operations.Block, new() { ["sessionId"] = "s", ["actionId"] = "a1", ["actionKey"] = "a1:1" }).Ok);
        Assert.Equal((true, false), Flags(ClaimState()));
        static (bool Busy, bool Held) Flags(RelayReply reply) { var data = JsonNode.Parse(reply.DataJson)!; return (data["busy"]!.GetValue<bool>(), data["held"]!.GetValue<bool>()); }
    }

    [Fact]
    public void Each_adapter_makes_its_session_open_request_id_its_own()
    {
        var arguments = JsonSerializer.SerializeToElement(new { repositoryId = "repo", requestId = "open-1" });
        var once = CoordinationTools.OwnRequestId(arguments).GetProperty("requestId").GetString()!;
        Assert.EndsWith(":open-1", once);
        Assert.NotEqual("open-1", once);
        Assert.Equal(once, CoordinationTools.OwnRequestId(arguments).GetProperty("requestId").GetString());
    }

    [Fact]
    public void An_uncertain_wake_holds_its_route_for_a_while_not_for_good()
    {
        Directory.CreateDirectory(root);
        using var state = new WakeState(Path.Combine(root, "wake.db"));
        var start = DateTimeOffset.Parse("2026-11-02T00:00:00Z");
        var (status, batch) = state.Reserve("route", ["m1"], start);
        Assert.Equal("reserved", status);
        state.Complete(batch!, queued: false);
        Assert.True(state.IsUncertain("route", start.AddMinutes(1)));
        Assert.Equal("uncertain", state.Reserve("route", ["m2"], start.AddMinutes(1)).Status);
        Assert.False(state.IsUncertain("route", start.AddMinutes(11)));
        // The uncertain batch's message is never sent again; new mail wakes the chat.
        Assert.Equal("empty", state.Reserve("route", ["m1"], start.AddMinutes(11)).Status);
        Assert.Equal("reserved", state.Reserve("route", ["m2"], start.AddMinutes(11)).Status);
    }

    [Fact]
    public void Rewriting_an_app_settings_file_keeps_it_owner_only()
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, ".claude.json");
        File.WriteAllText(path, "{\"primaryApiKey\":\"secret\"}");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Equal(ClientChange.Updated, ClientSettings.PlanClaudeCode(path, "dotnet", "/x/CoCern.Adapter.dll").Apply(TimeProvider.System));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        var created = Path.Combine(root, "new", "config.toml");
        ClientSettings.PlanCodex(created, "dotnet", "/x/CoCern.Adapter.dll").Apply(TimeProvider.System);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(created));
    }

    [Fact]
    public void A_chat_that_stopped_calling_cocern_is_pruned_after_a_week_unless_its_waiter_runs()
    {
        var folder = Path.Combine(root, "node");
        var openedAt = DateTimeOffset.UtcNow.AddDays(-30);
        var (idle, busy) = (Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        NodeState.WriteSessionHandle(folder, new SessionHandle("s-idle", "t1", "repo", "Idle", openedAt, Client: new ChatLocation("claude", idle)));
        NodeState.WriteSessionHandle(folder, new SessionHandle("s-busy", "t2", "repo", "Busy", openedAt, Client: new ChatLocation("claude", busy)));
        foreach (var id in new[] { "s-idle", "s-busy" }) File.SetLastWriteTimeUtc(NodeState.SessionHandlePath(folder, id), openedAt.UtcDateTime);
        // The busy chat called CoCern just now: its adapter touched the handle.
        var handoff = SessionHandoff.ForFolder(folder);
        handoff.Touch(JsonSerializer.SerializeToElement(new { sessionId = "s-busy" }));

        var announced = new NodeChats(folder, [], TimeProvider.System, NodeChats.DefaultLocateWindow, NodeChats.DefaultBudget).Read(_ => true);
        var ids = announced.Select(chat => chat!["relaySessionId"]!.GetValue<string>()).ToArray();
        Assert.Equal(["s-busy"], ids);
        Assert.False(File.Exists(NodeState.SessionHandlePath(folder, "s-idle")));
    }
}
