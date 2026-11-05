using CoCern.Contracts.Dashboard;
using CoCern.Dashboard.Services;
using Xunit;

namespace CoCern.Tests;

public sealed class DashboardStationsTests
{
    [Fact]
    public void Hostname_aliases_and_capitalization_share_a_card_without_losing_sessions()
    {
        var stations = DashboardStations.Build(new DashboardSnapshot
        {
            Machines = [new() { MachineId = "node-fc", Name = "DESKTOP-FC", Reachable = true }, new() { MachineId = "node-sc", Name = "DESKTOP-SC" }],
            Sessions =
            [
                new() { SessionId = "local-1", MachineId = "16XAURORA" },
                new() { SessionId = "local-2", MachineId = "16XAurora" },
                new() { SessionId = "fc-1", MachineId = "node-fc", MachineName = "DESKTOP-FC", Reachable = true },
                new() { SessionId = "fc-2", MachineId = "desktop-fc" },
                new() { SessionId = "sc-1", MachineId = "DESKTOP-SC" },
                new() { SessionId = "unknown" }
            ]
        });

        Assert.Equal(4, stations.Length);
        var local = Assert.Single(stations, station => station.MachineId == "16XAURORA");
        Assert.False(local.Enrolled);
        Assert.Equal(["local-1", "local-2"], local.Sessions.Select(session => session.SessionId));
        var fc = Assert.Single(stations, station => station.MachineId == "node-fc");
        Assert.True(fc.Enrolled);
        Assert.True(fc.Reachable);
        Assert.Equal(2, fc.SessionCount);
        Assert.Equal("DESKTOP-FC", fc.Name);
        var sc = Assert.Single(stations, station => station.MachineId == "node-sc");
        Assert.True(sc.Enrolled);
        Assert.False(sc.Reachable);
        Assert.Equal("sc-1", Assert.Single(sc.Sessions).SessionId);
        Assert.Equal("unknown", Assert.Single(Assert.Single(stations, station => station.MachineId == "").Sessions).SessionId);
        Assert.Equal(6, stations.Sum(station => station.SessionCount));
    }

    [Fact]
    public void Shared_hostnames_do_not_merge_distinct_enrollments_or_guess_legacy_ownership()
    {
        var stations = DashboardStations.Build(new DashboardSnapshot
        {
            Machines = [new() { MachineId = "node-1", Name = "DESKTOP" }, new() { MachineId = "node-2", Name = "desktop" }],
            Sessions = [new() { SessionId = "first", MachineId = "node-1" }, new() { SessionId = "second", MachineId = "node-2" }, new() { SessionId = "legacy", MachineId = "DESKTOP" }]
        });

        Assert.Equal(3, stations.Length);
        Assert.Equal("first", Assert.Single(Assert.Single(stations, station => station.MachineId == "node-1").Sessions).SessionId);
        Assert.Equal("second", Assert.Single(Assert.Single(stations, station => station.MachineId == "node-2").Sessions).SessionId);
        Assert.False(Assert.Single(stations, station => station.MachineId == "DESKTOP").Enrolled);
    }
}
