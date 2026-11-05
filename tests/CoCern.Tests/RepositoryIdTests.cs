using System.Text.Json;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
using Xunit;

namespace CoCern.Tests;

public class RepositoryIdTests
{
    [Fact]
    public void Repository_case_resolves_to_one_authorized_canonical_namespace()
    {
        var configured = RepositoryIds.Configured(["cocern", "other", "cocern"]);
        string? Resolve(string supplied) => RepositoryIds.Match(supplied, configured);
        Assert.True(RelayToolCatalog.TryGet(RelayTools.GetStatus, out var tool));
        var caller = new RelayCaller("worker", false, ["cocern"]);
        using var request = JsonDocument.Parse("{\"repositoryId\":\"CoCeRn\"}");
        Assert.Null(RelayRequestGuard.Validate(caller, tool!, request.RootElement, out var args, Resolve));
        Assert.Equal("cocern", args["repositoryId"]!.GetValue<string>());
        using var canonical = JsonDocument.Parse("{\"repositoryId\":\"cocern\"}");
        Assert.Null(RelayRequestGuard.Validate(caller, tool!, canonical.RootElement, out var same, Resolve));
        Assert.Equal(same.ToJsonString(), args.ToJsonString());
        using var unknown = JsonDocument.Parse("{\"repositoryId\":\"missing\"}");
        using var denied = JsonDocument.Parse("{\"repositoryId\":\"OTHER\"}");
        Assert.Equal(RelayErrorCodes.RepositoryDenied, RelayRequestGuard.Validate(caller, tool!, denied.RootElement, out _, Resolve)!.Code);
        Assert.Equal(RelayErrorCodes.RepositoryDenied, RelayRequestGuard.Validate(caller with { IsAdministrator = true }, tool!, unknown.RootElement, out _, Resolve)!.Code);
        Assert.Throws<InvalidOperationException>(() => RepositoryIds.Configured(["cocern", "CoCern"]));
    }
}
