namespace CoCern.Contracts;

/// <summary>A task workflow's states. The tool schema and the board's validator both read these, so they cannot drift.</summary>
public static class WorkflowStates
{
    public const string Ready = "ready";
    public const string Blocked = "blocked";
    public const string Completed = "completed";

    public static readonly IReadOnlyList<string> All = [Ready, Blocked, Completed];
}

/// <summary>An acceptance criterion's result; <see cref="Passed"/> needs an evidence reference.</summary>
public static class CriterionResults
{
    public const string NotRun = "not_run";
    public const string Passed = "passed";
    public const string Failed = "failed";

    public static readonly IReadOnlyList<string> All = [NotRun, Passed, Failed];
}

/// <summary>What blocks a task.</summary>
public static class BlockerKinds
{
    public const string CoordinatorHold = "coordinator_hold";
    public const string ProviderRejection = "provider_rejection";

    public static readonly IReadOnlyList<string> All = [CoordinatorHold, ProviderRejection, "tool_unavailable", "resource_busy", "missing_fixture", "prerequisite", "other"];
}

/// <summary>What an <c>action_report</c> may say happened.</summary>
public static class ActionOutcomes
{
    public static readonly IReadOnlyList<string> All = ["started", "completed", "failed", "canceled", "incomplete", "unknown"];
}

/// <summary>An action's restoration obligation.</summary>
public static class Restorations
{
    public const string NotRequired = "not_required";

    /// <summary>What a report may set. <see cref="NotRequired"/> is only how an action without one starts.</summary>
    public static readonly IReadOnlyList<string> Reported = ["pending", "blocked", "verified", "accepted"];
    public static readonly IReadOnlyList<string> All = [.. Reported, NotRequired];
}
