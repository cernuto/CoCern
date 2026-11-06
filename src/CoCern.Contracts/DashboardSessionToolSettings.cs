namespace CoCern.Contracts.Dashboard;

public sealed record DashboardSessionToolSettingsRequest
{
    public string RepositoryId { get; init; } = "";
    public string SessionId { get; init; } = "";
    public string Profile { get; init; } = "";
    public long ExpectedVersion { get; init; }
    public string HumanWords { get; init; } = "";
}

public sealed record DashboardSessionToolSettingsResult
{
    public bool Ok { get; init; }
    public string? Code { get; init; }
    public string? Profile { get; init; }
    public long Revision { get; init; }
}
