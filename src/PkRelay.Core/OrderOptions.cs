using PkRelay.Contracts;

namespace PkRelay.Core;

/// <summary>
/// Host settings for standing orders (<c>Relay:Orders:*</c>). Off by default. The board grain passes <see cref="Enabled"/> to the domain,
/// and the dispatcher drops order effects while it is off, so orders written earlier never run.
/// </summary>
public sealed record OrderOptions
{
    /// <summary>Whether <c>order_put</c> and <c>order_remove</c> work and orders run (<c>Relay:Orders:Enabled</c>, default off).</summary>
    public bool Enabled { get; init; }

    /// <summary>Days an order lives when it gives no <c>expiresAt</c> (<c>Relay:Orders:DefaultExpiryDays</c>).</summary>
    public int DefaultExpiryDays { get; init; } = RelayLimits.DefaultOrderExpiryDays;

    /// <summary>Refuses settings outside their limits with a message that names the setting.</summary>
    public OrderOptions Validated() =>
        DefaultExpiryDays is < 1 or > RelayLimits.MaxOrderExpiryDays ? throw new InvalidOperationException($"Relay:Orders:DefaultExpiryDays must be from 1 to {RelayLimits.MaxOrderExpiryDays}.") : this;
}
