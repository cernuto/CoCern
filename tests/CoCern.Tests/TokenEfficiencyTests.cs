using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using CoCern.Adapter;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Host;
using CoCern.Storage;
using Xunit;
using Xunit.Abstractions;
using ActivityEvent = CoCern.Core.ActivityEvent;

namespace CoCern.Tests;

/// <summary>Tool definitions sit in every turn's context, so the adapter keeps them as small as the chosen surface allows.</summary>
public sealed class TokenEfficiencyTests(ITestOutputHelper output)
{
    private static readonly string[] ChatTools = ["session_open", "send_message", "check_inbox", "ack", "chat_directory"];

    [Fact]
    public async Task Search_replies_stay_small_default_to_three_hits_one_per_task_and_drop_weak_ones()
    {
        await using var host = new RelayHostFixture();
        using var client = host.AuthorizedClient();
        var session = JsonDocument.Parse((await host.Call(client, "session_open", new { repositoryId = "repo", requestId = "budget-session" })).GetProperty("dataJson").GetString()!).RootElement;
        var (id, token) = (session.GetProperty("sessionId").GetString(), session.GetProperty("sessionToken").GetString());
        async Task Task(string taskId, string notes, object? message = null) =>
            Assert.True((await host.Call(client, "task_update", new { repositoryId = "repo", requestId = "budget-" + taskId, sessionId = id, sessionToken = token, taskId, expectedVersion = 0, notes, message })).GetProperty("ok").GetBoolean());
        // Each strong task mentions the words in its notes and again in its conversation: still one hit per task.
        for (var i = 0; i < 6; i++) await Task($"strong-{i}", $"deploy rollback runbook step {i}", new { text = "deploy rollback again" });
        await Task("weak", string.Join(' ', Enumerable.Repeat("unrelated filler text about other things entirely", 70)) + " deploy rollback");
        for (var i = 0; i < 12; i++) await Task(new string((char)('a' + i), 200), "deploy rollback with a long task ID " + new string('x', 40));
        async Task<(JsonElement Hits, int Bytes, bool Truncated)> Search(object arguments)
        {
            var reply = await host.Call(client, "search", arguments);
            Assert.True(reply.GetProperty("ok").GetBoolean(), reply.ToString());
            var data = reply.GetProperty("dataJson").GetString()!;
            using var document = JsonDocument.Parse(data);
            return (document.RootElement.GetProperty("hits").Clone(), Encoding.UTF8.GetByteCount(data), document.RootElement.TryGetProperty("truncated", out _));
        }

        var defaults = await Search(new { repositoryId = "repo", sessionId = id, sessionToken = token, query = "deploy rollback" });
        Assert.Equal(RelayLimits.DefaultSearchResults, defaults.Hits.GetArrayLength());
        Assert.True(defaults.Bytes <= RelayLimits.MaxSearchReplyBytes);
        var hit = defaults.Hits[0];
        Assert.Equal(new[] { "at", "id", "k", "s", "x" }, hit.EnumerateObject().Select(property => property.Name).Order());
        Assert.Matches("^\\d{4}-\\d{2}-\\d{2}$", hit.GetProperty("at").GetString()!);

        var strong = await Search(new { repositoryId = "repo", sessionId = id, sessionToken = token, query = "rollback runbook", limit = 10 });
        var ids = strong.Hits.EnumerateArray().Select(x => x.GetProperty("id").GetString()).ToArray();
        Assert.Equal(ids.Distinct().Count(), ids.Length);
        Assert.Equal(6, ids.Count(x => x!.StartsWith("strong-", StringComparison.Ordinal)));
        Assert.DoesNotContain("weak", ids);

        // Ten hits with 200-character IDs would not fit: the weakest give way and the reply says so.
        var wide = await Search(new { repositoryId = "repo", sessionId = id, sessionToken = token, query = "long task", limit = 10 });
        Assert.True(wide.Bytes <= RelayLimits.MaxSearchReplyBytes, $"{wide.Bytes} bytes");
        Assert.True(wide.Truncated);
        Assert.InRange(wide.Hits.GetArrayLength(), 1, 9);
    }

    [Fact]
    public async Task A_worst_case_attention_offer_fits_the_composition_reserve()
    {
        await using var host = new RelayHostFixture();
        using var client = host.AuthorizedClient();
        var session = JsonDocument.Parse((await host.Call(client, "session_open", new { repositoryId = "repo", requestId = "offer-budget" })).GetProperty("dataJson").GetString()!).RootElement;
        var (id, token) = (session.GetProperty("sessionId").GetString()!, session.GetProperty("sessionToken").GetString()!);
        var longId = new string('t', RelayLimits.MaxIdentifierLength);
        var grains = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Orleans.IGrainFactory>(host.Services);
        var raised = await grains.GetGrain<CoCern.Core.ISessionGrain>(id).ExecuteAsync(new CoCern.Domain.RelayCaller("test-user", false, ["repo"], true), "__attention_raise", JsonSerializer.Serialize(new
        {
            repositoryId = "repo", kind = "blocked", taskId = longId, boardId = new string('b', RelayLimits.MaxIdentifierLength), note = new string('\u00e9', 1_000),
            refs = Enumerable.Range(0, 20).Select(index => index + new string('r', 250)).ToArray()
        }));
        Assert.True(raised.Ok, raised.Code);
        var reply = await host.Call(client, "task_update", new { repositoryId = "repo", requestId = "offer-budget-1", sessionId = id, sessionToken = token, taskId = longId, expectedVersion = 0, notes = new string('n', 16_000) });
        var data = reply.GetProperty("dataJson").GetString()!;
        using var document = JsonDocument.Parse(data);
        var offer = document.RootElement.GetProperty("attention");
        Assert.Equal(RelayLimits.MaxAttentionTextLength, offer.GetProperty("x").GetString()!.Length);
        Assert.True(Encoding.UTF8.GetByteCount(offer.GetRawText()) <= RelayLimits.MaxAttentionOfferBytes);
        Assert.True(Encoding.UTF8.GetByteCount(reply.GetRawText()) <= RelayLimits.MaxPayloadBytes);
        Assert.True(RelayLimits.MaxAttentionOfferBytes <= RelayLimits.CompositionReserveBytes);
    }

