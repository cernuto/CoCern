using System.Text.Json;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Host;
using Xunit;

namespace CoCern.Tests;

public sealed class ContractsTests
{
    [Theory]
    [InlineData(RelayErrorCodes.VersionConflict, 409)]
    [InlineData(RelayErrorCodes.UnresolvedAction, 409)]
    [InlineData(RelayErrorCodes.SessionDenied, 403)]
    [InlineData(RelayErrorCodes.NotFound, 404)]
    [InlineData(RelayErrorCodes.PayloadTooLarge, 413)]
    [InlineData(RelayErrorCodes.InvalidArguments, 400)]
    public void Rest_status_follows_the_shared_error_classification(string code, int status) =>
        Assert.Equal(status, ToolResults.Status(new RelayReply(false, code, "{}")));

    [Fact]
    public void Host_and_adapter_advertise_one_catalog()
    {
        Assert.Equal(RelayToolCatalog.ForRole(false).Select(tool => tool.Name), ToolResults.Tools(false).Select(tool => tool.Name));
        Assert.Equal(RelayToolCatalog.ForRole(true).Select(tool => tool.Name), CoCern.Adapter.CoordinationTools.NamesForRole(true));
        Assert.DoesNotContain(RelayTools.ControlRequest, ToolResults.Tools(false).Select(tool => tool.Name));
    }

