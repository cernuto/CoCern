using System.Text.Json;
using PkRelay.Contracts;
using PkRelay.Core;
using PkRelay.Domain;
using PkRelay.Host;
using Xunit;

namespace PkRelay.Tests;

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
        Assert.Equal(RelayToolCatalog.ForRole(true).Select(tool => tool.Name), PkRelay.Adapter.CoordinationTools.NamesForRole(true));
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
        var adapter = PkRelay.Adapter.CoordinationTools.Create(new PkRelay.Adapter.RelayForwarder(client, new PkRelay.Adapter.AdapterOptions("http://127.0.0.1:5271", "client-secret")), admin: true)
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
        Assert.DoesNotContain(RelayTools.StandingPut, PkRelay.Adapter.CoordinationTools.NamesForRole(false));
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
    public void Forwarding_catalog_stays_within_its_context_budget()
    {
        // Every connected agent carries these definitions on every turn; the pre-refactor adapter was about 19,400 characters.
        var size = RelayToolCatalog.ForRole(true).Sum(tool => tool.Description.Length + RelayToolCatalog.ForwardingSchema(tool).GetRawText().Length);
        Assert.True(size < 20_000, $"Forwarding catalog grew to {size} characters.");
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