    [Fact]
    public void Chat_profile_advertises_only_the_messaging_tools()
    {
        Assert.Equal(ChatTools.Order(), CoordinationTools.NamesForRole(false, chatOnly: true).Order());
        Assert.Equal(ChatTools.Order(), Tools(chatOnly: true).Select(tool => tool.ProtocolTool.Name).Order());
        Assert.Contains("task_update", CoordinationTools.NamesForRole(false));
    }

    [Fact]
    public async Task Chat_profile_refuses_other_tools_without_network()
    {
        using var client = new HttpClient(new NoNetwork());
        var forwarder = new RelayForwarder(client, new AdapterOptions("http://127.0.0.1:5271", "client-secret") { ChatOnly = true });
        Assert.Equal("unknown_tool", (await forwarder.ForwardAsync("task_update", JsonSerializer.Deserialize<JsonElement>("{}"))).Code);
    }

    [Theory]
    [InlineData("chat", true)]
    [InlineData("CHAT", true)]
    [InlineData(null, false)]
    [InlineData("worker", false)]
    public void Profile_comes_from_COCERN_PROFILE(string? profile, bool chatOnly)
    {
        var variables = new Dictionary<string, string?> { ["COCERN_TOKEN"] = "client-secret", ["COCERN_PROFILE"] = profile };
        Assert.Equal(chatOnly, AdapterOptions.FromEnvironment(key => variables.GetValueOrDefault(key)).ChatOnly);
    }

    [Fact]
    public void Tool_descriptions_do_not_repeat_shared_guidance()
    {
        var descriptions = Tools(chatOnly: false).Select(tool => tool.ProtocolTool.Description!).ToArray();
        Assert.All(descriptions, description => Assert.DoesNotContain("Pass the hub tool's JSON arguments directly", description));
    }

    [Fact]
    public void Chat_profile_definitions_fit_a_small_budget()
    {
        var size = JsonSerializer.Serialize(Tools(chatOnly: true).Select(tool => tool.ProtocolTool)).Length;
        Assert.True(size < 6_000, $"Chat profile tool definitions are {size} characters.");
    }

    [Fact]
    public void Send_message_advertises_acknowledging_in_the_same_call()
    {
        Assert.True(RelayToolCatalog.TryGet("send_message", out var tool));
        var property = RelayToolCatalog.ForwardingSchema(tool).GetProperty("properties").GetProperty("ackMessageIds");
        Assert.Equal("array", property.GetProperty("type").GetString());
    }

    [Fact]
    public void Check_inbox_advertises_a_bounded_wait()
    {
        var schema = Tools(chatOnly: true).Single(tool => tool.ProtocolTool.Name == "check_inbox").ProtocolTool.InputSchema;
        var wait = schema.GetProperty("properties").GetProperty("waitSeconds");
        Assert.Equal("integer", wait.GetProperty("type").GetString());
        Assert.Equal(InboxWait.MaxSeconds, wait.GetProperty("maximum").GetInt32());
        Assert.True(InboxWait.MaxSeconds < 60, "The wait must end inside Codex's default 60-second MCP tool timeout.");
    }

    [Fact]
    public async Task Waiting_check_inbox_polls_the_hub_not_the_model_and_returns_on_first_mail()
    {
        var hub = new CountingInbox { MailOnCall = 3 };
        using var client = new HttpClient(hub);
        var forwarder = new RelayForwarder(client, new AdapterOptions("http://127.0.0.1:5271", "client-secret"));
        var arguments = JsonSerializer.Deserialize<JsonElement>("{\"repositoryId\":\"repo\",\"sessionId\":\"s\",\"sessionToken\":\"t\",\"waitSeconds\":30}");
        var reply = await InboxWait.CheckAsync(forwarder, arguments, TimeSpan.FromMilliseconds(10), CancellationToken.None);
        Assert.True(reply.Ok);
        Assert.Equal(1, reply.Data!.Value.GetProperty("total").GetInt32());
        Assert.Equal(3, hub.Calls);
        // The adapter-only option never reaches the hub, and the polls, mostly discarded unseen, take no attention offer.
        Assert.DoesNotContain("waitSeconds", hub.LastBody);
        Assert.Contains("\"attention\":false", hub.LastBody);
        // The poll that returns reaches the model, so the hub closes the info items of the messages it lists.
        Assert.Contains("\"seen\":true", hub.LastBody);
    }

    [Fact]
    public async Task Waiting_check_inbox_returns_the_empty_inbox_when_the_wait_ends()
    {
        var hub = new CountingInbox { MailOnCall = int.MaxValue };
        using var client = new HttpClient(hub);
        var forwarder = new RelayForwarder(client, new AdapterOptions("http://127.0.0.1:5271", "client-secret"));
        var arguments = JsonSerializer.Deserialize<JsonElement>("{\"repositoryId\":\"repo\",\"waitSeconds\":1}");
        var reply = await InboxWait.CheckAsync(forwarder, arguments, TimeSpan.FromMilliseconds(100), CancellationToken.None);
        Assert.True(reply.Ok);
        Assert.Equal(0, reply.Data!.Value.GetProperty("total").GetInt32());
        // At least the first read, and never faster than the poll interval (a loaded machine may fit only one read into the second).
        Assert.InRange(hub.Calls, 1, 12);

        // Without waitSeconds it is one plain call, exactly as before.
        hub.Calls = 0;
        await InboxWait.CheckAsync(forwarder, JsonSerializer.Deserialize<JsonElement>("{\"repositoryId\":\"repo\"}"), TimeSpan.FromMilliseconds(10), CancellationToken.None);
        Assert.Equal(1, hub.Calls);
    }

