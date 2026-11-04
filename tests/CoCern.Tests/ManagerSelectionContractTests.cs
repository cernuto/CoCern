using System.Text.Json;
using CoCern.Contracts;
using Xunit;

namespace CoCern.Tests;

public sealed class ManagerSelectionContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Selection_commands_and_typed_fields_are_advertised(bool forwarding)
    {
        var tool = RelayToolCatalog.All.Single(x => x.Name == RelayTools.ControlRequest);
        var schema = forwarding ? RelayToolCatalog.ForwardingSchema(tool) : RelayToolCatalog.InputSchema(tool);
        var properties = schema.GetProperty("properties");
        var commands = properties.GetProperty("command").GetProperty("enum").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Contains("appoint_manager", commands); Assert.Contains("clear_manager", commands);
        Assert.Equal("string", properties.GetProperty("targetSessionId").GetProperty("type").GetString());
        Assert.Equal("integer", properties.GetProperty("expectedManagerGeneration").GetProperty("type").GetString());
        Assert.Equal("string", properties.GetProperty("humanWords").GetProperty("type").GetString());
        Assert.DoesNotContain(RelayToolCatalog.ForRole(false), x => x.Name == RelayTools.ControlRequest);
        Assert.DoesNotContain(CoCern.Adapter.CoordinationTools.NamesForRole(false), x => x == RelayTools.ControlRequest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Status_advertises_typed_manager_history_cursor(bool forwarding)
    {
        var tool = RelayToolCatalog.All.Single(x => x.Name == RelayTools.GetStatus);
        var properties = (forwarding ? RelayToolCatalog.ForwardingSchema(tool) : RelayToolCatalog.InputSchema(tool)).GetProperty("properties");
        Assert.Equal("integer", properties.GetProperty("managerHistoryBefore").GetProperty("type").GetString());
    }
}
