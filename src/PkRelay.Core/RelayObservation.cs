using PkRelay.Contracts.Dashboard;

namespace PkRelay.Core;

/// <summary>A read projection from the existing storage owner. Null means collection was busy.</summary>
public interface IRelayObservationSource
{
    Task<RelayObservation?> ReadObservationAsync(DateTimeOffset now, string incarnationId, CancellationToken cancellationToken = default);
    Task<DatabaseMetrics?> ReadDatabaseMetricsAsync(int rowCountLimit = 10_000, CancellationToken cancellationToken = default);
    /// <summary>Standing preferences of the boards that have any, bounded. Null means collection was busy; a source without boards has none.</summary>
    Task<DashboardStandingObservation?> ReadStandingAsync(CancellationToken cancellationToken = default) => Task.FromResult<DashboardStandingObservation?>(new());
}

public sealed record DashboardStandingObservation
{
    public DashboardStanding[] Boards { get; init; } = [];
    public bool Truncated { get; init; }
}

public sealed record RelayObservation
{
    public int SessionCount { get; init; }
    public DashboardMachine[] Machines { get; init; } = [];
    public DashboardSession[] Sessions { get; init; } = [];
    public bool SessionsTruncated { get; init; }
    public string[] Warnings { get; init; } = [];
}

