using CoCern.Contracts;

namespace CoCern.Core;

/// <summary>Host settings for session cleanup (<c>Relay:Sessions:*</c>). The hub's sweep service passes them to the control directory.</summary>
public sealed record SessionCleanupOptions
{
    /// <summary>Hours without being seen before the sweep closes a session (<c>Relay:Sessions:IdleHours</c>, default 24).</summary>
    public int IdleHours { get; init; } = RelayLimits.DefaultSessionIdleHours;

    /// <summary>Refuses settings outside their limits with a message that names the setting.</summary>
    public SessionCleanupOptions Validated() =>
        IdleHours is < RelayLimits.MinSessionIdleHours or > RelayLimits.MaxSessionIdleHours
            ? throw new InvalidOperationException($"Relay:Sessions:IdleHours must be from {RelayLimits.MinSessionIdleHours} to {RelayLimits.MaxSessionIdleHours}.") : this;
}
