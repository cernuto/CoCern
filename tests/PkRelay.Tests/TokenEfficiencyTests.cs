using System.Text.Json;
using PkRelay.Adapter;
using PkRelay.Contracts;
using Xunit;

namespace PkRelay.Tests;

/// <summary>Tool definitions sit in every turn's context, so the adapter keeps them as small as the chosen surface allows.</summary>
public sealed class TokenEfficiencyTests
{
    private static readonly string[] ChatTools = ["session_open", "send_message", "check_inbox", "ack", "chat_directory"];

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
    public void Profile_comes_from_PKRELAY_PROFILE(string? profile, bool chatOnly)
    {
        var variables = new Dictionary<string, string?> { ["PKRELAY_TOKEN"] = "client-secret", ["PKRELAY_PROFILE"] = profile };
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
        // The adapter-only option never reaches the hub.
        Assert.DoesNotContain("waitSeconds", hub.LastBody);
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
        Assert.InRange(hub.Calls, 2, 15);

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
        var state = new PkRelay.Domain.StateEnvelope<PkRelay.Domain.CoordinationDocument>();
        var caller = new PkRelay.Domain.RelayCaller("alice", true, ["repo"], true);
        PkRelay.Domain.RelayReply Update(long expected, string request) => PkRelay.Domain.CoordinationStateMachine.Apply(state, "board", "key", caller, "task_update",
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
        var task = PkRelay.Domain.CoordinationStateMachine.Apply(state, "board", "key", caller, "task_get", new System.Text.Json.Nodes.JsonObject { ["repositoryId"] = "repo", ["taskId"] = "t" }, DateTimeOffset.Parse("2026-10-30T00:00:00Z"));
        Assert.Equal(20, JsonDocument.Parse(task.DataJson).RootElement.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public void Whole_task_read_keeps_only_the_latest_conversation_and_reports()
    {
        var state = new PkRelay.Domain.StateEnvelope<PkRelay.Domain.CoordinationDocument>();
        var caller = new PkRelay.Domain.RelayCaller("alice", true, ["repo"], true);
        var now = DateTimeOffset.Parse("2026-10-30T00:00:00Z");
        for (var i = 0; i < 45; i++)
            Assert.True(PkRelay.Domain.CoordinationStateMachine.Apply(state, "board", "key", caller, "task_update",
                new System.Text.Json.Nodes.JsonObject { ["repositoryId"] = "repo", ["taskId"] = "t", ["requestId"] = "r" + i, ["expectedVersion"] = i, ["message"] = new System.Text.Json.Nodes.JsonObject { ["text"] = "update " + i } }, now).Ok);
        PkRelay.Domain.RelayReply Get(System.Text.Json.Nodes.JsonObject args) { args["repositoryId"] = "repo"; args["taskId"] = "t"; return PkRelay.Domain.CoordinationStateMachine.Apply(state, "board", "key", caller, "task_get", args, now); }

        var whole = JsonDocument.Parse(Get([]).DataJson).RootElement;
        var messages = whole.GetProperty("messages");
        Assert.Equal(PkRelay.Contracts.RelayLimits.RecentTaskEntries, messages.GetArrayLength());
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
        var state = new PkRelay.Domain.StateEnvelope<PkRelay.Domain.CoordinationDocument>();
        var alice = new PkRelay.Domain.RelayCaller("alice", true, ["repo"], true);
        var now = DateTimeOffset.Parse("2026-10-30T00:00:00Z");
        PkRelay.Domain.RelayReply Call(string op, System.Text.Json.Nodes.JsonObject args) { args["repositoryId"] = "repo"; return PkRelay.Domain.CoordinationStateMachine.Apply(state, "session", "key", alice, op, args, now); }
        System.Text.Json.Nodes.JsonObject Message(string id, string sender, bool supersede) => new() { ["messageId"] = id, ["senderSessionId"] = sender, ["text"] = "status?", ["supersede"] = supersede };
        Assert.True(Call("__open", new() { ["principalId"] = "alice", ["sessionToken"] = "secret" }).Ok);

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
    public void Send_message_advertises_supersede()
    {
        Assert.True(RelayToolCatalog.TryGet("send_message", out var tool));
        Assert.Equal("boolean", RelayToolCatalog.ForwardingSchema(tool).GetProperty("properties").GetProperty("supersede").GetProperty("type").GetString());
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
