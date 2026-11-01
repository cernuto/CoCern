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

    [Fact]
    public void Standing_tools_are_discovered_by_role()
    {
        var administrator = PkRelay.Adapter.CoordinationTools.NamesForRole(true);
        var worker = PkRelay.Adapter.CoordinationTools.NamesForRole(false);
        Assert.Contains(RelayTools.StandingPropose, administrator);
        Assert.Contains(RelayTools.StandingPut, administrator);
        Assert.Contains(RelayTools.StandingPropose, worker);
        Assert.DoesNotContain(RelayTools.StandingPut, worker);
        Assert.True(PkRelay.Adapter.CoordinationTools.IsAllowed(RelayTools.StandingPut, admin: true));
        Assert.False(PkRelay.Adapter.CoordinationTools.IsAllowed(RelayTools.StandingPut, admin: false));
        Assert.False(PkRelay.Adapter.CoordinationTools.IsAllowed(RelayTools.StandingPropose, admin: false, chatOnly: true));
    }

    [Fact]
    public void Analytics_query_is_discovered_only_by_administrators()
    {
        Assert.Contains(RelayTools.AnalyticsQuery, PkRelay.Adapter.CoordinationTools.NamesForRole(true));
        Assert.DoesNotContain(RelayTools.AnalyticsQuery, PkRelay.Adapter.CoordinationTools.NamesForRole(false));
        Assert.True(PkRelay.Adapter.CoordinationTools.IsAllowed(RelayTools.AnalyticsQuery, admin: true));
        Assert.False(PkRelay.Adapter.CoordinationTools.IsAllowed(RelayTools.AnalyticsQuery, admin: false));
        Assert.False(PkRelay.Adapter.CoordinationTools.IsAllowed(RelayTools.AnalyticsQuery, admin: true, chatOnly: true));
    }

    [Fact]
    public void Search_is_discovered_by_every_session_role_but_not_the_chat_profile()
    {
        Assert.Contains(RelayTools.Search, PkRelay.Adapter.CoordinationTools.NamesForRole(true));
        Assert.Contains(RelayTools.Search, PkRelay.Adapter.CoordinationTools.NamesForRole(false));
        Assert.True(PkRelay.Adapter.CoordinationTools.IsAllowed(RelayTools.Search, admin: false));
        Assert.False(PkRelay.Adapter.CoordinationTools.IsAllowed(RelayTools.Search, admin: false, chatOnly: true));
    }

    [Fact]
    public void Attention_respond_is_discovered_by_every_session_role_but_not_the_chat_profile()
    {
        Assert.Contains(RelayTools.AttentionRespond, PkRelay.Adapter.CoordinationTools.NamesForRole(true));
        Assert.Contains(RelayTools.AttentionRespond, PkRelay.Adapter.CoordinationTools.NamesForRole(false));
        Assert.True(PkRelay.Adapter.CoordinationTools.IsAllowed(RelayTools.AttentionRespond, admin: false));
        Assert.False(PkRelay.Adapter.CoordinationTools.IsAllowed(RelayTools.AttentionRespond, admin: false, chatOnly: true));
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
    [InlineData("claim_renew", "resourceId", "string")]
    [InlineData("claim_renew", "generation", "integer")]
    [InlineData("claim_renew", "taskId", "string")]
    [InlineData("claim_renew", "claimGeneration", "integer")]
    [InlineData("claim_renew", "leaseSeconds", "integer")]
    [InlineData("claim_release", "resourceId", "string")]
    [InlineData("claim_release", "generation", "integer")]
    [InlineData("claim_release", "taskId", "string")]
    [InlineData("claim_release", "claimGeneration", "integer")]
    [InlineData("claim_release", "expectedVersion", "integer")]
    [InlineData("standing_propose", "line", "object")]
    [InlineData("standing_propose", "grant", "object")]
    [InlineData("standing_put", "expectedRevision", "integer")]
    [InlineData("standing_put", "lines", "array")]
    [InlineData("standing_put", "grants", "array")]
    [InlineData("standing_put", "remove", "array")]
    [InlineData("standing_put", "approve", "array")]
    [InlineData("standing_put", "settings", "object")]
    [InlineData("search", "query", "string")]
    [InlineData("search", "kinds", "array")]
    [InlineData("attention_respond", "itemId", "string")]
    [InlineData("attention_respond", "action", "string")]
    [InlineData("attention_respond", "until", "string")]
    [InlineData("task_update", "attention", "boolean")]
    [InlineData("action_report", "attention", "boolean")]
    [InlineData("task_claim", "attention", "boolean")]
    [InlineData("analytics_query", "name", "string")]
    [InlineData("analytics_query", "params", "object")]
    [InlineData("analytics_query", "sql", "string")]
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
