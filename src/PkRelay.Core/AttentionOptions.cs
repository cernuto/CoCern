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

    /// <summary>Whether boundary replies carry offers and items are stored and routed at all (<c>Relay:Attention:Enabled</c>, default on).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Minutes an idle chat waits with only lower-tier items before it gets one wake.</summary>
    public int MaxHoldMinutes { get; init; } = RelayLimits.DefaultAttentionMaxHoldMinutes;

    /// <summary>Refuses settings outside their limits with a message that names the setting.</summary>
    public AttentionOptions Validated() =>
        MaxHoldMinutes is < 1 or > RelayLimits.MaxAttentionMaxHoldMinutes ? throw new InvalidOperationException($"Relay:Attention:MaxHoldMinutes must be from 1 to {RelayLimits.MaxAttentionMaxHoldMinutes}.")
        : SnoozeBoundaries is < 1 or > RelayLimits.MaxAttentionSnoozeBoundaries ? throw new InvalidOperationException($"Relay:Attention:SnoozeBoundaries must be from 1 to {RelayLimits.MaxAttentionSnoozeBoundaries}.")
        : ItemExpiryDays is < 1 or > RelayLimits.MaxAttentionExpiryDays ? throw new InvalidOperationException($"Relay:Attention:ItemExpiryDays must be from 1 to {RelayLimits.MaxAttentionExpiryDays}.")
        : this;
}
