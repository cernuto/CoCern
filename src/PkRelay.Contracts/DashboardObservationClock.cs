namespace PkRelay.Contracts.Dashboard;

/// <summary>Tracks observation progress with client elapsed time, independent of the client wall clock.</summary>
public sealed class DashboardObservationClock
{
    private string? _instanceId;
    private long _revision;
    private DateTimeOffset? _observedAt;
    private TimeSpan _anchoredAt;
    private TimeSpan? _advancedAt;

    public bool Confirmed => _advancedAt is not null;

    public bool Accept(string instanceId, long revision, DateTimeOffset observedAt, TimeSpan elapsed)
    {
        if (!DashboardSnapshotOrder.ShouldAccept(_instanceId, _revision, instanceId, revision)) return false;
        if (!string.Equals(_instanceId, instanceId, StringComparison.Ordinal) || _observedAt is null)
        {
            _observedAt = observedAt;
            _anchoredAt = elapsed;
            _advancedAt = null;
        }
        else if (observedAt > _observedAt)
        {
            _observedAt = observedAt;
            _anchoredAt = elapsed;
            _advancedAt = elapsed;
        }
        _instanceId = instanceId;
        _revision = revision;
        return true;
    }

    public bool IsFresh(TimeSpan elapsed, TimeSpan maximumAge) =>
        _advancedAt is { } advancedAt && elapsed - advancedAt < maximumAge;

    public DateTimeOffset? ServerNow(TimeSpan elapsed) =>
        _observedAt is { } observedAt ? observedAt + (elapsed - _anchoredAt) : null;

    public void Reset()
    {
        _instanceId = null;
        _revision = 0;
        _observedAt = null;
        _anchoredAt = default;
        _advancedAt = null;
    }
}
