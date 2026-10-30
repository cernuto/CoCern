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
