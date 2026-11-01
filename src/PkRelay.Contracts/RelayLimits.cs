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

    /// <summary>Largest search document body, in UTF-8 bytes, so one huge task cannot dominate the index or the commit.</summary>
    public const int MaxSearchBodyBytes = 4_096;
    /// <summary>Largest share of a search body one part (a note, a report, a message) may take, in UTF-8 bytes.</summary>
    public const int MaxSearchFieldBytes = 1_024;
    /// <summary>Largest search document title, in UTF-8 bytes.</summary>
    public const int MaxSearchTitleBytes = 256;
    /// <summary>Hits a search returns when it gives no <c>limit</c>.</summary>
    public const int DefaultSearchResults = 3;
    /// <summary>Most hits one search returns.</summary>
    public const int MaxSearchResults = 10;
    /// <summary>Largest search reply, in UTF-8 bytes; the weakest hits give way first.</summary>
    public const int MaxSearchReplyBytes = 2_048;
    /// <summary>Longest search query, in characters.</summary>
    public const int MaxSearchQueryLength = 256;
    /// <summary>Longest status filter on a search, in characters.</summary>
    public const int MaxSearchStatusLength = 64;
    /// <summary>Most words of a query that are searched; the rest are ignored.</summary>
    public const int MaxSearchQueryTerms = 16;
    /// <summary>Rows one search reads from the index before dropping weak and duplicate hits.</summary>
    public const int SearchCandidateRows = 40;
    /// <summary>A hit scoring less than this share of the best hit's score is dropped as too weak.</summary>
    public const double SearchMinRelativeScore = 0.25;

    /// <summary>Most open attention items one session keeps; when full, the lowest-tier oldest item is dropped.</summary>
    public const int MaxAttentionItems = 50;
    /// <summary>Longest attention note and offer text, in characters.</summary>
    public const int MaxAttentionTextLength = 200;
    /// <summary>Most related IDs one attention item carries.</summary>
    public const int MaxAttentionRefs = 8;
    /// <summary>Boundaries a "later" item stays hidden when no time is given, unless <c>Relay:Attention:SnoozeBoundaries</c> says otherwise.</summary>
    public const int DefaultAttentionSnoozeBoundaries = 3;
    /// <summary>Largest allowed <c>Relay:Attention:SnoozeBoundaries</c>.</summary>
    public const int MaxAttentionSnoozeBoundaries = 50;
    /// <summary>Days an item stays open without being closed, unless <c>Relay:Attention:ItemExpiryDays</c> says otherwise.</summary>
    public const int DefaultAttentionExpiryDays = 7;
    /// <summary>Largest allowed item lifetime, in days.</summary>
    public const int MaxAttentionExpiryDays = 90;
    /// <summary>Most times an item that blocks another session may be snoozed.</summary>
    public const int MaxBlockingSnoozes = 2;
    /// <summary>Longest an item that blocks another session may stay snoozed, counted from its first snooze.</summary>
    public const int MaxBlockingSnoozeMinutes = 30;
    /// <summary>Closed item keys a session remembers with their source revision, so a late, older raise cannot reopen them.</summary>
    public const int MaxAttentionClosedKeys = 100;
    /// <summary>How long a closed item's content is remembered, so the same raise delivered again without a revision cannot reopen it.</summary>
    public const int AttentionTombstoneMinutes = 10;
    /// <summary>Most recently closed items one session remembers by content.</summary>
    public const int MaxAttentionTombstones = 100;
    /// <summary>Longest blocker kind an attention item records.</summary>
    public const int MaxAttentionBlockerLength = 64;
    /// <summary>Largest offer a boundary reply carries, in UTF-8 bytes; the offer is skipped when the reply has less room left.</summary>
    public const int MaxAttentionOfferBytes = 2_048;
    /// <summary>Minutes an idle chat waits with lower-tier items before one wake, unless <c>Relay:Attention:MaxHoldMinutes</c> says otherwise.</summary>
    public const int DefaultAttentionMaxHoldMinutes = 10;
    /// <summary>Largest allowed <c>Relay:Attention:MaxHoldMinutes</c>.</summary>
    public const int MaxAttentionMaxHoldMinutes = 1_440;
    /// <summary>Most sessions with one role (such as manager) that a routed attention item reaches, newest first.</summary>
    public const int MaxAttentionRouteTargets = 16;
    /// <summary>Seconds before a lease ends, with an action in flight, when the holder gets a <c>claim_expiring</c> item.</summary>
    public const int ClaimExpiringLeadSeconds = 60;
    /// <summary>Most task IDs a <c>related_work</c> or <c>duplicate_warning</c> item lists after a claim.</summary>
    public const int MaxRelatedWorkHits = 3;
    /// <summary>
    /// Least relevance (the negated bm25 score, title weighted 4) a look-alike task needs before a claim raises <c>related_work</c> or
    /// <c>duplicate_warning</c> for it; every word must match as well.
    /// </summary>
    public const double RelatedWorkMinScore = 1.0;
    /// <summary>Days a <c>next_work</c> item raised from a next-work offer stays open if no claim closes it.</summary>
    public const int OfferedWorkExpiryDays = 1;
    /// <summary>Hours a session may go without freshness or activity before it no longer receives routed (manager) attention items.</summary>
    public const int MaxAttentionRouteIdleHours = 24;
    /// <summary>Least minutes between a session's reports to the control directory that it is still active (made at boundary replies).</summary>
    public const int AttentionActivityReportMinutes = 60;
    /// <summary>Sessions whose last idle-hold wake the hub remembers for counting; past this the memory is cleared.</summary>
    public const int MaxHoldWakeSessions = 1_024;
    /// <summary>Seconds the prompt hook reuses its last answer.</summary>
    public const int PromptHookCacheSeconds = 30;
    /// <summary>The prompt hook's hub timeout, in milliseconds, unless <c>PKRELAY_HOOK_TIMEOUT_MS</c> says otherwise.</summary>
    public const int DefaultPromptHookTimeoutMs = 1_000;
    /// <summary>Largest allowed <c>PKRELAY_HOOK_TIMEOUT_MS</c>.</summary>
    public const int MaxPromptHookTimeoutMs = 10_000;
}
