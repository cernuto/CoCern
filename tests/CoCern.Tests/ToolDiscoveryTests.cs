using CoCern.Contracts;
using Xunit;

namespace CoCern.Tests;

public class ToolDiscoveryTests
{

    [Fact]
    public void Standing_tools_are_discovered_by_role()
    {
        var administrator = CoCern.Adapter.CoordinationTools.NamesForRole(true);
        var worker = CoCern.Adapter.CoordinationTools.NamesForRole(false);
        Assert.Contains(RelayTools.StandingPropose, administrator);
        Assert.Contains(RelayTools.StandingPut, administrator);
        Assert.Contains(RelayTools.StandingPropose, worker);
        Assert.DoesNotContain(RelayTools.StandingPut, worker);
        Assert.True(CoCern.Adapter.CoordinationTools.IsAllowed(RelayTools.StandingPut, admin: true));
        Assert.False(CoCern.Adapter.CoordinationTools.IsAllowed(RelayTools.StandingPut, admin: false));
        Assert.False(CoCern.Adapter.CoordinationTools.IsAllowed(RelayTools.StandingPropose, admin: false, chatOnly: true));
    }

    [Fact]
    public void Analytics_query_is_discovered_only_by_administrators()
    {
        Assert.Contains(RelayTools.AnalyticsQuery, CoCern.Adapter.CoordinationTools.NamesForRole(true));
        Assert.DoesNotContain(RelayTools.AnalyticsQuery, CoCern.Adapter.CoordinationTools.NamesForRole(false));
        Assert.True(CoCern.Adapter.CoordinationTools.IsAllowed(RelayTools.AnalyticsQuery, admin: true));
        Assert.False(CoCern.Adapter.CoordinationTools.IsAllowed(RelayTools.AnalyticsQuery, admin: false));
        Assert.False(CoCern.Adapter.CoordinationTools.IsAllowed(RelayTools.AnalyticsQuery, admin: true, chatOnly: true));
    }

    [Fact]
    public void Order_tools_are_discovered_only_by_administrators()
    {
        foreach (var name in new[] { RelayTools.OrderPut, RelayTools.OrderRemove })
        {
            Assert.Contains(name, CoCern.Adapter.CoordinationTools.NamesForRole(true));
            Assert.DoesNotContain(name, CoCern.Adapter.CoordinationTools.NamesForRole(false));
            Assert.True(CoCern.Adapter.CoordinationTools.IsAllowed(name, admin: true));
            Assert.False(CoCern.Adapter.CoordinationTools.IsAllowed(name, admin: false));
            Assert.False(CoCern.Adapter.CoordinationTools.IsAllowed(name, admin: true, chatOnly: true));
        }
    }

}