    [Fact]
    public async Task Unchanged_read_returns_only_its_etag()
    {
        var hub = new FixedReply { Data = "{\"items\":[{\"label\":\"a\"}],\"truncated\":false}" };
        using var client = new HttpClient(hub);
        var forwarder = new RelayForwarder(client, new AdapterOptions("http://127.0.0.1:5271", "client-secret"));
        var first = await ConditionalRead.ForwardAsync(forwarder, "chat_directory", Json("{\"repositoryId\":\"repo\"}"), CancellationToken.None);
        var etag = first.Data!.Value.GetProperty("etag").GetString()!;
        Assert.Equal("a", first.Data.Value.GetProperty("items")[0].GetProperty("label").GetString());

        var again = await ConditionalRead.ForwardAsync(forwarder, "chat_directory", Json($"{{\"repositoryId\":\"repo\",\"ifNoneMatch\":\"{etag}\"}}"), CancellationToken.None);
        Assert.True(again.Ok);
        Assert.True(again.Data!.Value.GetProperty("unchanged").GetBoolean());
        Assert.False(again.Data.Value.TryGetProperty("items", out _));
        Assert.DoesNotContain("ifNoneMatch", hub.LastBody);

        hub.Data = "{\"items\":[{\"label\":\"b\"}],\"truncated\":false}";
        var changed = await ConditionalRead.ForwardAsync(forwarder, "chat_directory", Json($"{{\"repositoryId\":\"repo\",\"ifNoneMatch\":\"{etag}\"}}"), CancellationToken.None);
        Assert.Equal("b", changed.Data!.Value.GetProperty("items")[0].GetProperty("label").GetString());
        Assert.NotEqual(etag, changed.Data.Value.GetProperty("etag").GetString());
    }

    [Fact]
    public async Task Mutations_and_failures_are_never_conditional()
    {
        var hub = new FixedReply { Data = "{\"messageId\":\"m\"}" };
        using var client = new HttpClient(hub);
        var forwarder = new RelayForwarder(client, new AdapterOptions("http://127.0.0.1:5271", "client-secret"));
        var sent = await ConditionalRead.ForwardAsync(forwarder, "send_message", Json("{\"repositoryId\":\"repo\",\"ifNoneMatch\":\"x\"}"), CancellationToken.None);
        Assert.False(sent.Data!.Value.TryGetProperty("etag", out _));
        Assert.Contains("ifNoneMatch", hub.LastBody);
        Assert.True(ConditionalRead.Applies("task_get") && ConditionalRead.Applies("chat_directory") && !ConditionalRead.Applies("check_inbox") && !ConditionalRead.Applies("send_message"));
    }

    [Fact]
    public void Conditional_reads_are_advertised_only_where_they_apply()
    {
        var tools = Tools(chatOnly: false).ToDictionary(tool => tool.ProtocolTool.Name, tool => tool.ProtocolTool.InputSchema.GetProperty("properties"));
        Assert.True(tools["task_get"].TryGetProperty("ifNoneMatch", out _));
        Assert.True(tools["chat_directory"].TryGetProperty("ifNoneMatch", out _));
        Assert.False(tools["send_message"].TryGetProperty("ifNoneMatch", out _));
        Assert.False(tools["check_inbox"].TryGetProperty("ifNoneMatch", out _));
    }

