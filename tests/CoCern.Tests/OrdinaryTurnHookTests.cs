using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Adapter;
using CoCern.Adapter.Node;
using Xunit;

namespace CoCern.Tests;

/// <summary>
/// Mail reaches a chat during ordinary turns: <see cref="PromptHook"/> on each human message and <see cref="PostToolHook"/> after each tool call. Both poll headers,
/// print only what the printed ledger does not hold, and stay quiet when the hub is down, slow or absent. Written without being run; the input and output shapes come
/// from Claude Code's hooks reference and have not been tried on a real client.
/// </summary>
public sealed class OrdinaryTurnHookTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cocern-turn-hook-" + Guid.NewGuid().ToString("N"));
    private readonly string _chat = Guid.NewGuid().ToString();
    private readonly MailHub _hub = new();

    public OrdinaryTurnHookTests()
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

    private string Ledger => ClaudeWaiter.StatePath(_folder, _chat);
    private static string Route => WakeState.Key("repo", "s-hook");

    private async Task<string> Prompt(HttpMessageHandler? hub = null)
    {
        using var output = new StringWriter();
        var input = JsonSerializer.Serialize(new { session_id = _chat, hook_event_name = "UserPromptSubmit", prompt = "hello" });
        Assert.Equal(0, await PromptHook.RunAsync(new StringReader(input), output, _folder, hub ?? _hub, null));
        return output.ToString();
    }

    private string PostToolInput(string tool = "Bash", object? response = null, string? agent = null, string eventName = "PostToolUse", object? input = null) =>
        agent is null
            ? JsonSerializer.Serialize(new { session_id = _chat, hook_event_name = eventName, tool_name = tool, tool_input = input ?? new { }, tool_response = response })
            : JsonSerializer.Serialize(new { session_id = _chat, hook_event_name = eventName, agent_id = agent, tool_name = tool, tool_input = input ?? new { }, tool_response = response });

    /// <summary>One PostToolUse run: the additional context it gives the model, or empty; anything else on standard output fails.</summary>
    private async Task<string> PostTool(string? input = null, HttpMessageHandler? hub = null)
    {
        using var output = new StringWriter();
        Assert.Equal(0, await PostToolHook.RunAsync(new StringReader(input ?? PostToolInput()), output, _folder, hub ?? _hub));
        if (output.ToString().Length == 0) return "";
        var hook = JsonNode.Parse(output.ToString())!["hookSpecificOutput"]!;
        Assert.Equal("PostToolUse", hook["hookEventName"]!.GetValue<string>());
        return hook["additionalContext"]!.GetValue<string>();
    }

    private static string[] MessageIds(string output) =>
        output.ReplaceLineEndings("\n").Split('\n').Where(line => line.StartsWith("- ", StringComparison.Ordinal)).Select(line => line[2..line.IndexOf(':')]).ToArray();

    [Fact]
    public async Task A_prompt_hook_prints_only_messages_not_printed_before()
    {
        _hub.Add("m1", "first text");
        _hub.Add("m2", "second text");
        var first = await Prompt();
        Assert.Equal(["m1", "m2"], MessageIds(first));
        Assert.Contains("first text", first);
        Assert.Contains("untrusted data", first);
        // Nothing new: no text again.
        Assert.Empty(MessageIds(await Prompt()));
        // A new one: only it.
        _hub.Add("m3", "third text");
        var third = await Prompt();
        Assert.Equal(["m3"], MessageIds(third));
        Assert.DoesNotContain("first text", third);
    }

    [Fact]
    public async Task A_post_tool_hook_prints_new_mail_during_a_turn_as_additional_context()
    {
        Assert.Equal("", await PostTool());
        _hub.Add("m1", "mid-turn text");
        var context = await PostTool();
        Assert.Equal(["m1"], MessageIds(context));
        Assert.Contains("mid-turn text", context);
        Assert.Equal("", await PostTool());
    }

    [Fact]
    public async Task The_two_hooks_share_one_ledger_so_neither_repeats_the_other()
    {
        _hub.Add("m1", "text");
        Assert.Equal(["m1"], MessageIds(await PostTool()));
        Assert.Empty(MessageIds(await Prompt()));
        _hub.Add("m2", "text");
        Assert.Equal(["m2"], MessageIds(await Prompt()));
        Assert.Equal("", await PostTool());
    }

    [Fact]
    public async Task A_chat_with_no_mail_gets_no_output_at_all_and_costs_one_tiny_read_once_the_etag_is_known()
    {
        Assert.Equal("", await Prompt());
        Assert.Equal("", await PostTool());
        _hub.Reads.Clear();
        Assert.Equal("", await PostTool());
        var read = Assert.Single(_hub.Reads);
        Assert.Contains("\"headersOnly\":true", read);
        Assert.Contains($"\"knownInboxEtag\":\"{_hub.Etag}\"", read);
        Assert.Contains("\"attention\":false", read);
        Assert.Contains("\"backgroundCall\":true", read);
    }

    [Fact]
    public async Task Printed_mail_is_not_read_again_while_the_inbox_stays_the_same()
    {
        _hub.Add("m1", "text");
        Assert.Single(MessageIds(await PostTool()));
        _hub.Reads.Clear();
        Assert.Equal("", await PostTool());
        Assert.All(_hub.Reads, read => Assert.Contains("\"headersOnly\":true", read));
    }

    [Fact]
    public async Task A_new_message_behind_printed_ones_is_read_from_where_it_starts()
    {
        foreach (var id in Enumerable.Range(0, 60).Select(n => $"m{n:00}")) _hub.Add(id, "x");
        Assert.Equal(60, MessageIds(await PostTool()).Length);
        _hub.Add("m60", "newest");
        _hub.Reads.Clear();
        Assert.Equal(["m60"], MessageIds(await PostTool()));
        // The text read starts at the new message rather than paging the sixty already printed.
        Assert.Contains(_hub.Reads, read => !read.Contains("headersOnly") && read.Contains("\"cursor\":60"));
        Assert.DoesNotContain(_hub.Reads, read => !read.Contains("headersOnly") && read.Contains("\"cursor\":0,"));
    }

    // Trap 1: a count-only notice must not mark messages seen, because that removes the direct-message wake.

    [Fact]
    public async Task A_count_only_notice_marks_nothing_seen_or_printed_and_the_rest_prints_next_time()
    {
        _hub.Add("m1", new string('a', 6_000));
        _hub.Add("m2", new string('b', 6_000));
        var first = await PostTool();
        Assert.Equal(["m1"], MessageIds(first));
        Assert.Contains("1 more not shown; check_inbox reads it.", first);
        // Only the printed message is reported or recorded: the counted one keeps its wake.
        Assert.Equal(["m1"], _hub.SeenReports().SelectMany(ids => ids));
        Assert.Equal(["m1"], WakeState.ReadPrinted(Ledger, Route, ["m1", "m2"]));
        Assert.Equal(["m2"], MessageIds(await PostTool()));
        Assert.Equal(["m1", "m2"], _hub.SeenReports().SelectMany(ids => ids));
    }

    [Fact]
    public async Task The_status_line_counts_without_marking_anything_when_the_text_cannot_be_read()
    {
        _hub.Attention = "{\"actionable\":1,\"urgent\":0,\"inbox\":1,\"standing\":0}";
        _hub.Add("m1", "text");
        _hub.RefuseFullReads = true;
        var status = await Prompt();
        Assert.Equal("CoCern: 1 attention item, 1 inbox.", status.TrimEnd());
        Assert.Empty(_hub.SeenReports());
        Assert.Empty(WakeState.ReadPrinted(Ledger, Route, ["m1"]));
        // Nothing was lost: the next poll prints it.
        _hub.RefuseFullReads = false;
        Assert.Equal(["m1"], MessageIds(await PostTool()));
    }

    [Fact]
    public async Task A_message_too_long_to_print_gets_one_pointer_and_stays_unseen_and_unprinted()
    {
        _hub.Add("big", new string('x', ClaudeWaiter.MaxPrintedInbox * 2));
        _hub.Add("after", "short");
        var first = await PostTool();
        Assert.Contains("- big: (too long to print; call check_inbox to read it)", first);
        Assert.Contains("- after: short", first);
        Assert.Equal(["after"], _hub.SeenReports().SelectMany(ids => ids));
        Assert.Equal(["after"], WakeState.ReadPrinted(Ledger, Route, ["big", "after"]));
        // A hook runs on every tool call, so the pointer is not said again, even when the inbox changes.
        Assert.Equal("", await PostTool());
        _hub.Add("next", "short");
        Assert.Equal(["next"], MessageIds(await PostTool()));
        Assert.Empty(WakeState.ReadPrinted(Ledger, Route, ["big"]));
    }

    // Trap 2: a waiter holds its state lock for its whole wait, so a hook must still reach the ledger.

    [Fact]
    public async Task Hooks_print_and_record_while_a_waiter_holds_its_state()
    {
        _hub.Add("m1", "held");
        using var waiter = new WakeState(Ledger);
        Assert.Equal(["m1"], MessageIds(await Prompt()));
        Assert.Equal(["m1"], waiter.Printed(Route, ["m1"]));
        // And what the waiter printed, a hook does not repeat.
        waiter.MarkPrinted(Route, ["m2"]);
        _hub.Add("m2", "the waiter printed this");
        Assert.Equal("", await PostTool());
    }

    // Trap 4: the model's own check_inbox writes no ledger. The hook records it from tool_response; a form it cannot read costs one repeat.

    private static string ToolReply(params string[] ids) => ToolReplyFrom("repo", ids);

    private static string ToolReplyFrom(string repository, params string[] ids) => JsonSerializer.Serialize(new { ok = true, code = "ok", data = new { items = ids.Select(id => new { messageId = id, repositoryId = repository, text = "seen by the model" }), total = ids.Length } });

    /// <summary>A check_inbox call that named this chat's own session, as the model makes it with the handle session_open gave it.</summary>
    private static readonly object OwnCall = new { repositoryId = "repo", sessionId = "s-hook", sessionToken = "synthetic-token-hook" };

    private static object Blocks(string reply) => new[] { new { type = "text", text = reply } };

    [Theory]
    [InlineData("s-other", "repo")]
    [InlineData("s-hook", "other-repo")]
    [InlineData("s-other", "other-repo")]
    public async Task A_read_of_another_session_or_repository_leaves_the_same_ids_unprinted_here(string session, string repository)
    {
        _hub.Add("m1", "this chat's own text");
        var context = await PostTool(PostToolInput(PostToolHook.CheckInboxTool, Blocks(ToolReplyFrom(repository, "m1")), input: new { repositoryId = repository, sessionId = session, sessionToken = "synthetic-token-other" }));
        // The model read some other inbox's m1, so this inbox's m1 still has to reach it.
        Assert.Equal(["m1"], MessageIds(context));
        Assert.Contains("this chat's own text", context);
    }

    [Fact]
    public async Task A_handle_change_between_the_tool_and_the_hook_leaves_the_new_inbox_unprinted()
    {
        _hub.Add("m1", "the new session's text");
        var late = PostToolInput(PostToolHook.CheckInboxTool, Blocks(ToolReply("m1")), input: OwnCall);
        // The tool read the old session's inbox; by the time the hook runs, the chat has moved.
        NodeState.WriteSessionHandle(_folder, new SessionHandle("s-new", "synthetic-token-new", "repo", "Hook chat", DateTimeOffset.UtcNow, Chat: new ChatLocation("claude", _chat)));
        var context = await PostTool(late);
        Assert.Equal(["m1"], MessageIds(context));
        Assert.Contains("the new session's text", context);
    }

    [Fact]
    public async Task A_read_with_no_session_named_costs_one_repeat_because_its_owner_cannot_be_established()
    {
        // The adapter fills the session in itself for such a call, from its own memory of the repository, which need not be the handle this hook chose.
        _hub.Add("m1", "text");
        var implicitCall = PostToolInput(PostToolHook.CheckInboxTool, Blocks(ToolReply("m1")), input: new { repositoryId = "repo" });
        Assert.Equal(["m1"], MessageIds(await PostTool(implicitCall)));
        Assert.Equal("", await PostTool(implicitCall));
    }

    [Fact]
    public async Task A_tool_input_that_is_missing_costs_one_repeat()
    {
        _hub.Add("m1", "text");
        var input = JsonSerializer.Serialize(new { session_id = _chat, hook_event_name = "PostToolUse", tool_name = PostToolHook.CheckInboxTool, tool_response = Blocks(ToolReply("m1")) });
        Assert.Equal(["m1"], MessageIds(await PostTool(input)));
    }

    [Fact]
    public async Task An_item_from_another_repository_keeps_the_whole_result_from_being_recorded()
    {
        _hub.Add("m1", "text");
        _hub.Add("m2", "text");
        var reply = JsonSerializer.Serialize(new { ok = true, code = "ok", data = new { items = new[] { new { messageId = "m1", repositoryId = "repo", text = "x" }, new { messageId = "m2", repositoryId = "other-repo", text = "x" } } } });
        Assert.Equal(["m1", "m2"], MessageIds(await PostTool(PostToolInput(PostToolHook.CheckInboxTool, Blocks(reply), input: OwnCall))));
    }

    [Fact]
    public async Task An_item_that_does_not_say_its_repository_is_not_recorded()
    {
        _hub.Add("m1", "text");
        var reply = JsonSerializer.Serialize(new { ok = true, code = "ok", data = new { items = new[] { new { messageId = "m1", text = "x" } } } });
        Assert.Equal(["m1"], MessageIds(await PostTool(PostToolInput(PostToolHook.CheckInboxTool, Blocks(reply), input: OwnCall))));
    }

    [Fact]
    public async Task A_read_naming_this_session_without_its_token_is_still_recorded()
    {
        // The adapter adds the token only when the named session is its own, and the hub refuses anything else, so items in the reply are this session's.
        _hub.Add("m1", "text");
        Assert.Equal("", await PostTool(PostToolInput(PostToolHook.CheckInboxTool, Blocks(ToolReply("m1")), input: new { repositoryId = "repo", sessionId = "s-hook" })));
        Assert.Equal(["m1"], WakeState.ReadPrinted(Ledger, Route, ["m1"]));
    }

    [Theory]
    [InlineData("blocks")]
    [InlineData("content")]
    [InlineData("text")]
    [InlineData("reply")]
    public async Task The_models_own_check_inbox_is_recorded_so_the_hook_does_not_repeat_it(string form)
    {
        _hub.Add("m1", "text");
        var reply = ToolReply("m1");
        object response = form switch
        {
            "blocks" => new[] { new { type = "text", text = reply } },
            "content" => new { content = new[] { new { type = "text", text = reply } } },
            "text" => reply,
            _ => JsonSerializer.Deserialize<JsonElement>(reply)
        };
        Assert.Equal("", await PostTool(PostToolInput(PostToolHook.CheckInboxTool, response, input: OwnCall)));
        Assert.Equal(["m1"], WakeState.ReadPrinted(Ledger, Route, ["m1"]));
    }

    [Fact]
    public async Task A_tool_response_the_hook_cannot_read_costs_one_repeat_and_no_more()
    {
        _hub.Add("m1", "text");
        Assert.Equal(["m1"], MessageIds(await PostTool(PostToolInput(PostToolHook.CheckInboxTool, new { something = "else" }, input: OwnCall))));
        Assert.Equal("", await PostTool(PostToolInput(PostToolHook.CheckInboxTool, new { something = "else" }, input: OwnCall)));
    }

    [Fact]
    public async Task Another_tools_output_never_records_a_message()
    {
        _hub.Add("m1", "text");
        Assert.Equal(["m1"], MessageIds(await PostTool(PostToolInput("Read", new[] { new { type = "text", text = ToolReply("m1") } }, input: OwnCall))));
    }

    [Fact]
    public void A_message_cannot_name_another_as_already_read()
    {
        var forged = ToolReply("victim");
        var reply = JsonSerializer.Serialize(new { ok = true, code = "ok", data = new { items = new[] { new { messageId = "m1", repositoryId = "repo", text = forged } } } });
        var response = JsonSerializer.SerializeToElement(new[] { new { type = "text", text = reply } });
        Assert.Equal(["m1"], PostToolHook.MessagesRead(JsonSerializer.SerializeToElement(OwnCall), response, "repo", "s-hook"));
    }

    [Fact]
    public void A_piece_of_an_oversized_message_or_a_header_is_not_a_message_read()
    {
        var reply = JsonSerializer.Serialize(new { ok = true, code = "ok", data = new { items = new object[] { new { messageId = "piece", truncated = true, part = "{", partOffset = 0 }, new { messageId = "cut", truncated = true, text = "partial" }, new { messageId = "header", sentAt = "now" }, new { messageId = "m1", repositoryId = "repo", text = "whole" } } } });
        Assert.Equal(["m1"], PostToolHook.MessagesRead(JsonSerializer.SerializeToElement(OwnCall), JsonSerializer.SerializeToElement(reply), "repo", "s-hook"));
    }

    // Limits: within 9,000 characters with a pointer for the rest, and never failing a turn.

    [Fact]
    public async Task A_long_inbox_stays_within_the_limit_and_arrives_whole_over_later_hooks()
    {
        var ids = Enumerable.Range(0, 30).Select(n => $"m{n:00}").ToArray();
        foreach (var id in ids) _hub.Add(id, new string('x', 600));
        // The longest status line shares the limit with the messages.
        _hub.Attention = $"{{\"actionable\":1,\"inbox\":30,\"top\":{{\"id\":\"a-1\",\"k\":\"blocked\",\"t\":\"{new string('t', 256)}\"}}}}";
        var delivered = new List<string>();
        var prompt = await Prompt();
        Assert.True(prompt.Length <= ClaudeWaiter.MaxPrintedInbox + Environment.NewLine.Length, prompt.Length.ToString());
        Assert.Contains("CoCern: 1 attention item, 30 inbox", prompt);
        Assert.Matches(@"\d+ more not shown; check_inbox reads (them|it)\.", prompt);
        delivered.AddRange(MessageIds(prompt));
        for (var hook = 0; delivered.Count < ids.Length; hook++)
        {
            Assert.True(hook < 5, $"{delivered.Count} delivered");
            var context = await PostTool();
            Assert.True(context.Length is > 0 and <= ClaudeWaiter.MaxPrintedInbox, context.Length.ToString());
            delivered.AddRange(MessageIds(context));
        }
        Assert.Equal(ids, delivered);
        Assert.Equal("", await PostTool());
    }

    [Fact]
    public async Task A_hub_that_is_down_or_refuses_means_no_output_and_exit_zero()
    {
        _hub.Add("m1", "text");
        using var down = new ThrowingHub(new HttpRequestException("refused"));
        Assert.Equal("", await Prompt(down));
        Assert.Equal("", await PostTool(hub: down));
        using var failing = new StatusHub(HttpStatusCode.InternalServerError);
        Assert.Equal("", await Prompt(failing));
        Assert.Equal("", await PostTool(hub: failing));
        // Nothing was recorded, so the mail still arrives once the hub answers.
        Assert.Equal(["m1"], MessageIds(await PostTool()));
    }

    [Fact]
    public async Task A_hub_that_never_answers_means_no_output_within_the_bound()
    {
        using var hanging = new HangingHub();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal("", await PostTool(hub: hanging));
        Assert.Equal("", await Prompt(hanging));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), watch.Elapsed.ToString());
    }

    [Fact]
    public async Task A_text_read_that_hangs_prints_nothing_and_marks_nothing()
    {
        _hub.Add("m1", "text");
        _hub.HangFullReads = true;
        Assert.Equal("", await PostTool());
        Assert.Empty(WakeState.ReadPrinted(Ledger, Route, ["m1"]));
    }

    [Fact]
    public async Task No_enrollment_no_session_and_bad_input_mean_no_output_and_exit_zero()
    {
        _hub.Add("m1", "text");
        var elsewhere = Path.Combine(_folder, "not-enrolled");
        using var silent = new StringWriter();
        Assert.Equal(0, await PostToolHook.RunAsync(new StringReader(PostToolInput()), silent, elsewhere, _hub));
        Assert.Equal(0, await PromptHook.RunAsync(new StringReader(JsonSerializer.Serialize(new { session_id = _chat })), silent, elsewhere, _hub, null));
        Assert.Equal(0, await PostToolHook.RunAsync(new StringReader(JsonSerializer.Serialize(new { session_id = Guid.NewGuid().ToString(), hook_event_name = "PostToolUse" })), silent, _folder, _hub));
        Assert.Equal(0, await PostToolHook.RunAsync(new StringReader(JsonSerializer.Serialize(new { session_id = "not-a-uuid", hook_event_name = "PostToolUse" })), silent, _folder, _hub));
        Assert.Equal(0, await PostToolHook.RunAsync(new StringReader("not json"), silent, _folder, _hub));
        Assert.Equal(0, await PostToolHook.RunAsync(new StringReader("[1,2]"), silent, _folder, _hub));
        Assert.Equal("", silent.ToString());
        Assert.Empty(_hub.Reads);
    }

    [Fact]
    public async Task Input_that_never_ends_means_no_output_within_the_bound()
    {
        _hub.Add("m1", "text");
        using var silent = new StringWriter();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(0, await PostToolHook.RunAsync(new NeverEnding(), silent, _folder, _hub));
        Assert.Equal("", silent.ToString());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());
    }

    [Fact]
    public async Task Input_too_long_to_keep_is_read_to_its_end_and_skips_only_this_poll()
    {
        _hub.Add("m1", "text");
        var reader = new StringReader(PostToolInput(response: new string('x', 5 * 1024 * 1024)));
        using var output = new StringWriter();
        Assert.Equal(0, await PostToolHook.RunAsync(reader, output, _folder, _hub));
        Assert.Equal("", output.ToString());
        Assert.Equal(-1, reader.Peek());
        Assert.Equal(["m1"], MessageIds(await PostTool()));
    }

    [Fact]
    public async Task A_subagents_tool_call_prints_nothing_because_its_context_is_not_the_chats()
    {
        _hub.Add("m1", "text");
        Assert.Equal("", await PostTool(PostToolInput(agent: "agent-1")));
        Assert.Empty(_hub.Reads);
        Assert.Empty(WakeState.ReadPrinted(Ledger, Route, ["m1"]));
        // Its own check_inbox reached only the subagent, so it records nothing either.
        Assert.Equal("", await PostTool(PostToolInput(PostToolHook.CheckInboxTool, new[] { new { type = "text", text = ToolReply("m1") } }, agent: "agent-1", input: OwnCall)));
        Assert.Empty(WakeState.ReadPrinted(Ledger, Route, ["m1"]));
        Assert.Equal(["m1"], MessageIds(await PostTool()));
    }

    [Fact]
    public async Task Another_event_does_not_run_the_post_tool_hook()
    {
        _hub.Add("m1", "text");
        Assert.Equal("", await PostTool(PostToolInput(eventName: "PreToolUse")));
        Assert.Empty(_hub.Reads);
    }

    [Fact]
    public async Task A_hook_finding_another_hook_of_this_chat_at_work_leaves_the_mail_to_it()
    {
        _hub.Add("m1", "text");
        Directory.CreateDirectory(Path.Combine(_folder, PromptHook.CacheFolder));
        using (new FileStream(Path.Combine(_folder, PromptHook.CacheFolder, _chat + ".mail.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal("", await PostTool());
            Assert.Empty(_hub.Reads);
        }
        Assert.Equal(["m1"], MessageIds(await PostTool()));
    }

    [Fact]
    public async Task A_codex_chat_keeps_the_status_line_and_gets_no_mail_because_it_has_no_ledger()
    {
        var codex = Guid.NewGuid().ToString();
        NodeState.WriteSessionHandle(_folder, new SessionHandle("s-codex", "synthetic-token-codex", "repo", "Codex chat", DateTimeOffset.UtcNow, Chat: new ChatLocation("codex", codex)));
        _hub.Attention = "{\"actionable\":0,\"urgent\":0,\"inbox\":1,\"standing\":0}";
        _hub.Add("m1", "text");
        using var output = new StringWriter();
        Assert.Equal(0, await PromptHook.RunAsync(new StringReader(JsonSerializer.Serialize(new { session_id = codex })), output, _folder, _hub, null));
        Assert.Equal("CoCern: 0 attention items, 1 inbox.", output.ToString().TrimEnd());
        Assert.Empty(_hub.Reads);
    }

    [Fact]
    public async Task The_status_line_goes_quiet_when_nothing_waits_and_its_answer_is_still_cached()
    {
        _hub.Attention = "{\"actionable\":0,\"urgent\":0,\"inbox\":0,\"standing\":7}";
        Assert.Equal("", await Prompt());
        Assert.Equal("", await Prompt());
        Assert.Equal(1, _hub.StatusReads);
    }

    [Fact]
    public async Task A_chat_that_moved_session_prints_its_new_inbox_whole()
    {
        _hub.Add("m1", "text");
        Assert.Equal(["m1"], MessageIds(await PostTool()));
        NodeState.WriteSessionHandle(_folder, new SessionHandle("s-new", "synthetic-token-new", "repo", "Hook chat", DateTimeOffset.UtcNow, Chat: new ChatLocation("claude", _chat)));
        // The same message ID under another session is that session's to print; the old route's etag and record do not apply.
        Assert.Equal(["m1"], MessageIds(await PostTool()));
    }

    /// <summary>The hub's get_status and check_inbox for one chat: headers with an etag, unchanged answers, full pages, and a record of every inbox read.</summary>
    private sealed class MailHub : HttpMessageHandler
    {
        private readonly List<(string Id, string Text)> _messages = [];
        private int _statusReads;
        public readonly ConcurrentQueue<string> Reads = new();
        public string Attention = "{\"actionable\":0,\"urgent\":0,\"inbox\":0,\"standing\":0}";
        public volatile bool RefuseFullReads;
        public volatile bool HangFullReads;
        public int StatusReads => Volatile.Read(ref _statusReads);
        public string Etag { get { lock (_messages) return "etag-" + string.Join("-", _messages.Select(message => message.Id)); } }

        public void Add(string id, string text) { lock (_messages) _messages.Add((id, text)); }

        public string[][] SeenReports() => Reads.Select(read => JsonNode.Parse(read)!["seenMessageIds"]).OfType<JsonArray>().Select(ids => ids.Select(id => id!.GetValue<string>()).ToArray()).ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (request.RequestUri!.AbsolutePath.EndsWith("/get_status"))
            {
                Interlocked.Increment(ref _statusReads);
                return Reply("{\"sessionId\":\"s-hook\",\"attention\":" + Attention + "}");
            }
            Assert.EndsWith("/check_inbox", request.RequestUri.AbsolutePath);
            Reads.Enqueue(body);
            using var args = JsonDocument.Parse(body);
            var headers = args.RootElement.TryGetProperty("headersOnly", out var flag) && flag.GetBoolean();
            if (!headers && RefuseFullReads) return new(HttpStatusCode.ServiceUnavailable);
            if (!headers && HangFullReads) await Task.Delay(Timeout.Infinite, cancellationToken);
            var etag = Etag;
            if (headers && args.RootElement.TryGetProperty("knownInboxEtag", out var known) && known.GetString() == etag)
                return Reply(new JsonObject { ["unchanged"] = true, ["etag"] = etag }.ToJsonString());
            var cursor = args.RootElement.TryGetProperty("cursor", out var from) ? from.GetInt32() : 0;
            var limit = args.RootElement.TryGetProperty("limit", out var asked) ? asked.GetInt32() : 50;
            (string Id, string Text)[] all;
            lock (_messages) all = _messages.ToArray();
            var page = all.Skip(cursor).Take(limit).ToArray();
            var more = cursor + page.Length < all.Length;
            var items = new JsonArray(page.Select(message =>
            {
                var item = new JsonObject { ["messageId"] = message.Id, ["repositoryId"] = "repo" };
                if (!headers) item["text"] = message.Text;
                return (JsonNode)item;
            }).ToArray());
            var data = new JsonObject { ["items"] = items, ["nextCursor"] = more ? cursor + page.Length : null, ["truncated"] = more, ["total"] = all.Length };
            if (headers) data["etag"] = etag;
            return Reply(data.ToJsonString());
        }

        private static HttpResponseMessage Reply(string data) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { ok = true, code = "ok", dataJson = data, version = 0 }), Encoding.UTF8, "application/json")
        };
    }

    private sealed class ThrowingHub(Exception failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromException<HttpResponseMessage>(failure);
    }

    private sealed class StatusHub(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(status));
    }

    private sealed class HangingHub : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new(HttpStatusCode.OK);
        }
    }

    /// <summary>Hook input that never arrives, like a client that keeps standard input open.</summary>
    private sealed class NeverEnding : TextReader
    {
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default) { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
        public override Task<int> ReadAsync(char[] buffer, int index, int count) => ReadAsync(buffer.AsMemory(index, count)).AsTask();
    }
}
