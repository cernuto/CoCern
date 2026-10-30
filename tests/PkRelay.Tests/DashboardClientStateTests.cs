using PkRelay.Contracts.Dashboard;
using Xunit;

namespace PkRelay.Tests;

public sealed class DashboardClientStateTests
{
    [Fact]
    public void First_snapshot_establishes_the_revision_baseline()
    {
        Assert.True(DashboardSnapshotOrder.ShouldAccept(null, 0, "host-a", 1));
    }

    [Theory]
    [InlineData(12, true)]
    [InlineData(11, false)]
    [InlineData(10, false)]
    public void Same_instance_only_accepts_newer_complete_snapshots(long incomingRevision, bool expected)
    {
        Assert.Equal(expected, DashboardSnapshotOrder.ShouldAccept("host-a", 11, "host-a", incomingRevision));
    }

    [Fact]
    public void Restarted_host_accepts_a_lower_revision_as_a_new_baseline()
    {
        Assert.True(DashboardSnapshotOrder.ShouldAccept("host-a", 200, "host-b", 1));
    }
}
