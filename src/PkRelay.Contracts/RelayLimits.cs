namespace PkRelay.Contracts;

/// <summary>Wire and record bounds shared by the hub, its domain and its clients.</summary>
public static class RelayLimits
{
    /// <summary>Largest public request or reply envelope, in UTF-8 bytes.</summary>
    public const int MaxPayloadBytes = 32_768;

    /// <summary>Room an aggregate reply leaves for application composition (session attachments, convenience outcomes).</summary>
    public const int CompositionReserveBytes = 8_192;

    /// <summary>Largest reply a single aggregate may produce before composition.</summary>
    public const int AggregateReplyBytes = MaxPayloadBytes - CompositionReserveBytes;

    public const int MaxIdentifierLength = 256;
    public const int MaxPageSize = 50;
    public const int DefaultPageSize = 20;
    /// <summary>A whole-task read carries only this many of the latest messages, reports and attempts; older ones stay in the paged sections.</summary>
    public const int RecentTaskEntries = 20;
    public const int MaxRecords = 2_048;
    public const int MaxTaskBytes = 262_144;
    public const int MaxReportBytes = 8_192;
    public const int MaxClaimResources = 16;
    public const int MaxAcknowledgedMessages = 50;
    public const int MaxLeaseSeconds = 600;
    public const int DefaultLeaseSeconds = 120;
}
