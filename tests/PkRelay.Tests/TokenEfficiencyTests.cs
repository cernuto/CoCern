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
