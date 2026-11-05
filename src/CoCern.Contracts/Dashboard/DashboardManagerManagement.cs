namespace CoCern.Contracts.Dashboard;

public sealed record DashboardManagerDecision(string RepositoryId, string RequestId, string Command, string? TargetSessionId, long? ExpectedManagerGeneration, string? HumanWords);
public sealed record DashboardDispatchDecision(string RepositoryId, string RequestId, string Command);
public sealed record DashboardManagerResult(bool Ok, string Code, ManagerSelectionStatus? Manager, bool? Paused, AdminActionPage? Actions = null);
