namespace CoCern.Contracts.Dashboard;

/// <summary>Credential-free local installation checks and the exact owned changes Repair proposes.</summary>
public sealed record DashboardRepairStatus
{
    public string? Unavailable { get; init; }
    public DashboardRepairCheck[] Checks { get; init; } = [];
    public string[] PlannedChanges { get; init; } = [];
    public string[] AppliedChanges { get; init; } = [];
    public string NextAction { get; init; } = "";
    public bool RepairPerformed { get; init; }
}

public sealed record DashboardRepairCheck
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>passed, warning, needs_repair or unavailable.</summary>
    public string State { get; init; } = "unavailable";
    public string Detail { get; init; } = "";
    public bool Repairable { get; init; }
}
