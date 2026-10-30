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
    public void Forwarding_schema_documents_every_field_but_requires_only_the_repository()
    {
        RelayToolCatalog.TryGet(RelayTools.TaskClaim, out var tool);
        var strict = RelayToolCatalog.InputSchema(tool);
        var loose = RelayToolCatalog.InputSchema(tool, enforceRequired: false);
        Assert.Contains("resourceIds", strict.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(["repositoryId"], loose.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
        Assert.True(loose.GetProperty("properties").TryGetProperty("resourceIds", out _));
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
