using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Adapter;
using CoCern.Adapter.Node;
using CoCern.Contracts;
using Xunit;

namespace CoCern.Tests;

/// <summary>
/// A Claude chat stopped at a client prompt reports itself through its own hooks: a permission notification or AskUserQuestion sets its <c>waitingFor</c>, the next turn event clears
/// what a hook set, and ordinary turns make no ack call. What the hub keeps of the model's own report is tested on the aggregate in <see cref="WaitingOnYouTests"/>.
/// The event shapes come from Claude Code's hooks reference and have not been tried on a real client.
/// </summary>
public sealed class WaitingHookTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cocern-waiting-hook-" + Guid.NewGuid().ToString("N"));
    private readonly string _chat = Guid.NewGuid().ToString();
    private readonly AckHub _hub = new();

    public WaitingHookTests()
    {
        using (var seed = NodeState.Open(_folder)) { seed.SetHub("http://127.0.0.1:5271/", null); seed.SaveCredential(new string('c', 64)); }
        NodeState.WriteSessionHandle(_folder, new SessionHandle("s-hook", "synthetic-token-hook", "repo", "Hook chat", DateTimeOffset.UtcNow.AddMinutes(-1), Chat: new ChatLocation("claude", _chat)));
    }

    public void Dispose()
    {
        _hub.Dispose();
        try { Directory.Delete(_folder, true); }
        catch (IOException) { /* An abandoned hook run may still hold a file for a moment; the folder is under the temp path. */ }
    }

    private string Permission(string? tool = null, string message = "Claude needs your permission", string? agent = null, string type = "permission_prompt") =>
        Input(new JsonObject { ["hook_event_name"] = "Notification", ["message"] = message, ["title"] = "Permission needed", ["notification_type"] = type, ["tool_name"] = tool, ["agent_id"] = agent });

    private string Question(string? agent = null) =>
        Input(new JsonObject { ["hook_event_name"] = "PreToolUse", ["tool_name"] = WaitingHook.QuestionTool, ["tool_use_id"] = "tool-1", ["agent_id"] = agent, ["tool_input"] = new JsonObject { ["questions"] = new JsonArray(new JsonObject { ["question"] = "SECRET QUESTION TEXT", ["header"] = "h" }) } });

    private string TurnEnd(string eventName, string? agent = null) => Input(new JsonObject { ["hook_event_name"] = eventName, ["agent_id"] = agent });

    private string Input(JsonObject fields)
    {
        fields["session_id"] = _chat;
        foreach (var empty in fields.Where(field => field.Value is null).Select(field => field.Key).ToArray()) fields.Remove(empty);
        return fields.ToJsonString();
    }

    private async Task Hook(string input, HttpMessageHandler? hub = null) =>
        Assert.Equal(0, await WaitingHook.RunAsync(new StringReader(input), _folder, hub ?? _hub));

    private async Task Prompt(HttpMessageHandler? hub = null)
    {
        using var output = new StringWriter();
        Assert.Equal(0, await PromptHook.RunAsync(new StringReader(JsonSerializer.Serialize(new { session_id = _chat, hook_event_name = "UserPromptSubmit", prompt = "go on" })), output, _folder, hub ?? _hub, null));
    }

    private async Task PostTool(string? agent = null, HttpMessageHandler? hub = null)
    {
        using var output = new StringWriter();
        var input = Input(new JsonObject { ["hook_event_name"] = "PostToolUse", ["tool_name"] = "Bash", ["agent_id"] = agent, ["tool_input"] = new JsonObject(), ["tool_response"] = new JsonObject() });
        Assert.Equal(0, await PostToolHook.RunAsync(new StringReader(input), output, _folder, hub ?? _hub));
    }

    private string Marker => Path.Combine(_folder, PromptHook.CacheFolder, _chat + ".waiting.json");

    [Fact]
    public async Task A_permission_prompt_sets_the_chats_report_with_the_fixed_text_tagged_as_the_nodes_and_the_hooks()
    {
        await Hook(Permission());
        var ack = Assert.Single(_hub.Acks);
        Assert.Equal(WaitingHook.PermissionText, ack["waitingFor"]!.GetValue<string>());
        Assert.True(ack[RelayLimits.HookWaitingField]!.GetValue<bool>());
        Assert.Equal(CallOrigins.Node, ack[CallOrigins.Field]!.GetValue<string>());
        Assert.Equal(("s-hook", "repo"), (ack["sessionId"]!.GetValue<string>(), ack["repositoryId"]!.GetValue<string>()));
        Assert.StartsWith("waiting-hook-", ack["requestId"]!.GetValue<string>());
        Assert.Null(ack["messageIds"]);
    }

    [Theory]
    [InlineData("Bash", "Claude Code is waiting for your permission to use Bash.")]
    [InlineData("mcp__cocern__task_update", "Claude Code is waiting for your permission to use mcp__cocern__task_update.")]
    [InlineData(null, WaitingHook.PermissionText)]
    [InlineData("", WaitingHook.PermissionText)]
    [InlineData("rm -rf C:\\secret", WaitingHook.PermissionText)]
    public async Task The_tool_is_named_only_when_the_input_gives_a_plain_tool_name(string? tool, string expected)
    {
        await Hook(Permission(tool));
        Assert.Equal(expected, Assert.Single(_hub.Acks)["waitingFor"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_report_never_carries_command_contents_or_a_question_and_stays_within_the_limit()
    {
        await Hook(Permission(message: "Claude needs your permission to run: curl https://example.test/SECRET-COMMAND"));
        await Hook(Question());
        Assert.Equal(2, _hub.Acks.Count);
        Assert.All(_hub.RawAcks, raw =>
        {
            Assert.DoesNotContain("SECRET", raw);
            Assert.DoesNotContain("curl", raw);
        });
        Assert.True(WaitingHook.PermissionReport(new string('a', 120)).Length <= RelayLimits.MaxWaitingTextLength);
        Assert.Equal(WaitingHook.PermissionText, WaitingHook.PermissionReport(new string('a', 121)));
    }

    [Fact]
    public async Task AskUserQuestion_sets_the_fixed_text_and_the_hook_has_nowhere_to_write_output()
    {
        await Hook(Question());
        Assert.Equal(WaitingHook.QuestionText, Assert.Single(_hub.Acks)["waitingFor"]!.GetValue<string>());
        // No output is what keeps a PreToolUse hook from influencing the call: no entry point takes a writer, and the command gives it none.
        var entries = typeof(WaitingHook).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).Where(method => method.Name is nameof(WaitingHook.RunAsync));
        Assert.All(entries, entry => Assert.DoesNotContain(entry.GetParameters(), parameter => parameter.ParameterType == typeof(TextWriter)));
    }

    [Fact]
    public async Task Another_tool_or_another_notification_sets_nothing()
    {
        await Hook(Input(new JsonObject { ["hook_event_name"] = "PreToolUse", ["tool_name"] = "Bash" }));
        await Hook(Permission(type: "idle_prompt"));
        Assert.Empty(_hub.Acks);
        Assert.False(File.Exists(Marker));
    }

    [Fact]
    public async Task The_next_prompt_clears_what_the_hook_set_once()
    {
        await Hook(Permission());
        await Prompt();
        Assert.Equal(2, _hub.Acks.Count);
        var clear = _hub.Acks.Last();
        Assert.Equal("", clear["waitingFor"]!.GetValue<string>());
        Assert.True(clear[RelayLimits.HookWaitingField]!.GetValue<bool>());
        Assert.Equal(CallOrigins.Node, clear[CallOrigins.Field]!.GetValue<string>());
        await Prompt();
        Assert.Equal(2, _hub.Acks.Count);
    }

    [Fact]
    public async Task The_next_tool_clears_because_an_answered_prompt_brings_no_new_one()
    {
        await Hook(Question());
        await PostTool();
        Assert.Equal("", _hub.Acks.Last()["waitingFor"]!.GetValue<string>());
        await PostTool();
        Assert.Equal(2, _hub.Acks.Count);
    }

    [Theory]
    [InlineData("Stop")]
    [InlineData("PostToolUseFailure")]
    public async Task The_end_of_the_turn_or_a_failed_tool_clears(string eventName)
    {
        await Hook(Permission());
        await Hook(TurnEnd(eventName));
        Assert.Equal("", _hub.Acks.Last()["waitingFor"]!.GetValue<string>());
        Assert.False(File.Exists(Marker));
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, RelayErrorCodes.Busy)]
    [InlineData(HttpStatusCode.Conflict, RelayErrorCodes.Paused)]
    [InlineData(HttpStatusCode.TooManyRequests, RelayErrorCodes.RateLimited)]
    [InlineData(HttpStatusCode.BadRequest, RelayErrorCodes.InvalidArguments)]
    public async Task A_clear_the_hub_refused_for_now_is_tried_again_on_a_later_event(HttpStatusCode status, string code)
    {
        await Hook(Permission());
        _hub.FailClears = (status, code);
        await Hook(TurnEnd("Stop"));
        Assert.Equal(2, _hub.Acks.Count);
        Assert.True(File.Exists(Marker));
        _hub.FailClears = null;
        // The turn is over, so nothing is pending and any later event retries, a tool finishing included.
        await PostTool();
        Assert.Equal(3, _hub.Acks.Count);
        Assert.Equal("", _hub.Acks.Last()["waitingFor"]!.GetValue<string>());
        Assert.False(File.Exists(Marker));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, RelayErrorCodes.SessionDenied)]
    [InlineData(HttpStatusCode.BadRequest, RelayErrorCodes.SessionUnknown)]
    public async Task A_session_the_hub_will_never_accept_again_ends_the_marker(HttpStatusCode status, string code)
    {
        await Hook(Permission());
        _hub.FailClears = (status, code);
        await Hook(TurnEnd("Stop"));
        Assert.False(File.Exists(Marker));
        await Prompt();
        Assert.Equal(2, _hub.Acks.Count);
    }

    [Fact]
    public async Task A_clear_answered_with_the_models_report_kept_ends_the_marker()
    {
        await Hook(Permission());
        _hub.KeepModelReport = true;
        await Hook(TurnEnd("Stop"));
        Assert.False(File.Exists(Marker));
        await Prompt();
        Assert.Equal(2, _hub.Acks.Count);
    }

    [Fact]
    public async Task A_report_the_hub_kept_for_the_model_is_not_cleared_later()
    {
        // The model reported first, so the hub answered that it kept that report.
        _hub.KeepModelReport = true;
        await Hook(Permission());
        Assert.False(File.Exists(Marker));
        await PostTool();
        await Prompt();
        await Hook(TurnEnd("Stop"));
        Assert.Single(_hub.Acks);
    }

    [Fact]
    public async Task Ordinary_turns_make_no_ack_call()
    {
        await Prompt();
        await PostTool();
        await PostTool();
        await Hook(TurnEnd("Stop"));
        await Hook(TurnEnd("PostToolUseFailure"));
        Assert.Empty(_hub.Acks);
    }

    [Fact]
    public async Task A_subagent_sets_nothing_and_clears_nothing()
    {
        await Hook(Permission(agent: "agent-1"));
        await Hook(Question(agent: "agent-1"));
        Assert.Empty(_hub.Acks);
        await Hook(Permission());
        await PostTool(agent: "agent-1");
        await Hook(TurnEnd("PostToolUseFailure", agent: "agent-1"));
        Assert.Single(_hub.Acks);
        Assert.True(File.Exists(Marker));
    }

    [Fact]
    public async Task A_hub_that_is_down_stays_silent_and_exits_zero_and_the_report_is_cleared_once_it_answers()
    {
        using var down = new ThrowingHub();
        await Hook(Permission(), down);
        await Hook(Question(), down);
        // The call may have landed, so the marker stays and a later hook clears it.
        Assert.True(File.Exists(Marker));
        await Hook(TurnEnd("Stop"), down);
        Assert.True(File.Exists(Marker));
        await PostTool();
        Assert.Equal("", Assert.Single(_hub.Acks)["waitingFor"]!.GetValue<string>());
        Assert.False(File.Exists(Marker));
    }

    [Fact]
    public async Task A_hub_that_never_answers_stays_silent_within_the_bound()
    {
        using var hanging = new HangingHub();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Hook(Permission(), hanging);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());
    }

    [Fact]
    public async Task A_refusal_from_the_hub_leaves_nothing_to_clear()
    {
        _hub.Refuse = true;
        await Hook(Permission());
        Assert.False(File.Exists(Marker));
        _hub.Refuse = false;
        await PostTool();
        Assert.Single(_hub.Acks);
    }

    [Fact]
    public async Task No_enrollment_no_session_and_bad_input_exit_zero_and_call_nothing()
    {
        Assert.Equal(0, await WaitingHook.RunAsync(new StringReader(Permission()), Path.Combine(_folder, "not-enrolled"), _hub));
        Assert.Equal(0, await WaitingHook.RunAsync(new StringReader(JsonSerializer.Serialize(new { session_id = Guid.NewGuid().ToString(), hook_event_name = "Notification", notification_type = "permission_prompt" })), _folder, _hub));
        Assert.Equal(0, await WaitingHook.RunAsync(new StringReader(JsonSerializer.Serialize(new { session_id = "not-a-uuid", hook_event_name = "Notification", notification_type = "permission_prompt" })), _folder, _hub));
        Assert.Equal(0, await WaitingHook.RunAsync(new StringReader("not json"), _folder, _hub));
        Assert.Equal(0, await WaitingHook.RunAsync(new StringReader("[1,2]"), _folder, _hub));
        Assert.Empty(_hub.Acks);
    }

    [Fact]
    public async Task A_chat_that_moved_session_leaves_the_old_report_to_expire_and_sends_nothing_to_the_new_one()
    {
        await Hook(Permission());
        NodeState.WriteSessionHandle(_folder, new SessionHandle("s-new", "synthetic-token-new", "repo", "Hook chat", DateTimeOffset.UtcNow, Chat: new ChatLocation("claude", _chat)));
        await PostTool();
        Assert.Single(_hub.Acks);
        Assert.False(File.Exists(Marker));
    }

    [Fact]
    public async Task A_marker_older_than_the_reports_own_limit_is_dropped_with_no_call()
    {
        var clock = new Clock { Now = DateTimeOffset.UtcNow };
        Assert.Equal(0, await WaitingHook.RunAsync(new StringReader(Permission()), _folder, _hub, null, clock));
        clock.Now += TimeSpan.FromHours(RelayLimits.WaitingForMaxHours + 1);
        Assert.Equal(0, await WaitingHook.RunAsync(new StringReader(TurnEnd("Stop")), _folder, _hub, null, clock));
        Assert.Single(_hub.Acks);
        Assert.False(File.Exists(Marker));
    }

    [Fact]
    public async Task A_marker_that_cannot_be_read_is_dropped_with_no_call()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Marker)!);
        File.WriteAllText(Marker, "{ not json");
        await PostTool();
        Assert.Empty(_hub.Acks);
        Assert.False(File.Exists(Marker));
    }

    [Fact]
    public async Task A_parallel_hook_holding_the_chats_lock_means_this_one_gives_up_quietly()
    {
        await Hook(Permission());
        using (new FileStream(Path.Combine(_folder, PromptHook.CacheFolder, _chat + ".waiting.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            await PostTool();
            await Hook(Question());
            Assert.Single(_hub.Acks);
        }
        // Nothing was lost: the marker is still there, and the next hook clears.
        await PostTool();
        Assert.Equal("", _hub.Acks.Last()["waitingFor"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_codex_chat_gets_nothing_from_its_prompt_hook_and_no_report_can_be_set_for_it()
    {
        var codex = Guid.NewGuid().ToString();
        NodeState.WriteSessionHandle(_folder, new SessionHandle("s-codex", "synthetic-token-codex", "repo", "Codex chat", DateTimeOffset.UtcNow, Chat: new ChatLocation("codex", codex)));
        Assert.Equal(0, await WaitingHook.RunAsync(new StringReader(JsonSerializer.Serialize(new { session_id = codex, hook_event_name = "Notification", notification_type = "permission_prompt" })), _folder, _hub));
        using var output = new StringWriter();
        Assert.Equal(0, await PromptHook.RunAsync(new StringReader(JsonSerializer.Serialize(new { session_id = codex })), output, _folder, _hub, null));
        Assert.Empty(_hub.Acks);
    }

    /// <summary>The hub for one chat: an empty inbox, quiet status, and an <c>ack</c> that answers as the session aggregate does.</summary>
    private sealed class AckHub : HttpMessageHandler
    {
        public readonly ConcurrentQueue<string> RawAcks = new();
        public bool KeepModelReport;
        public bool Refuse;
        public (HttpStatusCode Status, string Code)? FailClears;
        public List<JsonNode> Acks => RawAcks.Select(raw => JsonNode.Parse(raw)!).ToList();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/get_status")) return Reply("{\"sessionId\":\"s-hook\",\"attention\":{\"actionable\":0,\"urgent\":0,\"inbox\":0,\"standing\":0}}");
            if (path.EndsWith("/check_inbox")) return Reply("{\"items\":[],\"nextCursor\":null,\"truncated\":false,\"total\":0,\"etag\":\"e\"}");
            Assert.EndsWith("/ack", path);
            RawAcks.Enqueue(body);
            if (Refuse) return new(HttpStatusCode.Forbidden) { Content = new StringContent(JsonSerializer.Serialize(new { ok = false, code = RelayErrorCodes.SessionDenied, dataJson = "{}", version = 0 }), Encoding.UTF8, "application/json") };
            var text = JsonNode.Parse(body)!["waitingFor"]!.GetValue<string>();
            if (text.Length == 0 && FailClears is { } failure) return new(failure.Status) { Content = new StringContent(JsonSerializer.Serialize(new { ok = false, code = failure.Code, dataJson = "{}", version = 0 }), Encoding.UTF8, "application/json") };
            var data = new JsonObject { ["acknowledged"] = 0 };
            if (KeepModelReport) data["waitingForKept"] = true;
            else data["waitingFor"] = text.Length == 0 ? null : new JsonObject { ["since"] = "2026-11-05T00:00:00.0000000+00:00", ["expiresAt"] = "2026-11-05T12:00:00.0000000+00:00" };
            return Reply(data.ToJsonString());
        }

        private static HttpResponseMessage Reply(string data) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { ok = true, code = "ok", dataJson = data, version = 0 }), Encoding.UTF8, "application/json")
        };
    }

    private sealed class ThrowingHub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromException<HttpResponseMessage>(new HttpRequestException("refused"));
    }

    private sealed class HangingHub : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new(HttpStatusCode.OK);
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
