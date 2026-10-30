namespace PkRelay.Contracts.Dashboard;

/// <summary>Orders complete dashboard observations within a host process lifetime.</summary>
public static class DashboardSnapshotOrder
{
    public static bool ShouldAccept(string? currentInstanceId, long currentRevision, string incomingInstanceId, long incomingRevision)
        => !string.Equals(currentInstanceId, incomingInstanceId, StringComparison.Ordinal) || incomingRevision > currentRevision;
}
