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

}
