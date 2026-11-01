using CoCern.Contracts;

namespace CoCern.Core;

/// <summary>
/// Host settings for token counting (<c>Relay:Tokens:Enabled</c>, on by default) and budgets (<c>Relay:Tokens:EnforceTools</c>, empty by
/// default). Counting only measures: replies are unchanged unless a tool is listed in <see cref="EnforceTools"/>, which compacts its
/// replies to their budget. The doc cache tools keep their own token budget and cannot be listed.
/// </summary>
public sealed record TokenOptions
{
    public bool Enabled { get; init; } = true;

    /// <summary>Tools whose replies are compacted to their budget. Every other tool is measure-only: overruns are counted, not enforced.</summary>
    public IReadOnlySet<string> EnforceTools { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public bool Enforces(string tool) => Enabled && EnforceTools.Contains(tool);

    /// <summary>Refuses unknown tools and the doc cache tools with a message that names the setting.</summary>
    public TokenOptions Validated()
    {
        foreach (var tool in EnforceTools)
        {
            if (!RelayToolCatalog.TryGet(tool, out _)) throw new InvalidOperationException($"Relay:Tokens:EnforceTools names an unknown tool: {tool}.");
            if (tool is RelayTools.DocSearch or RelayTools.DocSection or RelayTools.DocSync)
                throw new InvalidOperationException($"Relay:Tokens:EnforceTools cannot list {tool}: the doc cache keeps its own token budget.");
        }
        return this;
    }
}
