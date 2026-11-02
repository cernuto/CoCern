using CoCern.Contracts;

namespace CoCern.Core;

/// <summary>Host settings for task boards (<c>Relay:Board:*</c>). The board grain passes them to the domain.</summary>
public sealed record BoardOptions
{
    /// <summary>
    /// Days after completion before a finished, idle task moves to the archive (<c>Relay:Board:ArchiveAfterDays</c>, default 30). 0 archives
    /// only when a board nears its task limit.
    /// </summary>
    public int ArchiveAfterDays { get; init; } = RelayLimits.DefaultArchiveAfterDays;

    /// <summary>The clock boards judge operations and sweeps by; tests move it.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Refuses settings outside their limits with a message that names the setting.</summary>
    public BoardOptions Validated() =>
        ArchiveAfterDays is < 0 or > RelayLimits.MaxArchiveAfterDays ? throw new InvalidOperationException($"Relay:Board:ArchiveAfterDays must be from 0 to {RelayLimits.MaxArchiveAfterDays}.") : this;
}
