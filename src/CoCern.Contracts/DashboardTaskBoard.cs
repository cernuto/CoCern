namespace CoCern.Contracts.Dashboard;

public sealed record DashboardBoardRow(string TaskId, long Version, string? Owner, bool LeaseActive,
    bool? UnresolvedActions, string? WorkflowState, bool Archived, DateTimeOffset? LeaseUntil = null,
    string? Title = null, string? Milestone = null, string? UpdatedBy = null, DateTimeOffset? UpdatedAt = null,
    string? BlockerKind = null, string? Blocker = null, string? NextActor = null, string? ParentTaskId = null,
    DashboardReview? Review = null, bool HumanDecisionsAnswered = false);

public sealed record DashboardReview(string Commit, int? PullRequest, string Branch, string Reviewer, string Conclusion);
public sealed record DashboardReportPage(long Version, string[] Reports, int? NextCursor);
public sealed record DashboardRevisionComparison(string? PullRequestUrl, int? NewerCommits);

public sealed record DashboardBoardPage(long Revision, int? NextCursor, DashboardBoardRow[] Items, bool Restart = false, DateTimeOffset? SampledAt = null);