    [Fact]
    public void Forwarding_schema_stays_compact_and_requires_only_the_repository()
    {
        RelayToolCatalog.TryGet(RelayTools.TaskClaim, out var claim);
        var strict = RelayToolCatalog.InputSchema(claim);
        var forwarding = RelayToolCatalog.ForwardingSchema(claim);
        Assert.Contains("resourceIds", strict.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(["repositoryId"], forwarding.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
        Assert.True(forwarding.GetProperty("properties").TryGetProperty("resourceIds", out _));
        Assert.False(forwarding.GetProperty("properties").TryGetProperty("boardId", out _));

        RelayToolCatalog.TryGet(RelayTools.TaskUpdate, out var update);
        Assert.True(RelayToolCatalog.ForwardingSchema(update).GetProperty("properties").TryGetProperty("workflow", out _));
    }

    [Fact]
    public void Workflow_schema_advertises_dependencies_and_resources()
    {
        RelayToolCatalog.TryGet(RelayTools.TaskUpdate, out var update);
        var strict = RelayToolCatalog.InputSchema(update).GetProperty("properties").GetProperty("workflow").GetProperty("properties");
        Assert.Equal(RelayLimits.MaxDependencies, strict.GetProperty("dependsOn").GetProperty("maxItems").GetInt32());
        Assert.Equal(RelayLimits.MaxClaimResources, strict.GetProperty("packet").GetProperty("properties").GetProperty("resources").GetProperty("maxItems").GetInt32());
        // The forwarding schema gives both fields as compact string arrays; the hub validates the limits.
        var forwarding = RelayToolCatalog.ForwardingSchema(update).GetProperty("properties").GetProperty("workflow").GetProperty("properties");
        foreach (var field in new[] { forwarding.GetProperty("dependsOn"), forwarding.GetProperty("packet").GetProperty("properties").GetProperty("resources") })
        {
            Assert.Equal("array", field.GetProperty("type").GetString());
            Assert.Equal("string", field.GetProperty("items").GetProperty("type").GetString());
        }
    }

    [Fact]
    public void Standing_tools_appear_in_hub_and_adapter_catalogs_with_matching_schemas()
    {
        using var client = new HttpClient();
        var adapter = CoCern.Adapter.CoordinationTools.Create(new CoCern.Adapter.RelayForwarder(client, new CoCern.Adapter.AdapterOptions("http://127.0.0.1:5271", "client-secret")), admin: true)
            .ToDictionary(tool => tool.ProtocolTool.Name, tool => tool.ProtocolTool.InputSchema.GetRawText());
        var hub = ToolResults.Tools(true).ToDictionary(tool => tool.Name, tool => tool.InputSchema.GetRawText());
        foreach (var name in new[] { RelayTools.StandingPropose, RelayTools.StandingPut })
        {
            Assert.True(RelayToolCatalog.TryGet(name, out var tool));
            Assert.Equal(RelayToolCatalog.ForwardingSchema(tool).GetRawText(), adapter[name]);
            Assert.Equal(RelayToolCatalog.InputSchema(tool).GetRawText(), hub[name]);
            Assert.True(tool.IsMutation);
            Assert.DoesNotContain('.', tool.Description.TrimEnd('.'));
        }
        // Only administrators see standing_put; any session may propose.
        Assert.Contains(RelayTools.StandingPropose, ToolResults.Tools(false).Select(tool => tool.Name));
        Assert.DoesNotContain(RelayTools.StandingPut, ToolResults.Tools(false).Select(tool => tool.Name));
        Assert.DoesNotContain(RelayTools.StandingPut, CoCern.Adapter.CoordinationTools.NamesForRole(false));
        RelayToolCatalog.TryGet(RelayTools.StandingPut, out var put);
        var strict = RelayToolCatalog.InputSchema(put);
        Assert.Contains("expectedRevision", strict.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(RelayLimits.MaxStandingLines, strict.GetProperty("properties").GetProperty("lines").GetProperty("maxItems").GetInt32());
        Assert.Equal(RelayLimits.MaxStandingLineLength, strict.GetProperty("properties").GetProperty("lines").GetProperty("items").GetProperty("properties").GetProperty("text").GetProperty("maxLength").GetInt32());
        var forwarding = RelayToolCatalog.ForwardingSchema(put).GetProperty("properties");
        foreach (var field in new[] { "expectedRevision", "lines", "grants", "remove", "approve", "settings" }) Assert.True(forwarding.TryGetProperty(field, out _), field);
        RelayToolCatalog.TryGet(RelayTools.StandingPropose, out var propose);
        Assert.Contains("sessionToken", RelayToolCatalog.InputSchema(propose).GetProperty("required").EnumerateArray().Select(x => x.GetString()));
        Assert.True(RelayToolCatalog.ForwardingSchema(propose).GetProperty("properties").TryGetProperty("grant", out _));
        // The envelope keeps its length bound in the strict schema; the compact forwarding copy leaves it to the hub.
        Assert.Equal(RelayLimits.MaxIdentifierLength, strict.GetProperty("properties").GetProperty("requestId").GetProperty("maxLength").GetInt32());
        Assert.False(forwarding.GetProperty("requestId").TryGetProperty("maxLength", out _));
    }

    [Fact]
    public void Analytics_query_appears_only_in_the_administrator_catalogs_with_matching_schemas()
    {
        using var client = new HttpClient();
        var forwarder = new CoCern.Adapter.RelayForwarder(client, new CoCern.Adapter.AdapterOptions("http://127.0.0.1:5271", "client-secret"));
        var adapter = CoCern.Adapter.CoordinationTools.Create(forwarder, admin: true).ToDictionary(tool => tool.ProtocolTool.Name, tool => tool.ProtocolTool.InputSchema.GetRawText());
        var hub = ToolResults.Tools(true).ToDictionary(tool => tool.Name, tool => tool.InputSchema.GetRawText());
        Assert.True(RelayToolCatalog.TryGet(RelayTools.AnalyticsQuery, out var analytics));
        Assert.Equal(RelayToolCatalog.ForwardingSchema(analytics).GetRawText(), adapter[RelayTools.AnalyticsQuery]);
        Assert.Equal(RelayToolCatalog.InputSchema(analytics).GetRawText(), hub[RelayTools.AnalyticsQuery]);
        // Administrator only, a read, and no session: the administrator token is the authority.
        Assert.Equal((false, false, true), (analytics.IsMutation, analytics.RequiresSession, analytics.AdministratorOnly));
        Assert.DoesNotContain(RelayTools.AnalyticsQuery, ToolResults.Tools(false).Select(tool => tool.Name));
        Assert.DoesNotContain(RelayTools.AnalyticsQuery, CoCern.Adapter.CoordinationTools.NamesForRole(false));
        Assert.Contains(RelayTools.AnalyticsQuery, CoCern.Adapter.CoordinationTools.NamesForRole(true));
        Assert.DoesNotContain(RelayTools.AnalyticsQuery, CoCern.Adapter.CoordinationTools.Create(forwarder, admin: false).Select(tool => tool.ProtocolTool.Name));
        // One sentence.
        Assert.DoesNotContain('.', analytics.Description.TrimEnd('.'));
        var strict = RelayToolCatalog.InputSchema(analytics);
        Assert.Equal(new[] { "repositoryId", "name" }, strict.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
        var properties = strict.GetProperty("properties");
        Assert.Equal(AnalyticsQueryNames.All.Append(AnalyticsQueryNames.AdHocSql).Append("batch"), properties.GetProperty("name").GetProperty("enum").EnumerateArray().Select(x => x.GetString()!));
        Assert.Equal(CoCern.Analytics.NamedQueries.Names, AnalyticsQueryNames.All);
        Assert.Equal(RelayLimits.MaxAnalyticsPeriodDays, properties.GetProperty("params").GetProperty("properties").GetProperty("days").GetProperty("maximum").GetInt32());
        Assert.Equal(RelayLimits.MaxAnalyticsSqlLength, properties.GetProperty("sql").GetProperty("maxLength").GetInt32());
        Assert.Equal(new[] { "ifNoneMatch", "includeDetails", "name", "params", "reports", "repositoryId", "sql" }, RelayToolCatalog.ForwardingSchema(analytics).GetProperty("properties").EnumerateObject().Select(x => x.Name).Order());
        foreach (var code in new[] { RelayErrorCodes.AnalyticsUnavailable, RelayErrorCodes.AnalyticsRefused, RelayErrorCodes.AnalyticsTimeout, RelayErrorCodes.AnalyticsFailed })
            Assert.Equal(RelayErrorKind.Invalid, RelayErrorCodes.Classify(code));
    }

    [Fact]
    public void Order_tools_appear_only_in_the_administrator_catalogs_with_matching_schemas()
    {
        using var client = new HttpClient();
        var forwarder = new CoCern.Adapter.RelayForwarder(client, new CoCern.Adapter.AdapterOptions("http://127.0.0.1:5271", "client-secret"));
        var adapter = CoCern.Adapter.CoordinationTools.Create(forwarder, admin: true).ToDictionary(tool => tool.ProtocolTool.Name, tool => tool.ProtocolTool.InputSchema.GetRawText());
        var hub = ToolResults.Tools(true).ToDictionary(tool => tool.Name, tool => tool.InputSchema.GetRawText());
        foreach (var (name, description) in new[] { (RelayTools.OrderPut, "Create or replace a standing order with a trigger, actions and expiry."), (RelayTools.OrderRemove, "Remove a standing order.") })
        {
            Assert.True(RelayToolCatalog.TryGet(name, out var tool));
            Assert.Equal(RelayToolCatalog.ForwardingSchema(tool).GetRawText(), adapter[name]);
            Assert.Equal(RelayToolCatalog.InputSchema(tool).GetRawText(), hub[name]);
            // An administrator-only mutation with no session: the administrator token is the authority, as for standing_put.
            Assert.Equal((true, false, true), (tool.IsMutation, tool.RequiresSession, tool.AdministratorOnly));
            Assert.Equal(description, tool.Description);
            Assert.DoesNotContain(name, ToolResults.Tools(false).Select(item => item.Name));
            Assert.DoesNotContain(name, CoCern.Adapter.CoordinationTools.NamesForRole(false));
            Assert.DoesNotContain(name, CoCern.Adapter.CoordinationTools.Create(forwarder, admin: false).Select(item => item.ProtocolTool.Name));
            Assert.Contains(name, CoCern.Adapter.CoordinationTools.NamesForRole(true));
            var forwarding = RelayToolCatalog.ForwardingSchema(tool).GetProperty("properties");
            Assert.False(forwarding.TryGetProperty("sessionToken", out _));
            Assert.False(forwarding.GetProperty("orderId").TryGetProperty("maxLength", out _));
        }
        RelayToolCatalog.TryGet(RelayTools.OrderPut, out var put);
        var strict = RelayToolCatalog.InputSchema(put);
        Assert.Equal(new[] { "repositoryId", "requestId", "orderId", "trigger", "actions" }, strict.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
        var properties = strict.GetProperty("properties");
        Assert.Equal(OrderTriggers.All, properties.GetProperty("trigger").GetProperty("properties").GetProperty("on").GetProperty("enum").EnumerateArray().Select(x => x.GetString()!));
        Assert.Equal(OrderActions.All, properties.GetProperty("actions").GetProperty("items").GetProperty("properties").GetProperty("do").GetProperty("enum").EnumerateArray().Select(x => x.GetString()!));
        Assert.DoesNotContain("accept", OrderActions.All);
        Assert.Equal(RelayLimits.MaxOrderActions, properties.GetProperty("actions").GetProperty("maxItems").GetInt32());
        Assert.Equal(RelayLimits.MaxOrderNoteLength, properties.GetProperty("actions").GetProperty("items").GetProperty("properties").GetProperty("note").GetProperty("maxLength").GetInt32());
        Assert.Equal(RelayLimits.MaxStandingIdLength, properties.GetProperty("orderId").GetProperty("maxLength").GetInt32());
        Assert.Equal(0, properties.GetProperty("expectedRevision").GetProperty("minimum").GetInt32());
        Assert.Equal(new[] { "actions", "expectedRevision", "expiresAt", "orderId", "repositoryId", "requestId", "trigger" }, RelayToolCatalog.ForwardingSchema(put).GetProperty("properties").EnumerateObject().Select(x => x.Name).Order());
        Assert.Equal(RelayErrorKind.Invalid, RelayErrorCodes.Classify(RelayErrorCodes.OrdersUnavailable));
    }

    [Fact]
    public void Search_tool_appears_in_hub_and_adapter_catalogs_with_matching_schemas()
    {
        using var client = new HttpClient();
        var adapter = CoCern.Adapter.CoordinationTools.Create(new CoCern.Adapter.RelayForwarder(client, new CoCern.Adapter.AdapterOptions("http://127.0.0.1:5271", "client-secret")), admin: false)
            .ToDictionary(tool => tool.ProtocolTool.Name, tool => tool.ProtocolTool.InputSchema.GetRawText());
        var hub = ToolResults.Tools(false).ToDictionary(tool => tool.Name, tool => tool.InputSchema.GetRawText());
        Assert.True(RelayToolCatalog.TryGet(RelayTools.Search, out var search));
        Assert.Equal(RelayToolCatalog.ForwardingSchema(search).GetRawText(), adapter[RelayTools.Search]);
        Assert.Equal(RelayToolCatalog.InputSchema(search).GetRawText(), hub[RelayTools.Search]);
        Assert.Equal((false, true, false), (search.IsMutation, search.RequiresSession, search.AdministratorOnly));
        // One sentence.
        Assert.DoesNotContain('.', search.Description.TrimEnd('.'));
        var strict = RelayToolCatalog.InputSchema(search);
        Assert.Equal(new[] { "repositoryId", "sessionId", "sessionToken", "query" }, strict.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
        var properties = strict.GetProperty("properties");
        Assert.Equal(SearchKinds.All, properties.GetProperty("kinds").GetProperty("items").GetProperty("enum").EnumerateArray().Select(x => x.GetString()!));
        Assert.Equal(RelayLimits.MaxSearchQueryLength, properties.GetProperty("query").GetProperty("maxLength").GetInt32());
        Assert.Equal(RelayLimits.MaxSearchResults, properties.GetProperty("limit").GetProperty("maximum").GetInt32());
        Assert.True(properties.TryGetProperty("status", out _));
        // The forwarding copy is minimal: the session envelope, query and kinds, with no requestId for a read.
        Assert.Equal(new[] { "kinds", "query", "repositoryId", "sessionId", "sessionToken" }, RelayToolCatalog.ForwardingSchema(search).GetProperty("properties").EnumerateObject().Select(x => x.Name).Order());
        Assert.Contains(RelayTools.Search, CoCern.Adapter.CoordinationTools.NamesForRole(false));
        Assert.DoesNotContain(RelayTools.Search, CoCern.Adapter.CoordinationTools.NamesForRole(false, chatOnly: true));
        Assert.Equal(RelayErrorKind.Invalid, RelayErrorCodes.Classify(RelayErrorCodes.SearchUnavailable));
    }

    [Fact]
    public void Attention_respond_appears_in_hub_and_adapter_catalogs_with_matching_schemas()
    {
        using var client = new HttpClient();
        var adapter = CoCern.Adapter.CoordinationTools.Create(new CoCern.Adapter.RelayForwarder(client, new CoCern.Adapter.AdapterOptions("http://127.0.0.1:5271", "client-secret")), admin: false)
            .ToDictionary(tool => tool.ProtocolTool.Name, tool => tool.ProtocolTool.InputSchema.GetRawText());
        var hub = ToolResults.Tools(false).ToDictionary(tool => tool.Name, tool => tool.InputSchema.GetRawText());
        Assert.True(RelayToolCatalog.TryGet(RelayTools.AttentionRespond, out var respond));
        Assert.Equal(RelayToolCatalog.ForwardingSchema(respond).GetRawText(), adapter[RelayTools.AttentionRespond]);
        Assert.Equal(RelayToolCatalog.InputSchema(respond).GetRawText(), hub[RelayTools.AttentionRespond]);
        Assert.Contains(RelayTools.AttentionRespond, ToolResults.Tools(true).Select(tool => tool.Name));
        Assert.Equal((true, true, false), (respond.IsMutation, respond.RequiresSession, respond.AdministratorOnly));
        Assert.Equal("Accept, dismiss or postpone an attention offer; postponing never acknowledges the message.", respond.Description);
        var strict = RelayToolCatalog.InputSchema(respond);
        Assert.Equal(new[] { "repositoryId", "requestId", "sessionId", "sessionToken", "itemId", "action" }, strict.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
        var properties = strict.GetProperty("properties");
        Assert.Equal(AttentionActions.All, properties.GetProperty("action").GetProperty("enum").EnumerateArray().Select(x => x.GetString()!));
        Assert.Equal(RelayLimits.MaxIdentifierLength, properties.GetProperty("itemId").GetProperty("maxLength").GetInt32());
        Assert.Equal("date-time", properties.GetProperty("until").GetProperty("format").GetString());
        // The forwarding copy is the session envelope plus the three answer fields, by type only.
        var forwarding = RelayToolCatalog.ForwardingSchema(respond).GetProperty("properties");
        Assert.Equal(new[] { "action", "itemId", "repositoryId", "requestId", "sessionId", "sessionToken", "until" }, forwarding.EnumerateObject().Select(x => x.Name).Order());
        Assert.Equal(AttentionActions.All, forwarding.GetProperty("action").GetProperty("enum").EnumerateArray().Select(x => x.GetString()!));
        Assert.False(forwarding.GetProperty("itemId").TryGetProperty("maxLength", out _));
        // Every session may answer, but the messaging-only chat profile receives no offers to answer.
        Assert.Contains(RelayTools.AttentionRespond, CoCern.Adapter.CoordinationTools.NamesForRole(false));
        Assert.DoesNotContain(RelayTools.AttentionRespond, CoCern.Adapter.CoordinationTools.NamesForRole(false, chatOnly: true));
        Assert.Equal(RelayErrorKind.Conflict, RelayErrorCodes.Classify(RelayErrorCodes.SnoozeLimit));
    }

    [Fact]
    public void Boundary_tools_advertise_the_attention_opt_out_in_hub_and_adapter_catalogs()
    {
        using var client = new HttpClient();
        var adapter = CoCern.Adapter.CoordinationTools.Create(new CoCern.Adapter.RelayForwarder(client, new CoCern.Adapter.AdapterOptions("http://127.0.0.1:5271", "client-secret")), admin: false)
            .ToDictionary(tool => tool.ProtocolTool.Name, tool => tool.ProtocolTool.InputSchema.GetRawText());
        var hub = ToolResults.Tools(false).ToDictionary(tool => tool.Name, tool => tool.InputSchema.GetRawText());
        foreach (var name in new[] { RelayTools.TaskUpdate, RelayTools.ActionReport, RelayTools.TaskClaim })
        {
            Assert.True(RelayToolCatalog.TryGet(name, out var tool));
            Assert.Equal(RelayToolCatalog.ForwardingSchema(tool).GetRawText(), adapter[name]);
            Assert.Equal(RelayToolCatalog.InputSchema(tool).GetRawText(), hub[name]);
            Assert.Equal("boolean", RelayToolCatalog.ForwardingSchema(tool).GetProperty("properties").GetProperty("attention").GetProperty("type").GetString());
            Assert.Equal("boolean", RelayToolCatalog.InputSchema(tool).GetProperty("properties").GetProperty("attention").GetProperty("type").GetString());
        }
        // check_inbox is not a boundary, so it has no opt-out; and no new tool was added for delivery.
        Assert.True(RelayToolCatalog.TryGet(RelayTools.CheckInbox, out var inbox));
        Assert.False(RelayToolCatalog.InputSchema(inbox).GetProperty("properties").TryGetProperty("attention", out _));
        // An offer always fits the room the hub keeps for composing replies.
        Assert.True(RelayLimits.MaxAttentionOfferBytes <= RelayLimits.CompositionReserveBytes / 2);
    }

    [Fact]
    public void Forwarding_catalog_stays_within_its_context_budget()
    {
        // Every connected agent carries these definitions on every turn; the pre-refactor adapter was about 19,400 characters.
        // Workers are most agents, so their surface keeps the original 20,000-character budget.
        Assert.True(Size(administrator: false) < 20_000, $"Worker forwarding catalog grew to {Size(administrator: false)} characters.");
        // The room left for work already planned is smaller than that budget, so this is the cap a change actually meets.
        Assert.True(Size(administrator: false) <= 18_968, $"Worker forwarding catalog grew to {Size(administrator: false)} characters.");
        // Role-specific lists keep administrator-only tools (standing_put, control_request, and later analytics_query and the order
        // tools) away from workers, so only the few administrator chats pay for them; that surface has its own, larger cap.
        // Waiting reports and reply acknowledgments measure 22,603 characters together; keep the cap below the 23,000 ceiling.
        Assert.True(Size(administrator: true) < 22_650, $"Administrator forwarding catalog grew to {Size(administrator: true)} characters.");

        static int Size(bool administrator) => RelayToolCatalog.ForRole(administrator).Sum(tool => tool.Description.Length + RelayToolCatalog.ForwardingSchema(tool).GetRawText().Length);
    }

    [Fact]
    public async Task Non_string_resource_ids_are_invalid_arguments_not_internal_errors()
    {
        var application = new RelayApplication(null!);
        var caller = new RelayCaller("alice", true, ["repo"]);
        var reply = await application.ExecuteAsync(caller, RelayTools.TaskClaim, JsonSerializer.SerializeToElement(new { repositoryId = "repo", requestId = "r", resourceIds = new[] { 1 } }));
        Assert.Equal(RelayErrorCodes.InvalidArguments, reply.Code);
    }
}
