using PkRelay.Contracts;

namespace PkRelay.Core;

/// <summary>
/// Host settings for attention items (<c>Relay:Attention:*</c>). The hub passes them to the session aggregate as internal arguments,
/// so the domain stays free of configuration.
/// </summary>
public sealed record AttentionOptions
{
    /// <summary>Boundaries a "later" item stays hidden when no time is given.</summary>
    public int SnoozeBoundaries { get; init; } = RelayLimits.DefaultAttentionSnoozeBoundaries;

    /// <summary>Days an item stays open when nothing closes it: a safety net for a missed close.</summary>
    public int ItemExpiryDays { get; init; } = RelayLimits.DefaultAttentionExpiryDays;

    /// <summary>Refuses settings outside their limits with a message that names the setting.</summary>
    public AttentionOptions Validated() =>
        SnoozeBoundaries is < 1 or > RelayLimits.MaxAttentionSnoozeBoundaries ? throw new InvalidOperationException($"Relay:Attention:SnoozeBoundaries must be from 1 to {RelayLimits.MaxAttentionSnoozeBoundaries}.")
        : ItemExpiryDays is < 1 or > RelayLimits.MaxAttentionExpiryDays ? throw new InvalidOperationException($"Relay:Attention:ItemExpiryDays must be from 1 to {RelayLimits.MaxAttentionExpiryDays}.")
        : this;
}
