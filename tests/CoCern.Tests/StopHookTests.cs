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
    public void Plan_stop_hook_is_idempotent_and_replaces_only_its_own_outdated_handler()
    {
        Directory.CreateDirectory(root);
        const string Old = "\\\"dotnet\\\" \\\"C:/old/CoCern.Adapter.dll\\\" --stop-hook";
        const string New = "\"dotnet\" \"C:/new/CoCern.Adapter.dll\" --stop-hook";
        File.WriteAllText(Settings, "{\n  \"hooks\": {\n    \"Stop\": [ { \"hooks\": [ { \"type\": \"command\", \"command\": \"" + Old + "\", \"timeout\": 60 }, { \"type\": \"command\", \"command\": \"echo mine\" }, { \"type\": \"command\", \"command\": \"other-agent.exe --stop-hook\" } ] } ]\n  }\n}\n");
        ClientSettings.PlanStopHook(Settings, New).Apply(TimeProvider.System);
        var handlers = JsonNode.Parse(File.ReadAllText(Settings))!["hooks"]!["Stop"]!.AsArray().SelectMany(group => group!["hooks"]!.AsArray()).ToArray();
        var owned = Assert.Single(handlers, handler => handler!["command"]!.GetValue<string>().Contains("CoCern.Adapter.dll"))!;
        Assert.Equal(New, owned["command"]!.GetValue<string>());
        Assert.Equal(ClientSettings.StopHookTimeoutSeconds, owned["timeout"]!.GetValue<int>());
        Assert.True(owned["asyncRewake"]!.GetValue<bool>());
        Assert.Contains(handlers, handler => handler!["command"]!.GetValue<string>() == "echo mine");
        // Another tool's handler that uses the same switch is not ours: it is neither replaced nor removed.
        Assert.Contains(handlers, handler => handler!["command"]!.GetValue<string>() == "other-agent.exe --stop-hook");
        var once = File.ReadAllBytes(Settings);
        Assert.True(ClientSettings.PlanStopHook(Settings, New).IsUnchanged);
        Assert.Equal(once, File.ReadAllBytes(Settings));
    }

    [Fact]
    public void Plan_stop_hook_adds_its_own_handler_beside_a_foreign_handler_with_the_same_switch_and_dedupes_its_own()
    {
        Directory.CreateDirectory(root);
        const string Ours = "\\\"dotnet\\\" \\\"C:/a/CoCern.Adapter.dll\\\" --stop-hook";
        File.WriteAllText(Settings, "{\n  \"hooks\": {\n    \"Stop\": [ { \"hooks\": [ { \"type\": \"command\", \"command\": \"other-agent.exe --stop-hook\" } ] }, { \"hooks\": [ { \"type\": \"command\", \"command\": \"" + Ours + "\" } ] }, { \"hooks\": [ { \"type\": \"command\", \"command\": \"" + Ours + "\" } ] } ]\n  }\n}\n");
        ClientSettings.PlanStopHook(Settings, "\"C:/b/CoCern.Adapter.exe\" --stop-hook").Apply(TimeProvider.System);
        var commands = JsonNode.Parse(File.ReadAllText(Settings))!["hooks"]!["Stop"]!.AsArray().SelectMany(group => group!["hooks"]!.AsArray()).Select(handler => handler!["command"]!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "other-agent.exe --stop-hook", "\"C:/b/CoCern.Adapter.exe\" --stop-hook" }, commands);
    }

    [Fact]
    public void Plan_stop_hook_creates_a_missing_file_and_refuses_one_it_cannot_understand()
    {
        Directory.CreateDirectory(root);
        ClientSettings.PlanStopHook(Settings, "adapter --stop-hook").Apply(TimeProvider.System);
        Assert.Equal("adapter --stop-hook", JsonNode.Parse(File.ReadAllText(Settings))!["hooks"]!["Stop"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        File.WriteAllText(Settings, "{ \"hooks\": { \"Stop\": {} } }");
        Assert.Throws<ClientSettingsException>(() => ClientSettings.PlanStopHook(Settings, "adapter --stop-hook"));
        File.WriteAllText(Settings, "{ not json");
        Assert.Throws<ClientSettingsException>(() => ClientSettings.PlanStopHook(Settings, "adapter --stop-hook"));
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

    [Theory]
    [InlineData("timeout")]
    [InlineData("stopped")]
    public async Task A_wait_that_ends_without_mail_exits_0_silently(string status)
    {
        var error = new StringWriter();
        Assert.Equal(0, await StopHook.RunAsync(Input(Chat), error, "node", CancellationToken.None, (_, _, _, _, _) => Task.FromResult(status)));
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public async Task A_chat_without_a_node_session_or_with_a_waiter_already_running_exits_0_silently()
    {
        var error = new StringWriter();
        Assert.Equal(0, await StopHook.RunAsync(Input(Chat), error, "node", CancellationToken.None,
            (_, _, _, _, _) => throw new AdapterConfigurationException("No relay session on this PC belongs to Claude chat.")));
        Assert.Equal(0, await StopHook.RunAsync(Input(Chat), error, "node", CancellationToken.None,
            (_, _, _, _, _) => throw new IOException("hub unreachable")));
        Assert.Equal("", error.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ \"hook_event_name\": \"Stop\" }")]
    [InlineData("{ \"session_id\": \"not-a-chat\" }")]
    public async Task Input_without_a_chat_id_never_starts_a_waiter(string input)
    {
        var called = false;
        Assert.Equal(0, await StopHook.RunAsync(new StringReader(input), new StringWriter(), "node", CancellationToken.None, (_, _, _, _, _) => { called = true; return Task.FromResult("queued"); }));
        Assert.False(called);
    }

    [Fact]
    public async Task The_waiter_gives_up_before_the_client_would_kill_the_hook()
    {
        TimeSpan? limit = null;
        await StopHook.RunAsync(Input(Chat), new StringWriter(), "node", CancellationToken.None, (_, _, _, given, _) => { limit = given; return Task.FromResult("timeout"); });
        Assert.Equal(StopHook.WaitLimit, limit);
        Assert.True(StopHook.WaitLimit > TimeSpan.FromHours(23));
        Assert.True(StopHook.WaitLimit < TimeSpan.FromSeconds(ClientSettings.StopHookTimeoutSeconds));
    }

    private static StringReader Input(string chat) => new($"{{ \"session_id\": \"{chat}\", \"hook_event_name\": \"Stop\", \"stop_hook_active\": false }}");

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
