using CoCern.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace CoCern.Tests;

// Run this existing-surface meter before any production catalog edits. It is
// source-only until the current exclusive gate ends; no guessed size is evidence.
public sealed class EventWaitCatalogBaselineTests(ITestOutputHelper output)
{
    [Fact]
    public void Existing_forwarding_catalog_cost_is_recorded_without_relaxing_its_caps()
    {
        foreach (var administrator in new[] { false, true })
        {
            var costs = RelayToolCatalog.ForRole(administrator).Select(tool => new
            {
                tool.Name, Description = tool.Description.Length,
                Schema = RelayToolCatalog.ForwardingSchema(tool).GetRawText().Length
            }).ToArray();
            var size = costs.Sum(tool => tool.Description + tool.Schema);
            output.WriteLine($"Existing {(administrator ? "administrator" : "worker")} forwarding characters: {size}; tools: {costs.Length}.");
            foreach (var tool in costs) output.WriteLine($"{tool.Name}: description={tool.Description}, schema={tool.Schema}");
            Assert.True(administrator ? size < 22_650 : size <= 18_968, $"Existing catalog size: {size}.");
        }
    }
}
