namespace CoCern.Contracts.Dashboard;

/// <summary>Measured connection phases. No arguments, credentials or session identifiers are included.</summary>
public sealed record ConnectionDiagnosticEvent
{
    public string CorrelationId { get; init; } = "";
    public string Tool { get; init; } = "";
    public string Source { get; init; } = "";
    public string Stage { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public double DurationMs { get; init; }
    public string Outcome { get; init; } = "";
    public string? ErrorCode { get; init; }
}

public sealed record ConnectionDiagnosticsSnapshot
{
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
    public ConnectionDiagnosticEvent[] Events { get; init; } = [];
    public string Note { get; init; } = "This PC's recent connection traces include locally observed and authenticated client-reported phases. Durations overlap; do not add totals to their phases. Measurement starts when CoCern receives the request, not while the model is deciding to call it. Missing phases were not observed.";
    public string? Unavailable { get; init; }
}

public sealed record ConnectionDiagnosticsBatch
{
    public ConnectionDiagnosticEvent[] Events { get; init; } = [];
}
