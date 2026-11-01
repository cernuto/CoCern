using CoCern.Contracts;

namespace CoCern.Core;

/// <summary>
/// Host settings for token counting (<c>Relay:Tokens:Enabled</c>, on by default) and budgets. By default every tool is enforced
/// (<see cref="AllEnforceable"/>): a reply over its tool's budget is compacted. Each budget sits at what the tool's byte cap allows
/// (<see cref="RelayLimits.PayloadReplyTokens"/>), so in practice only a reply that would otherwise be cut at the byte limit is compacted.
/// The doc cache tools keep their own token budget and are never listed.
/// </summary>
public sealed record TokenOptions
{
    /// <summary>Every public tool except the doc cache's, which bound their own replies to <see cref="RelayLimits.MaxDocReplyTokens"/>.</summary>
    public static IReadOnlySet<string> AllEnforceable { get; } = RelayToolCatalog.All.Select(tool => tool.Name).Where(name => !IsDocTool(name)).ToHashSet(StringComparer.Ordinal);

    public bool Enabled { get; init; } = true;

    /// <summary>Tools whose replies are compacted to their budget. Any other tool is measure-only: overruns are counted, not enforced.</summary>
    public IReadOnlySet<string> EnforceTools { get; init; } = AllEnforceable;

    public bool Enforces(string tool) => Enabled && EnforceTools.Contains(tool);

    /// <summary>Refuses unknown tools and the doc cache tools with a message that names the setting.</summary>
    public TokenOptions Validated()
    {
        foreach (var tool in EnforceTools)
        {
            if (!RelayToolCatalog.TryGet(tool, out _)) throw new InvalidOperationException($"Relay:Tokens:EnforceTools names an unknown tool: {tool}.");
            if (IsDocTool(tool))
                throw new InvalidOperationException($"Relay:Tokens:EnforceTools cannot list {tool}: the doc cache keeps its own token budget.");
        }
        return this;
    }

    private static bool IsDocTool(string tool) => tool is RelayTools.DocSearch or RelayTools.DocSection or RelayTools.DocSync;
}
