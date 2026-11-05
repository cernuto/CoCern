using System.Text.Json;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Host;
using Xunit;

namespace CoCern.Tests;

public sealed class ContractsTests
{

    [Fact]
    public void Host_and_adapter_advertise_one_catalog()
    {
        Assert.Equal(RelayToolCatalog.ForRole(false).Select(tool => tool.Name), ToolResults.Tools(false).Select(tool => tool.Name));
        Assert.Equal(RelayToolCatalog.ForRole(true).Select(tool => tool.Name), CoCern.Adapter.CoordinationTools.NamesForRole(true));
        Assert.DoesNotContain(RelayTools.ControlRequest, ToolResults.Tools(false).Select(tool => tool.Name));
    }

}
