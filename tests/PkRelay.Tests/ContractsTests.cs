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
        Assert.Equal(["repositoryId", "sessionId", "sessionToken", "requestId"], forwarding.GetProperty("properties").EnumerateObject().Select(x => x.Name));

        RelayToolCatalog.TryGet(RelayTools.TaskUpdate, out var update);
        Assert.True(RelayToolCatalog.ForwardingSchema(update).GetProperty("properties").TryGetProperty("workflow", out _));
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