    [Fact]
    public void Task_update_acknowledges_compactly_however_long_the_conversation()
    {
        var state = new CoCern.Domain.StateEnvelope<CoCern.Domain.CoordinationDocument>();
        var caller = new CoCern.Domain.RelayCaller("alice", true, ["repo"], true);
        CoCern.Domain.RelayReply Update(long expected, string request) => CoCern.Domain.CoordinationStateMachine.Apply(state, "board", "key", caller, "task_update",
            new System.Text.Json.Nodes.JsonObject { ["repositoryId"] = "repo", ["taskId"] = "t", ["requestId"] = request, ["expectedVersion"] = expected, ["message"] = new System.Text.Json.Nodes.JsonObject { ["text"] = new string('m', 400) } },
            DateTimeOffset.Parse("2026-10-30T00:00:00Z"));
        var sizes = new List<int>();
        for (var i = 0; i < 20; i++)
        {
            var reply = Update(i, "r" + i);
            Assert.True(reply.Ok, reply.Code);
            Assert.Equal(i + 1, reply.Version);
            var data = JsonDocument.Parse(reply.DataJson).RootElement;
            Assert.Equal("t", data.GetProperty("taskId").GetString());
            Assert.Equal(i + 1, data.GetProperty("version").GetInt64());
            Assert.True(data.TryGetProperty("changeRevision", out _));
            sizes.Add(reply.DataJson.Length);
        }
        // The reply does not echo the task back, so it does not grow with the conversation it just extended.
        Assert.True(sizes.Max() < 300, $"task_update replies reached {sizes.Max()} characters.");
        Assert.InRange(sizes[^1] - sizes[1], 0, 4);

        // The task is still one task_get away.
        var task = CoCern.Domain.CoordinationStateMachine.Apply(state, "board", "key", caller, "task_get", new System.Text.Json.Nodes.JsonObject { ["repositoryId"] = "repo", ["taskId"] = "t" }, DateTimeOffset.Parse("2026-10-30T00:00:00Z"));
        Assert.Equal(20, JsonDocument.Parse(task.DataJson).RootElement.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public void Whole_task_read_keeps_only_the_latest_conversation_and_reports()
    {
        var state = new CoCern.Domain.StateEnvelope<CoCern.Domain.CoordinationDocument>();
        var caller = new CoCern.Domain.RelayCaller("alice", true, ["repo"], true);
        var now = DateTimeOffset.Parse("2026-10-30T00:00:00Z");
        for (var i = 0; i < 45; i++)
            Assert.True(CoCern.Domain.CoordinationStateMachine.Apply(state, "board", "key", caller, "task_update",
                new System.Text.Json.Nodes.JsonObject { ["repositoryId"] = "repo", ["taskId"] = "t", ["requestId"] = "r" + i, ["expectedVersion"] = i, ["message"] = new System.Text.Json.Nodes.JsonObject { ["text"] = "update " + i } }, now).Ok);
        CoCern.Domain.RelayReply Get(System.Text.Json.Nodes.JsonObject args) { args["repositoryId"] = "repo"; args["taskId"] = "t"; return CoCern.Domain.CoordinationStateMachine.Apply(state, "board", "key", caller, "task_get", args, now); }

        var whole = JsonDocument.Parse(Get([]).DataJson).RootElement;
        var messages = whole.GetProperty("messages");
        Assert.Equal(CoCern.Contracts.RelayLimits.RecentTaskEntries, messages.GetArrayLength());
        Assert.Contains("update 44", messages[messages.GetArrayLength() - 1].GetRawText());
        Assert.DoesNotContain("\"update 0\"", whole.GetRawText());
        Assert.Equal(45, whole.GetProperty("messagesTotal").GetInt32());

        // Older entries stay readable through the paged section.
        var first = JsonDocument.Parse(Get(new() { ["section"] = "messages", ["limit"] = 5 }).DataJson).RootElement;
        Assert.Contains("update 0", first.GetProperty("items")[0].GetRawText());
        Assert.Equal(45, first.GetProperty("total").GetInt32());
    }

    [Fact]
    public void Superseding_message_replaces_the_senders_earlier_unread_one()
    {
        var state = new CoCern.Domain.StateEnvelope<CoCern.Domain.CoordinationDocument>();
        var alice = new CoCern.Domain.RelayCaller("alice", true, ["repo"], true);
        var now = DateTimeOffset.Parse("2026-10-30T00:00:00Z");
        CoCern.Domain.RelayReply Call(string op, System.Text.Json.Nodes.JsonObject args) { args["repositoryId"] = "repo"; return CoCern.Domain.CoordinationStateMachine.Apply(state, "session", "key", alice, op, args, now); }
        System.Text.Json.Nodes.JsonObject Message(string id, string sender, bool supersede) => new() { ["messageId"] = id, ["senderSessionId"] = sender, ["text"] = "status?", ["supersede"] = supersede };
        Assert.True(Call("__open", new() { ["principalId"] = "alice", ["sessionToken"] = "secret", ["sessionId"] = "s1" }).Ok);

        Assert.True(Call("__deliver", Message("h1", "heartbeat", true)).Ok);
        Assert.True(Call("__deliver", Message("q1", "heartbeat", false)).Ok);
        Assert.True(Call("__deliver", Message("o1", "other", true)).Ok);
        Assert.True(Call("__deliver", Message("h2", "heartbeat", true)).Ok);
        Assert.True(Call("__deliver", Message("h3", "heartbeat", true)).Ok);

        string[] Inbox() => JsonDocument.Parse(Call("check_inbox", new() { ["sessionToken"] = "secret" }).DataJson).RootElement.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("messageId").GetString()!).Order().ToArray();
        // Only the sender's latest superseding message is kept; its ordinary message and another sender's are untouched.
        Assert.Equal(["h3", "o1", "q1"], Inbox());
        // A late redelivery of a superseded message does not bring it back.
        Assert.True(Call("__deliver", Message("h1", "heartbeat", true)).Ok);
        Assert.Equal(["h3", "o1", "q1"], Inbox());
    }

    [Fact]
    public void Quiet_send_is_delivered_with_wake_false()
    {
        var state = new CoCern.Domain.StateEnvelope<CoCern.Domain.CoordinationDocument>();
        var alice = new CoCern.Domain.RelayCaller("alice", true, ["repo"], true);
        var now = DateTimeOffset.Parse("2026-10-30T00:00:00Z");
        CoCern.Domain.RelayReply Call(string op, System.Text.Json.Nodes.JsonObject args) { args["repositoryId"] = "repo"; return CoCern.Domain.CoordinationStateMachine.Apply(state, "session", "key", alice, op, args, now); }
        Assert.True(Call("__open", new() { ["principalId"] = "alice", ["sessionToken"] = "secret", ["sessionId"] = "s1" }).Ok);
        Assert.True(Call("send_message", new() { ["sessionToken"] = "secret", ["requestId"] = "q", ["recipientSessionId"] = "bob", ["text"] = "fyi", ["wake"] = false }).Ok);
        Assert.True(Call("send_message", new() { ["sessionToken"] = "secret", ["requestId"] = "l", ["recipientSessionId"] = "bob", ["text"] = "act" }).Ok);
        var delivered = state.Effects.Select(effect => JsonDocument.Parse(effect.PayloadJson).RootElement).ToArray();
        Assert.False(delivered[0].GetProperty("wake").GetBoolean());
        Assert.False(delivered[1].TryGetProperty("wake", out _));

        Assert.True(RelayToolCatalog.TryGet("send_message", out var tool));
        Assert.Equal("boolean", RelayToolCatalog.ForwardingSchema(tool).GetProperty("properties").GetProperty("wake").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData(null, 120)]
    [InlineData(1800, 1800)]
    [InlineData(3600, 1800)]
    [InlineData(0, 1)]
    public void Leases_may_last_thirty_minutes_so_long_turns_renew_rarely(int? requested, int granted)
    {
        var state = new CoCern.Domain.StateEnvelope<CoCern.Domain.CoordinationDocument>();
        var caller = new CoCern.Domain.RelayCaller("alice", false, ["repo"], true);
        var now = DateTimeOffset.Parse("2026-10-30T00:00:00Z");
        var args = new System.Text.Json.Nodes.JsonObject { ["repositoryId"] = "repo", ["sessionId"] = "s", ["requestId"] = "lease" };
        if (requested is { } seconds) args["leaseSeconds"] = seconds;
        var reply = CoCern.Domain.CoordinationStateMachine.Apply(state, "lock", "key", caller, "claim_acquire", args, now);
        Assert.True(reply.Ok, reply.Code);
        Assert.Equal(now.AddSeconds(granted), DateTimeOffset.Parse(JsonDocument.Parse(reply.DataJson).RootElement.GetProperty("leaseUntil").GetString()!));
        Assert.Equal(1800, RelayLimits.MaxLeaseSeconds);
    }

    [Fact]
    public void Send_message_advertises_supersede()
    {
        Assert.True(RelayToolCatalog.TryGet("send_message", out var tool));
        Assert.Equal("boolean", RelayToolCatalog.ForwardingSchema(tool).GetProperty("properties").GetProperty("supersede").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Every_call_records_estimated_request_and_reply_tokens_with_tool_session_and_role()
    {
        await using var host = new RelayHostFixture();
        using var client = host.AuthorizedClient();
        var session = Data(await host.Call(client, "session_open", new { repositoryId = "repo", requestId = "tokens-open", role = "Worker" }));
        var handle = new { repositoryId = "repo", sessionId = session.GetProperty("sessionId").GetString(), sessionToken = session.GetProperty("sessionToken").GetString() };
        Data(await host.Call(client, "task_update", new { handle.repositoryId, handle.sessionId, handle.sessionToken, requestId = "tokens-create", taskId = "T1", expectedVersion = 0, notes = "first note" }));
        var inbox = await host.Call(client, "check_inbox", handle);
        var store = host.Services.GetRequiredService<SqliteStore>();
        await store.FlushActivityAsync();
        var rows = Rows(store, "SELECT tool, session_id, role, request_tokens, reply_tokens FROM activity_log WHERE kind = 'call' ORDER BY id");
        Assert.Equal(["session_open", "task_update", "check_inbox"], rows.Select(row => (string)row[0]!));
        Assert.All(rows, row =>
        {
            Assert.Equal(handle.sessionId, row[1]);
            Assert.Equal("worker", row[2]);
            Assert.True((long)row[3]! > 0 && (long)row[4]! > 0);
        });
        // The reply count is the shared counter's count of the reply as sent.
        var counter = host.Services.GetRequiredService<ITokenCounter>();
        Assert.Equal("o200k_base", counter.Encoding);
        Assert.Equal(counter.Count(inbox.GetProperty("dataJson").GetString()!), (long)rows[2][4]!);
        // Counting is measure-only: no reply says it was compacted.
        Assert.DoesNotContain("compacted", inbox.GetProperty("dataJson").GetString()!);

        // With Relay:Tokens:Enabled off nothing is counted and nothing is recorded as over budget.
        await using var off = new RelayHostFixture(settings: new Dictionary<string, string?> { ["Relay:Tokens:Enabled"] = "false", ["Relay:Clients:0:IsAdministrator"] = "true" });
        using var offClient = off.AuthorizedClient();
        var quiet = Data(await off.Call(offClient, "session_open", new { repositoryId = "repo", requestId = "tokens-off", role = "worker" }));
        await off.Call(offClient, "check_inbox", new { repositoryId = "repo", sessionId = quiet.GetProperty("sessionId").GetString(), sessionToken = quiet.GetProperty("sessionToken").GetString() });
        Assert.False(Data(await off.Call(offClient, "get_status", new { repositoryId = "repo" })).TryGetProperty("toolTokens", out _));
        var offStore = off.Services.GetRequiredService<SqliteStore>();
        await offStore.FlushActivityAsync();
        Assert.Equal(3, Rows(offStore, "SELECT id FROM activity_log WHERE kind = 'call'").Count);
        Assert.Empty(Rows(offStore, "SELECT id FROM activity_log WHERE request_tokens IS NOT NULL OR reply_tokens IS NOT NULL OR kind = 'budget'"));
    }

    [Fact]
    public async Task Administrators_see_tool_definition_token_totals_per_role_in_get_status()
    {
        await using var host = new RelayHostFixture(settings: new Dictionary<string, string?> { ["Relay:Clients:0:IsAdministrator"] = "true" });
        using var client = host.AuthorizedClient();
        // The totals appear once the startup warm-up has counted them; no status read waits for the tokenizer.
        JsonElement totals = default;
        for (var retry = 0; retry < 100 && totals.ValueKind != JsonValueKind.Object; retry++)
        {
            if (Data(await host.Call(client, "get_status", new { repositoryId = "repo" })).TryGetProperty("toolTokens", out var found)) totals = found;
            else await Task.Delay(100);
        }
        var counter = new O200kTokenCounter();
        int Expected(bool administrator) => RelayToolCatalog.ForRole(administrator).Sum(tool => counter.Count(tool.Description) + counter.Count(RelayToolCatalog.ForwardingSchema(tool).GetRawText()));
        Assert.Equal(Expected(false), totals.GetProperty("worker").GetInt32());
        Assert.Equal(Expected(true), totals.GetProperty("administrator").GetInt32());
        Assert.True(totals.GetProperty("administrator").GetInt32() > totals.GetProperty("worker").GetInt32());
        Assert.True(totals.GetProperty("estimated").GetBoolean());
        Assert.Equal("o200k_base", totals.GetProperty("encoding").GetString());
        int Characters(bool administrator) => RelayToolCatalog.ForRole(administrator).Sum(tool => tool.Description.Length + RelayToolCatalog.ForwardingSchema(tool).GetRawText().Length);
        output.WriteLine($"Tool definitions: worker {Characters(false)} characters, {Expected(false)} estimated tokens; administrator {Characters(true)} characters, {Expected(true)} estimated tokens.");

        // A worker's status is unchanged.
        await using var workerHost = new RelayHostFixture();
        using var workerClient = workerHost.AuthorizedClient();
        Assert.False(Data(await workerHost.Call(workerClient, "get_status", new { repositoryId = "repo" })).TryGetProperty("toolTokens", out _));
    }

    [Fact]
    public async Task Measure_only_tools_are_counted_but_sent_unchanged_and_enforced_tools_stay_under_budget()
    {
        async Task<(string Reply, string[] Budget)> Search(Dictionary<string, string?>? settings)
        {
            await using var host = new RelayHostFixture(settings: settings);
            using var client = host.AuthorizedClient();
            var session = Data(await host.Call(client, "session_open", new { repositoryId = "repo", requestId = "budget-open" }));
            var (id, token) = (session.GetProperty("sessionId").GetString(), session.GetProperty("sessionToken").GetString());
            for (var i = 0; i < 10; i++)
                Data(await host.Call(client, "task_update", new { repositoryId = "repo", requestId = $"budget-{i}", sessionId = id, sessionToken = token, taskId = $"deploy-{i}", expectedVersion = 0, notes = $"deploy rollback runbook step {i} for the staging bench" }));
            var reply = Data(await host.Call(client, "search", new { repositoryId = "repo", sessionId = id, sessionToken = token, query = "deploy rollback runbook", limit = 10 }));
            var store = host.Services.GetRequiredService<SqliteStore>();
            await store.FlushActivityAsync();
            return (reply.GetRawText(), Rows(store, "SELECT code FROM activity_log WHERE kind = 'budget' AND tool = 'search'").Select(row => (string)row[0]!).ToArray());
        }

        // Every tool is enforced by default, and its budget sits at what its byte cap allows, so a normal reply is sent byte for byte as
        // without counting, and is not even over its budget.
        var counter = new O200kTokenCounter();
        var uncounted = await Search(new() { ["Relay:Tokens:Enabled"] = "false" });
        var enforced = await Search(null);
        Assert.True(new TokenOptions().Enforces(RelayTools.Search));
        Assert.True(counter.Count(enforced.Reply) < RelayLimits.SearchReplyTokens, counter.Count(enforced.Reply).ToString());
        Assert.Equal(uncounted.Reply, enforced.Reply);
        Assert.Empty(enforced.Budget);
        Assert.Empty(uncounted.Budget);

        // A page of token-dense messages ("7 3 9 1 4" is one token per byte) is over its budget while still under the byte cap.
        async Task<(JsonElement Page, string[] Budget, Func<long, Task<JsonElement>> Next)> Inbox(RelayHostFixture host)
        {
            var client = host.AuthorizedClient();
            var reader = Data(await host.Call(client, "session_open", new { repositoryId = "repo", requestId = "budget-reader" }));
            var writer = Data(await host.Call(client, "session_open", new { repositoryId = "repo", requestId = "budget-writer" }));
            for (var i = 0; i < 30; i++)
                Data(await host.Call(client, "send_message", new { repositoryId = "repo", requestId = $"budget-mail-{i}", sessionId = writer.GetProperty("sessionId").GetString(), sessionToken = writer.GetProperty("sessionToken").GetString(),
                    recipientSessionId = reader.GetProperty("sessionId").GetString(), text = $"Message {i}: " + string.Concat(Enumerable.Repeat("7 3 9 1 4 ", 35)) }));
            var inbox = new { repositoryId = "repo", sessionId = reader.GetProperty("sessionId").GetString(), sessionToken = reader.GetProperty("sessionToken").GetString(), attention = false, limit = 50 };
            JsonElement page = default;
            for (var retry = 0; retry < 50; retry++)
            {
                page = Data(await host.Call(client, "check_inbox", inbox));
                if (page.GetProperty("total").GetInt32() == 30) break;
                await Task.Delay(100);
            }
            var store = host.Services.GetRequiredService<SqliteStore>();
            await store.FlushActivityAsync();
            return (page, Rows(store, "SELECT code FROM activity_log WHERE kind = 'budget' AND tool = 'check_inbox'").Select(row => (string)row[0]!).ToArray(),
                cursor => host.Call(client, "check_inbox", new { inbox.repositoryId, inbox.sessionId, inbox.sessionToken, inbox.attention, inbox.limit, cursor }).ContinueWith(task => Data(task.Result)));
        }

        // Measure-only (Relay:Tokens:Enforce false): the over-budget page is counted as over and sent whole.
        await using (var measureOnly = new RelayHostFixture(settings: new Dictionary<string, string?> { ["Relay:Tokens:Enforce"] = "false" }))
        {
            var (page, budget, _) = await Inbox(measureOnly);
            Assert.True(counter.Count(page.GetRawText()) > RelayLimits.InboxReplyTokens, counter.Count(page.GetRawText()).ToString());
            Assert.True(Encoding.UTF8.GetByteCount(page.GetRawText()) < RelayLimits.AggregateReplyBytes);
            Assert.Equal(30, page.GetProperty("items").GetArrayLength());
            Assert.False(page.TryGetProperty("compacted", out _));
            // Every read of the page (the test polls until all 30 messages arrived) is counted as over, and none is compacted.
            Assert.NotEmpty(budget);
            Assert.All(budget, code => Assert.Equal("over", code));
        }

        // Enforced (the default): the same page is compacted under its budget, and its cursor continues where it stopped.
        await using var host = new RelayHostFixture();
        var (compacted, steps, next) = await Inbox(host);
        Assert.True(counter.Count(compacted.GetRawText()) <= RelayLimits.InboxReplyTokens, counter.Count(compacted.GetRawText()).ToString());
        Assert.True(compacted.GetProperty("compacted").GetBoolean());
        Assert.Equal(2, compacted.GetProperty("compactStep").GetInt32());
        // The whole inbox fit one page before compaction, so "more" is every message left out.
        var kept = compacted.GetProperty("items").GetArrayLength();
        Assert.Equal(30 - kept, compacted.GetProperty("more").GetInt32());
        Assert.Equal(kept, compacted.GetProperty("nextCursor").GetInt32());
        Assert.StartsWith($"Message {kept}:", (await next(kept)).GetProperty("items")[0].GetProperty("text").GetString());
        Assert.Contains("step2", steps);
    }

    [Fact]
    public void Queued_text_past_its_cap_drops_only_the_token_counts()
    {
        var budget = new TokenTextBudget(100);
        Assert.True(budget.TryHold(new string('a', 40), new string('b', 40)));
        // Both texts or neither: a call that would pass the cap attaches none, and its event is recorded without counts.
        Assert.False(budget.TryHold(new string('c', 15), new string('d', 15)));
        Assert.Equal(80, budget.Held);
        Assert.True(budget.TryHold(null, new string('e', 20)));
        budget.Release(new ActivityEvent { RequestText = new string('a', 40), ReplyText = new string('b', 40) });
        Assert.Equal(20, budget.Held);
        Assert.True(budget.TryHold(new string('c', 15), new string('d', 15)));
        // An event without texts is written as it is, with no counts.
        var plain = new ActivityEvent { Kind = ActivityEvent.Call, Tool = RelayTools.TaskGet, Ok = true };
        Assert.Same(plain, Assert.Single(TokenMeter.Measure(plain, new O200kTokenCounter())));
        Assert.Equal(RelayLimits.MaxQueuedTokenTextChars, 8_000_000);
    }

    [Fact]
    public async Task Bad_token_settings_fail_clearly()
    {
        foreach (var (tool, named) in new[] { ("no_such_tool", "unknown tool"), ("doc_search", "own token budget") })
        {
            await using var host = new RelayHostFixture(settings: new Dictionary<string, string?> { ["Relay:Tokens:EnforceTools:0"] = tool });
            var failure = Assert.ThrowsAny<Exception>(() => host.Services.GetRequiredService<TokenOptions>());
            Assert.Contains("Relay:Tokens:EnforceTools", failure.ToString());
            Assert.Contains(named, failure.ToString());
        }
    }

    /// <summary>
    /// Counting runs in the activity writer, so a call only hands over its request and reply text. The median added latency per call,
    /// measured in process with counting on and off, must stay under a millisecond. The writer's own cost of counting a typical call and a
    /// large reply is measured and reported too.
    /// </summary>
    [Fact]
    public async Task Token_counting_adds_well_under_a_millisecond_per_call()
    {
        static double Median(double[] samples) { Array.Sort(samples); return samples[samples.Length / 2]; }
        async Task<double> CallMedian(Dictionary<string, string?>? settings)
        {
            await using var host = new RelayHostFixture(settings: settings);
            using var client = host.AuthorizedClient();
            var application = host.Services.GetRequiredService<IRelayApplication>();
            var caller = new RelayCaller("test-user", false, ["repo"]);
            var opened = await application.ExecuteAsync(caller, RelayTools.SessionOpen, JsonSerializer.SerializeToElement(new { repositoryId = "repo", requestId = "latency-open", role = "worker" }));
            var session = JsonDocument.Parse(opened.DataJson).RootElement;
            var read = JsonSerializer.SerializeToElement(new { repositoryId = "repo", sessionId = session.GetProperty("sessionId").GetString(), sessionToken = session.GetProperty("sessionToken").GetString(), attention = false });
            for (var i = 0; i < 100; i++) await application.ExecuteAsync(caller, RelayTools.CheckInbox, read);
            var samples = new double[400];
            for (var i = 0; i < samples.Length; i++)
            {
                var started = Stopwatch.GetTimestamp();
                Assert.True((await application.ExecuteAsync(caller, RelayTools.CheckInbox, read)).Ok);
                samples[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
            return Median(samples);
        }
        // A first host warms up the JIT and the shared tokenizer, so neither measured host pays for it.
        await CallMedian(null);
        var off = await CallMedian(new() { ["Relay:Tokens:Enabled"] = "false" });
        var on = await CallMedian(null);
        output.WriteLine($"Median in-process call: {on:0.000} ms with counting, {off:0.000} ms without; added {on - off:0.000} ms.");
        Assert.True(on - off < 1.0, $"{on} ms with counting, {off} ms without");

        // The writer's cost per event, off the call's path.
        var counter = new O200kTokenCounter();
        var request = JsonSerializer.Serialize(new
        {
            repositoryId = "cocern", sessionId = "s-" + new string('a', 32), sessionToken = new string('b', 43), requestId = "milestone-42", taskId = "T12", expectedVersion = 7,
            notes = "Built the token counter and the compactor; tests pass on Linux.", workflow = new { state = "ready" }, includeNextWork = true
        });
        var reply = Replies.Page(Enumerable.Range(1, 12).Select(i => (JsonNode)new JsonObject { ["taskId"] = $"T{i}", ["title"] = $"Task {i}: wire the dashboard view for budget events", ["state"] = "ready", ["version"] = i }),
            new JsonObject { ["limit"] = 12 }).DataJson;
        var large = Replies.Ok(new { items = Enumerable.Range(1, 260).Select(i => new { messageId = $"m{i}", text = "Status: build " + i + " passed on the bench; next is the review." }) }).DataJson;
        double Counting(string replyText)
        {
            var call = new ActivityEvent { Kind = ActivityEvent.Call, RepositoryId = "cocern", Tool = RelayTools.TaskGet, Ok = true, RequestText = request, ReplyText = replyText };
            for (var i = 0; i < 50; i++) TokenMeter.Measure(call, counter).ToList();
            var samples = new double[300];
            for (var i = 0; i < samples.Length; i++)
            {
                var started = Stopwatch.GetTimestamp();
                TokenMeter.Measure(call, counter).ToList();
                samples[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
            return Median(samples);
        }
        var typical = Counting(reply);
        var worst = Counting(large);
        output.WriteLine($"Writer, per event (median): {typical:0.000} ms for a {Encoding.UTF8.GetByteCount(request)}-byte request and {Encoding.UTF8.GetByteCount(reply)}-byte reply; {worst:0.000} ms with a {Encoding.UTF8.GetByteCount(large)}-byte reply.");
        Assert.True(typical < 1.0, $"{typical} ms");
        // A reply over task_get's budget (at its byte cap) is counted as over: token-dense text is one token per byte.
        var over = Replies.Ok(new { items = Enumerable.Range(1, 60).Select(i => new { messageId = $"m{i}", text = string.Concat(Enumerable.Repeat("7 3 9 1 4 ", 25)) }) }).DataJson;
        Assert.True(counter.Count(over) > RelayLimits.TaskReadReplyTokens);
        var measured = TokenMeter.Measure(new ActivityEvent { Kind = ActivityEvent.Call, Tool = RelayTools.TaskGet, Ok = true, RequestText = request, ReplyText = over }, counter).ToList();
        Assert.Equal((counter.Count(request), counter.Count(over)), (measured[0].RequestTokens, measured[0].ReplyTokens));
        Assert.Null(measured[0].ReplyText);
        Assert.Equal("over", Assert.Single(measured, row => row.Kind == ActivityEvent.Budget).Code);
    }

    [Fact]
    public async Task Oversized_pieces_of_dense_text_stay_within_the_inbox_token_budget_and_rejoin_exactly()
    {
        await using var host = new RelayHostFixture();
        var client = host.AuthorizedClient();
        var reader = Data(await host.Call(client, "session_open", new { repositoryId = "repo", requestId = "dense-reader" }));
        var (sessionId, sessionToken) = (reader.GetProperty("sessionId").GetString()!, reader.GetProperty("sessionToken").GetString()!);
        // CJK, emoji (surrogate pairs) and accents: each escapes to several bytes and tokens. Stored as before sends were measured.
        var text = string.Concat(Enumerable.Repeat("中文字符测试😀é ", 450));
        var grains = host.Services.GetRequiredService<Orleans.IGrainFactory>();
        var delivered = await grains.GetGrain<ISessionGrain>(sessionId).ExecuteAsync(new RelayCaller("relay-dispatcher", false, ["repo"], true), Operations.Deliver,
            new JsonObject { ["repositoryId"] = "repo", ["messageId"] = "dense", ["text"] = text }.ToJsonString());
        Assert.True(delivered.Ok, delivered.Code);

        var counter = new O200kTokenCounter();
        var json = new StringBuilder();
        long? partOffset = 0;
        var pieces = 0;
        while (partOffset is { } at)
        {
            var reply = await host.Call(client, "check_inbox", at == 0
                ? new { repositoryId = "repo", sessionId, sessionToken, attention = false, limit = 1, cursor = 0 } as object
                : new { repositoryId = "repo", sessionId, sessionToken, attention = false, limit = 1, cursor = 0, partOffset = at });
            var dataJson = reply.GetProperty("dataJson").GetString()!;
            Assert.True(counter.Count(dataJson) <= RelayLimits.InboxReplyTokens, counter.Count(dataJson).ToString());
            var piece = Data(reply).GetProperty("items")[0];
            Assert.False(Data(reply).TryGetProperty("compacted", out _));
            Assert.Equal("dense", piece.GetProperty("messageId").GetString());
            Assert.Equal(at, piece.GetProperty("partOffset").GetInt64());
            json.Append(piece.GetProperty("part").GetString());
            partOffset = piece.GetProperty("nextPartOffset").ValueKind == JsonValueKind.Null ? null : piece.GetProperty("nextPartOffset").GetInt64();
            pieces++;
        }
        Assert.True(pieces > 1, pieces.ToString());
        Assert.Equal(text, JsonNode.Parse(json.ToString())!["text"]!.GetValue<string>());
    }

    [Fact]
    public void The_claude_waiter_flags_an_oversized_piece_and_leaves_it_unseen()
    {
        var page = JsonDocument.Parse("""
            {"items":[{"messageId":"small","text":"hello"},{"messageId":"big","truncated":true,"part":"{\"text\":\"aaaa","partOffset":0,"nextPartOffset":12}],"total":2}
            """).RootElement;
        var shown = new List<string>();
        var printed = CoCern.Adapter.Node.ClaudeWaiter.FormatInbox(page, "repo", shown)!;
        Assert.Contains("big: (oversized; call check_inbox with partOffset to read it)", printed);
        Assert.Equal(["small"], shown);
    }

    private static JsonElement Data(JsonElement reply)
    {
        Assert.True(reply.GetProperty("ok").GetBoolean(), reply.ToString());
        return JsonDocument.Parse(reply.GetProperty("dataJson").GetString()!).RootElement.Clone();
    }

    private static List<object?[]> Rows(SqliteStore store, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read()) rows.Add(Enumerable.Range(0, reader.FieldCount).Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index)).ToArray());
        return rows;
    }

    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private sealed class FixedReply : HttpMessageHandler
    {
        public string Data = "{}";
        public string LastBody = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { ok = true, code = "Ok", dataJson = Data, version = 4 }), System.Text.Encoding.UTF8, "application/json") };
        }
    }

    private sealed class CountingInbox : HttpMessageHandler
    {
        public int Calls;
        public int MailOnCall;
        public string LastBody = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            var mail = ++Calls >= MailOnCall;
            var data = JsonSerializer.Serialize(new { items = mail ? new[] { new { messageId = "m1", repositoryId = "repo", text = "hi" } } : [], nextCursor = (int?)null, truncated = false, total = mail ? 1 : 0 });
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { ok = true, code = "Ok", dataJson = data, version = 0 }), System.Text.Encoding.UTF8, "application/json") };
        }
    }

    private static IEnumerable<ModelContextProtocol.Server.McpServerTool> Tools(bool chatOnly)
    {
        var client = new HttpClient(new NoNetwork());
        return CoordinationTools.Create(new RelayForwarder(client, new AdapterOptions("http://127.0.0.1:5271", "client-secret")), false, chatOnly: chatOnly).ToArray();
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new Xunit.Sdk.XunitException("Unexpected network request");
    }
}
