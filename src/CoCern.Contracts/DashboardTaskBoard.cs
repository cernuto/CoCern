namespace CoCern.Contracts.Dashboard;

public sealed record DashboardBoardRow(string TaskId, long Version, string? Owner, bool LeaseActive,
    bool? UnresolvedActions, string? WorkflowState, bool Archived, DateTimeOffset? LeaseUntil = null);

public sealed record DashboardBoardPage(long Revision, int? NextCursor, DashboardBoardRow[] Items, bool Restart = false, DateTimeOffset? SampledAt = null);
