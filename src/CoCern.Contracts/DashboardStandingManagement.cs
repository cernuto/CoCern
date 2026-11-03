namespace CoCern.Contracts.Dashboard;

public sealed record DashboardStandingDecision
{
    public string RepositoryId { get; init; } = "";
    public string BoardId { get; init; } = "";
    public string Id { get; init; } = "";
    public string Action { get; init; } = "";
    public long? ExpectedRevision { get; init; }
    public string RequestId { get; init; } = "";
}

public sealed record DashboardStandingDecisionResult(bool Ok, string Code, long Revision);
