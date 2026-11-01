using CoCern.Contracts.Dashboard;
using Xunit;

namespace CoCern.Tests;

public sealed class DashboardFreshnessReviewTests
{
    private static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(12);
    private static readonly DateTimeOffset ServerTime = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(-3)]
    [InlineData(3)]
    public void Advancing_observations_remain_live_with_phone_clocks_days_apart(int clockOffsetDays)
    {
        var clock = new DashboardObservationClock();
        var firstReceipt = ServerTime.AddDays(clockOffsetDays);
        var phoneStartedAt = firstReceipt.AddMinutes(-1);
        Assert.True(clock.Accept("relay-a", 1, ServerTime, firstReceipt - phoneStartedAt));
        Assert.False(clock.Confirmed);
        Assert.False(clock.IsFresh(firstReceipt - phoneStartedAt, MaximumAge));

        var phoneNow = firstReceipt.AddSeconds(5);
        var elapsed = phoneNow - phoneStartedAt;
        Assert.True(clock.Accept("relay-a", 2, ServerTime.AddSeconds(5), elapsed));
        Assert.True(clock.Confirmed);
        Assert.True(clock.IsFresh(elapsed, MaximumAge));
        Assert.True(clock.IsFresh(elapsed + TimeSpan.FromSeconds(11), MaximumAge));
        Assert.Equal(ServerTime.AddSeconds(8), clock.ServerNow(elapsed + TimeSpan.FromSeconds(3)));

        var reportFreshUntil = ServerTime.AddSeconds(10);
        Assert.True(reportFreshUntil > clock.ServerNow(elapsed + TimeSpan.FromSeconds(3)));
        Assert.False(reportFreshUntil > clock.ServerNow(elapsed + TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Initial_old_observation_waits_for_a_later_server_timestamp()
    {
        var clock = new DashboardObservationClock();
        var oldObservation = ServerTime.AddHours(-3);
        Assert.True(clock.Accept("relay-a", 1, oldObservation, TimeSpan.FromSeconds(10)));
        Assert.False(clock.Confirmed);
        Assert.False(clock.IsFresh(TimeSpan.FromSeconds(10), MaximumAge));
        Assert.Equal(oldObservation, clock.ServerNow(TimeSpan.FromSeconds(10)));

        Assert.True(clock.Accept("relay-a", 2, oldObservation, TimeSpan.FromSeconds(12)));
        Assert.False(clock.Confirmed);
        Assert.False(clock.IsFresh(TimeSpan.FromSeconds(12), MaximumAge));
        Assert.Equal(oldObservation.AddSeconds(2), clock.ServerNow(TimeSpan.FromSeconds(12)));

        Assert.True(clock.Accept("relay-a", 3, ServerTime, TimeSpan.FromSeconds(15)));
        Assert.True(clock.Confirmed);
        Assert.True(clock.IsFresh(TimeSpan.FromSeconds(15), MaximumAge));
        Assert.Equal(ServerTime, clock.ServerNow(TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public void Frozen_observation_expires_despite_incoming_revisions()
    {
        var clock = new DashboardObservationClock();
        Assert.True(clock.Accept("relay-a", 1, ServerTime, TimeSpan.Zero));
        Assert.True(clock.Accept("relay-a", 2, ServerTime.AddSeconds(1), TimeSpan.FromSeconds(1)));
        Assert.True(clock.IsFresh(TimeSpan.FromSeconds(1), MaximumAge));
        for (var seconds = 2; seconds <= 13; seconds++)
        {
            Assert.True(clock.Accept("relay-a", seconds + 1, ServerTime.AddSeconds(1), TimeSpan.FromSeconds(seconds)));
            Assert.Equal(seconds < 13, clock.IsFresh(TimeSpan.FromSeconds(seconds), MaximumAge));
        }

        Assert.False(clock.IsFresh(TimeSpan.FromSeconds(13), MaximumAge));
        Assert.Equal(ServerTime.AddSeconds(13), clock.ServerNow(TimeSpan.FromSeconds(13)));

        Assert.True(clock.Accept("relay-a", 15, ServerTime.AddSeconds(14), TimeSpan.FromSeconds(14)));
        Assert.True(clock.IsFresh(TimeSpan.FromSeconds(14), MaximumAge));
    }

    [Fact]
    public void Process_change_accepts_reset_revision_and_requires_new_confirmation()
    {
        var clock = new DashboardObservationClock();
        Assert.True(clock.Accept("relay-a", 500, ServerTime, TimeSpan.Zero));
        Assert.True(clock.Accept("relay-a", 501, ServerTime.AddSeconds(1), TimeSpan.FromSeconds(1)));
        Assert.True(clock.Confirmed);
        Assert.False(clock.IsFresh(TimeSpan.FromSeconds(20), MaximumAge));

        var restartedAt = ServerTime.AddDays(-1);
        Assert.True(clock.Accept("relay-b", 1, restartedAt, TimeSpan.FromSeconds(20)));
        Assert.False(clock.Confirmed);
        Assert.False(clock.IsFresh(TimeSpan.FromSeconds(20), MaximumAge));
        Assert.True(clock.Accept("relay-b", 2, restartedAt, TimeSpan.FromSeconds(21)));
        Assert.False(clock.IsFresh(TimeSpan.FromSeconds(21), MaximumAge));
        Assert.Equal(restartedAt.AddSeconds(2), clock.ServerNow(TimeSpan.FromSeconds(22)));

        Assert.True(clock.Accept("relay-b", 3, restartedAt.AddSeconds(2), TimeSpan.FromSeconds(22)));
        Assert.True(clock.Confirmed);
        Assert.True(clock.IsFresh(TimeSpan.FromSeconds(22), MaximumAge));
    }

    [Fact]
    public void Duplicate_and_older_revisions_do_not_refresh_observation_progress()
    {
        var clock = new DashboardObservationClock();
        Assert.True(clock.Accept("relay-a", 10, ServerTime, TimeSpan.Zero));
        Assert.True(clock.Accept("relay-a", 11, ServerTime.AddSeconds(1), TimeSpan.FromSeconds(1)));
        Assert.True(clock.IsFresh(TimeSpan.FromSeconds(1), MaximumAge));
        Assert.False(clock.Accept("relay-a", 9, ServerTime.AddDays(1), TimeSpan.FromSeconds(12)));
        Assert.False(clock.Accept("relay-a", 11, ServerTime.AddDays(1), TimeSpan.FromSeconds(13)));
        Assert.False(clock.IsFresh(TimeSpan.FromSeconds(13), MaximumAge));

        Assert.True(clock.Accept("relay-a", 12, ServerTime.AddSeconds(-1), TimeSpan.FromSeconds(14)));
        Assert.False(clock.IsFresh(TimeSpan.FromSeconds(14), MaximumAge));
        Assert.Equal(ServerTime.AddSeconds(14), clock.ServerNow(TimeSpan.FromSeconds(14)));
    }

    [Fact]
    public void Signing_out_clears_prior_confirmation_and_acceptance_state()
    {
        var clock = new DashboardObservationClock();
        Assert.False(clock.IsFresh(TimeSpan.Zero, MaximumAge));
        Assert.Null(clock.ServerNow(TimeSpan.Zero));
        Assert.True(clock.Accept("relay-a", 10, ServerTime, TimeSpan.Zero));
        Assert.True(clock.Accept("relay-a", 11, ServerTime.AddSeconds(1), TimeSpan.FromSeconds(1)));
        Assert.True(clock.Confirmed);

        clock.Reset();

        Assert.False(clock.Confirmed);
        Assert.False(clock.IsFresh(TimeSpan.FromSeconds(1), MaximumAge));
        Assert.Null(clock.ServerNow(TimeSpan.FromSeconds(1)));
        Assert.True(clock.Accept("relay-a", 1, ServerTime.AddDays(-1), TimeSpan.FromSeconds(2)));
        Assert.False(clock.IsFresh(TimeSpan.FromSeconds(2), MaximumAge));
        Assert.True(clock.Accept("relay-a", 2, ServerTime.AddDays(-1).AddSeconds(1), TimeSpan.FromSeconds(3)));
        Assert.True(clock.IsFresh(TimeSpan.FromSeconds(3), MaximumAge));
    }
}
