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
    /// <summary>Thirty minutes, so a chat holding a claim through a long turn renews rarely: each renewal is a model call over its whole context.</summary>
    public const int MaxLeaseSeconds = 1800;
    public const int DefaultLeaseSeconds = 120;

    /// <summary>Activity events waiting for the background writer. When full, new events are dropped; a call never waits.</summary>
    public const int ActivityQueueCapacity = 4_096;
    /// <summary>Most activity rows inserted in one short transaction.</summary>
    public const int ActivityBatchSize = 512;
    /// <summary>How long the activity writer collects events before inserting a batch.</summary>
    public const int ActivityBatchMilliseconds = 1_000;
    /// <summary>How often raw activity older than the retention period is rolled into daily totals.</summary>
    public const int ActivityRollupHours = 24;
    /// <summary>How long shutdown waits for the activity writer to drain its queue.</summary>
    public const int ActivityStopWaitSeconds = 5;
    /// <summary>Longest session role recorded with activity; longer roles are left out.</summary>
    public const int MaxActivityRoleLength = 64;
    /// <summary>A gap this long between one session's calls starts a new counted burst: a manager turn, or an inbox read.</summary>
    public const int ActivityTurnGapSeconds = 60;
    /// <summary>Largest token count accepted in one <c>usage</c> report field.</summary>
    public const long MaxReportedTokens = 1_000_000_000;

    /// <summary>Most task IDs one workflow may list in <c>dependsOn</c>.</summary>
    public const int MaxDependencies = 16;
    /// <summary>Offerable tasks a next-work offer checks against current resource claims.</summary>
    public const int NextWorkCandidates = 5;
    /// <summary>Most resource claim lookups one next-work offer makes.</summary>
    public const int MaxOfferResourceLookups = 32;

    /// <summary>Most standing lines one board keeps.</summary>
    public const int MaxStandingLines = 30;
    /// <summary>Longest standing line, in characters.</summary>
    public const int MaxStandingLineLength = 200;
    /// <summary>Most authority grants one board keeps.</summary>
    public const int MaxStandingGrants = 30;
    /// <summary>Most repository, resource or action targets one grant names.</summary>
    public const int MaxGrantTargets = 16;
    /// <summary>Longest standing line, grant or proposal ID.</summary>
    public const int MaxStandingIdLength = 64;
    /// <summary>Most proposals one board keeps waiting for an administrator.</summary>
    public const int MaxStandingProposals = 20;
    /// <summary>Most proposals one session may have waiting at once.</summary>
    public const int MaxStandingProposalsPerSession = 5;
    /// <summary>Changes listed in one history entry; the entry keeps the total when there are more.</summary>
    public const int StandingHistoryChanges = 20;
    /// <summary>Standing changes the board keeps in its revision history; older ones are dropped.</summary>
    public const int StandingHistoryEntries = 20;
    /// <summary>Largest standing text sent to a chat (lines, active grants and settings), in UTF-8 bytes.</summary>
    public const int MaxStandingBytes = 12_288;
    /// <summary>How long the hub reuses a board's standing read before reading it again.</summary>
    public const int StandingCacheSeconds = 5;
    /// <summary>Most boards whose standing the hub keeps cached; past this the cache starts over.</summary>
    public const int StandingCacheBoards = 1_024;
    /// <summary>Most boards one session records a received standing revision for.</summary>
    public const int MaxStandingBoardsPerSession = 64;
}
