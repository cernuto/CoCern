using CoCern.Contracts.Dashboard;
using CoCern.Domain;

namespace CoCern.Core;

/// <summary>A read projection from the existing storage owner. Null means collection was busy.</summary>
public interface IRelayObservationSource
{
    Task<RelayObservation?> ReadObservationAsync(DateTimeOffset now, string incarnationId, CancellationToken cancellationToken = default);
    Task<DatabaseMetrics?> ReadDatabaseMetricsAsync(int rowCountLimit = 10_000, CancellationToken cancellationToken = default);
    /// <summary>Standing preferences of the boards that have any, bounded. Null means collection was busy; a source without boards has none.</summary>
    Task<DashboardStandingObservation?> ReadStandingAsync(CancellationToken cancellationToken = default) => Task.FromResult<DashboardStandingObservation?>(new());
    /// <summary>Repository-scoped pending questions, oldest first. Null means busy; reservations are made only by the board.</summary>
    Task<IReadOnlyList<StandingPresentation>?> ReadPendingStandingAsync(string repositoryId, string principalId, string sessionId, string requestCorrelation, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StandingPresentation>?>(Array.Empty<StandingPresentation>());
    Task<IReadOnlyList<StandingPresentation>?> ReadPendingStandingForParticipantAsync(string repositoryId, string principalId, string sessionId, string requestCorrelation, VerifiedNativeParticipant? participant, CancellationToken cancellationToken = default) =>
        ReadPendingStandingAsync(repositoryId, principalId, sessionId, requestCorrelation, cancellationToken);
    /// <summary>Blocked tasks in every repository that "Waiting on you" may list, bounded, for the dashboard's 15-second read. Null means collection was busy.</summary>
    Task<WaitingSources?> ReadWaitingBlockersAsync(CancellationToken cancellationToken = default) => Task.FromResult<WaitingSources?>(new());
    /// <summary>
    /// Everything "Waiting on you" reads for one repository, and no other, for an administrator's <c>get_status</c>, which reads on call since
    /// the collector only runs with a viewer. Null means the store stayed busy.
    /// </summary>
    Task<WaitingSources?> ReadWaitingAsync(string repositoryId, CancellationToken cancellationToken = default) => Task.FromResult<WaitingSources?>(new());
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
    public DashboardInteractions Interactions { get; init; } = new();
    /// <summary>The reports of the sessions in <see cref="Sessions"/>, read from the same rows; <see cref="SessionsTruncated"/> says some sessions were not read.</summary>
    public WaitingReport[] WaitingReports { get; init; } = [];
    /// <summary>The unread direct mail of the sessions in <see cref="Sessions"/> and their own last hub calls, for the unresponsive row.</summary>
    public WaitingMail[] WaitingMail { get; init; } = [];
    public string[] Warnings { get; init; } = [];
}

