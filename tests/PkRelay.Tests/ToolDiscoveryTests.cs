using PkRelay.Contracts;
using Xunit;

namespace PkRelay.Tests;

public class ToolDiscoveryTests
{
    [Fact]
    public void Forwarding_tools_advertise_every_catalog_required_argument()
    {
        foreach (var tool in RelayToolCatalog.All)
        {
            var properties = RelayToolCatalog.ForwardingSchema(tool).GetProperty("properties");
            foreach (var field in tool.RequiredFields)
                Assert.True(properties.TryGetProperty(field, out _), $"{tool.Name} requires {field} but does not advertise it.");
        }
    }

    [Theory]
    [InlineData("session_open", "machineId", "string")]
    [InlineData("session_open", "label", "string")]
    [InlineData("send_message", "text", "string")]
    [InlineData("send_message", "recipientSessionId", "string")]
    [InlineData("send_message", "taskId", "string")]
    [InlineData("send_message", "replyTo", "string")]
    [InlineData("check_inbox", "cursor", "integer")]
    [InlineData("check_inbox", "limit", "integer")]
    [InlineData("ack", "messageId", "string")]
    [InlineData("ack", "messageIds", "array")]
    public void Forwarding_tools_advertise_arguments_needed_for_chat_coordination(string toolName, string field, string type)
    {
        Assert.True(RelayToolCatalog.TryGet(toolName, out var tool));
        var schema = RelayToolCatalog.ForwardingSchema(tool);

        Assert.True(schema.GetProperty("properties").TryGetProperty(field, out var property), $"{toolName} does not advertise {field}.");
        Assert.Equal(type, property.GetProperty("type").GetString());
        Assert.Equal(["repositoryId"], schema.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        Assert.True(schema.GetProperty("additionalProperties").GetBoolean());
    }
}
