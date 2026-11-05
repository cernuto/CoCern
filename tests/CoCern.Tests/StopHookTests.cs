using System.Text.Json.Nodes;
using CoCern.Adapter;
using CoCern.Adapter.Node;
using CoCern.Adapter.Setup;
using Xunit;

namespace CoCern.Tests;

/// <summary>
/// The Claude Code Stop hook keeps an idle chat reachable: when a turn ends it waits on the chat's inbox through the node, and wakes the
/// chat (exit 2, asyncRewake) only when the hub queues a wake. Everything else ends quietly so a hook never troubles a chat.
/// </summary>
public sealed class StopHookTests : IDisposable
{
    private const string Chat = "0b6a2f4e-3c1d-4e5f-8a9b-1c2d3e4f5a6b";
    private readonly string root = Path.Combine(Path.GetTempPath(), "cocern-stop-hook-" + Guid.NewGuid().ToString("N"));
    private string Settings => Path.Combine(root, "settings.json");

    [Fact]
    public void Plan_stop_hook_adds_an_async_rewake_handler_with_a_day_long_timeout_and_keeps_other_hooks()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Settings, "{\n  \"model\": \"opus\",\n  \"hooks\": {\n    \"Stop\": [ { \"hooks\": [ { \"type\": \"command\", \"command\": \"echo mine\" } ] } ],\n    \"UserPromptSubmit\": [ { \"hooks\": [ { \"type\": \"command\", \"command\": \"echo prompt\" } ] } ]\n  }\n}\n");
        var plan = ClientSettings.PlanStopHook(Settings, "adapter --stop-hook");
        Assert.False(plan.IsUnchanged);
        plan.Apply(TimeProvider.System);
        var document = JsonNode.Parse(File.ReadAllText(Settings))!;
        var handlers = document["hooks"]!["Stop"]!.AsArray().SelectMany(group => group!["hooks"]!.AsArray()).ToArray();
        var owned = Assert.Single(handlers, handler => handler!["command"]!.GetValue<string>() == "adapter --stop-hook")!;
        Assert.True(owned["asyncRewake"]!.GetValue<bool>());
        Assert.Equal(ClientSettings.StopHookTimeoutSeconds, owned["timeout"]!.GetValue<int>());
        Assert.Equal(24 * 60 * 60, ClientSettings.StopHookTimeoutSeconds);
        Assert.Contains(handlers, handler => handler!["command"]!.GetValue<string>() == "echo mine");
        Assert.Equal("echo prompt", document["hooks"]!["UserPromptSubmit"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Equal("opus", document["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_queued_wake_exits_2_with_the_delivered_messages_so_the_idle_chat_wakes()
    {
        var error = new StringWriter();
        string? waitedFor = null;
        var code = await StopHook.RunAsync(Input(Chat), error, "node", CancellationToken.None, (folder, chat, deliver, limit, ct) =>
        {
            waitedFor = chat;
            deliver("CoCern: 1 new message\nhello from a peer");
            return Task.FromResult("queued");
        });
        Assert.Equal(2, code);
        Assert.Equal(Chat, waitedFor);
        Assert.Contains("hello from a peer", error.ToString());
    }

    [Fact]
    public async Task A_chat_with_its_own_local_connection_waits_there_and_any_other_chat_waits_on_the_node()
    {
        string? folder = null;
        StopHook.Waiter wait = (given, _, _, _, _) => { folder = given; return Task.FromResult("timeout"); };
        await StopHook.RunAsync(Input(Chat), new StringWriter(), "node", CancellationToken.None, wait, chat => chat == Chat ? "local-scope" : null);
        Assert.Equal("local-scope", folder);
        await StopHook.RunAsync(Input(Chat), new StringWriter(), "node", CancellationToken.None, wait, _ => null);
        Assert.Equal("node", folder);
    }

    private static StringReader Input(string chat) => new($"{{ \"session_id\": \"{chat}\", \"hook_event_name\": \"Stop\", \"stop_hook_active\": false }}");

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
