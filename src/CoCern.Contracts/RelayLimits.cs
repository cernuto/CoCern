namespace CoCern.Contracts;

/// <summary>Wire and record bounds shared by the hub, its domain and its clients.</summary>
public static class RelayLimits
{
    /// <summary>Largest public request or reply envelope, in UTF-8 bytes.</summary>
    public const int MaxPayloadBytes = 32_768;

    /// <summary>Room an aggregate reply leaves for application composition (session attachments, convenience outcomes).</summary>
    public const int CompositionReserveBytes = 8_192;

    /// <summary>Largest reply a single aggregate may produce before composition.</summary>
    public const int AggregateReplyBytes = MaxPayloadBytes - CompositionReserveBytes;

    // Token budgets (o200k). A reply over its budget is compacted (every tool but the doc cache's). Budgets sit at the byte cap, so in
    // practice only a reply that would be cut anyway is compacted; the byte limits stay the hard safety net.
    /// <summary>Bytes per token for turning a byte cap into a budget; real JSON runs nearer 4, so budgets round up.</summary>
    public const int BudgetBytesPerToken = 3;
    /// <summary>What the whole reply envelope holds, in tokens; inner caps (search hits, analytics rows) bound only part of a reply.</summary>
    public const int PayloadReplyTokens = (MaxPayloadBytes + BudgetBytesPerToken - 1) / BudgetBytesPerToken;
    /// <summary>Reply budget of a tool without its own, in tokens.</summary>
    public const int DefaultReplyTokens = PayloadReplyTokens;
    /// <summary>Budget of a <c>search</c> reply; its hits have a smaller cap, but the composed reply only the envelope's.</summary>
    public const int SearchReplyTokens = PayloadReplyTokens;
    /// <summary>Budget of a whole-task or board <c>task_get</c> reply.</summary>
    public const int TaskReadReplyTokens = PayloadReplyTokens;
    /// <summary>Budget of one <c>check_inbox</c> page.</summary>
    public const int InboxReplyTokens = PayloadReplyTokens;
    /// <summary>Budget of the attention offer a boundary reply carries.</summary>
    public const int AttentionOfferTokens = 60;
    /// <summary>Budget of the next-work offer a <c>task_update</c> reply carries.</summary>
    public const int NextWorkOfferTokens = 400;
    /// <summary>Compaction step 3 shortens text values longer than this, in characters, to their first line.</summary>
    public const int CompactTextChars = 200;
    /// <summary>Text queued activity events may hold for token counting (about 16 MB). Past it events are recorded uncounted.</summary>
    public const long MaxQueuedTokenTextChars = 8_000_000;
    /// <summary>Longest etag the adapter forwards as <c>knownEtag</c> and the hub reads; the adapter's etags are 16 characters.</summary>
    public const int MaxKnownEtagLength = 64;

    // Feedback rules (tips to agents, proposed team rules).
    /// <summary>Most tips one session gets in an hour.</summary>
    public const int TipsPerSessionPerHour = 1;
    /// <summary>Hours before the same rule may tip the same session again.</summary>
    public const int TipRuleCooldownHours = 24;
    /// <summary>Every tip is shorter than this, in tokens.</summary>
    public const int MaxTipTokens = 40;
    /// <summary>Minutes after a tip in which a repeat of its pattern counts as not followed.</summary>
    public const int TipOutcomeMinutes = 60;
    /// <summary>Recorded tip outcomes a rule needs before a low follow rate can switch it off.</summary>
    public const int MinTipOutcomes = 20;
    /// <summary>Follow rate below which a tip rule switches itself off, unless <c>Relay:Feedback:MinFollowRate</c> says otherwise.</summary>
    public const double DefaultMinFollowRate = 0.2;
    /// <summary>Board reads without <c>sinceRevision</c> in an hour that fire the board reread rule.</summary>
    public const int BoardRereadsPerHour = 3;
    /// <summary>Full reads of one task in <see cref="TaskRereadMinutes"/> that fire the task reread rule.</summary>
    public const int TaskRereads = 4;
    public const int TaskRereadMinutes = 30;
    /// <summary>Messages averaged by the long message rule, and the average, in tokens, that fires it.</summary>
    public const int LongMessageWindow = 5;
    public const int LongMessageTokens = 150;
    /// <summary>Model-visible <c>check_inbox</c> reads with nothing new in an hour that fire the inbox polling rule.</summary>
    public const int InboxPollsPerHour = 6;
    /// <summary>Different sessions one rule must fire for, within <see cref="TeamRuleDays"/>, before the hub proposes its standing line.</summary>
    public const int TeamRuleSessions = 5;
    public const int TeamRuleDays = 7;
    /// <summary>Sessions whose feedback counts the hub keeps in memory; past it the oldest are forgotten.</summary>
    public const int MaxFeedbackSessions = 4_096;
    /// <summary>Message IDs per session the inbox polling rule remembers as already seen.</summary>
    public const int MaxFeedbackSeenMessages = 200;
    /// <summary>Targets whose last status fingerprint the repeated status rule remembers.</summary>
    public const int MaxFeedbackTargets = 4_096;
    /// <summary>Days a tip attention item lives before it expires unoffered.</summary>
    public const int TipExpiryDays = 1;

    // Manager alerts: named queries the anomaly monitor runs with Relay:Feedback:Alerts on.
    /// <summary>A role's relay tokens per task are an alert at this many times their baseline, from at least <see cref="TokensPerTaskAlertSamples"/> tasks.</summary>
    public const double TokensPerTaskAlertRatio = 2.0;
    public const int TokensPerTaskAlertSamples = 20;
    /// <summary>A tool compacting more than this share of its replies is an alert, from at least <see cref="CompactionAlertSamples"/> replies.</summary>
    public const double CompactionAlertRate = 0.3;
    public const int CompactionAlertSamples = 20;
    /// <summary>A session's relay tokens in one hour are an alert at this many times its role's median session-hour, from at least <see cref="SessionOutlierAlertSamples"/> session-hours.</summary>
    public const double SessionOutlierAlertRatio = 5.0;
    public const int SessionOutlierAlertSamples = 10;

    public const int MaxIdentifierLength = 256;
    public const int MaxManagerHumanWordsLength = 200;
    public const int ManagerSelectionHistoryEntries = 20;
    public const int ManagerSelectionHistoryPageEntries = 2;
    /// <summary>Former managers the hub still refuses to take administrative calls from; past this the oldest revocation is forgotten.</summary>
    public const int ManagerRevokedEntries = 64;
    /// <summary>Administrative actions kept per repository; the count dropped is reported beside them. Only a repository's own actions push its entries out.</summary>
    public const int AdminActionEntries = 100;
    /// <summary>The control document's ceiling across repositories, reached only with more than four repositories busy at once.</summary>
    public const int AdminActionTotalEntries = 400;
    /// <summary>Past this many repositories with a sequence, those with no entry left are forgotten.</summary>
    public const int AdminActionSequences = 256;
    public const int AdminActionPageEntries = 20;
    public const int MaxPageSize = 50;
    /// <summary>How many chats a refused ambiguous name lists; the reply says when there are more.</summary>
    public const int MaxPeerChoices = 10;
    public const int DefaultPageSize = 20;
    /// <summary>A whole-task read carries only this many of the latest messages, reports and attempts; older ones stay in the paged sections.</summary>
    public const int RecentTaskEntries = 20;
    public const int MaxRecords = 2_048;
    /// <summary>A machine not seen (announced) for this many days may be removed to make room when the machine table is full.</summary>
    public const int MachineExpiryDays = 30;
    /// <summary>Days after completion before a finished task moves to the archive (<c>Relay:Board:ArchiveAfterDays</c>; 0: only when the board fills).</summary>
    public const int DefaultArchiveAfterDays = 30;
    public const int MaxArchiveAfterDays = 3_650;
    /// <summary>A board write with this many tasks archives finished ones, oldest first, down to <see cref="ArchiveLowWater"/>, even if younger than the setting.</summary>
    public const int ArchiveHighWater = MaxRecords * 7 / 8;
    public const int ArchiveLowWater = MaxRecords * 3 / 4;
    /// <summary>Even near the cap, a task is archived no sooner than this after it completed, so a fresh result stays on the board for review.</summary>
    public const int MinArchiveAgeHours = 24;
    /// <summary>
    /// The board remembers recently archived IDs (for deltas and quick refusals) for at most this many IDs or <see cref="ArchivedIdDays"/>,
    /// whichever is fewer, plus any a live task's <c>dependsOn</c> still names. Older IDs resolve through the archive table.
    /// </summary>
    public const int MaxArchivedIds = MaxRecords;
    public const int ArchivedIdDays = 30;
    /// <summary>A board's archive sweep that fails waits this long before it runs again.</summary>
    public const int ArchiveRetryMinutes = 60;
    public const int MaxTaskBytes = 262_144;
    public const int MaxReportBytes = 8_192;
    public const int MaxClaimResources = 16;
    public const int MaxAcknowledgedMessages = 50;
    /// <summary>Thirty minutes, so a chat holding a claim through a long turn renews rarely: each renewal is a model call over its whole context.</summary>
    public const int MaxLeaseSeconds = 1800;
    public const int DefaultLeaseSeconds = 120;

    /// <summary>
    /// How long an aggregate remembers a request (its stored reply), an acknowledged message and a resolved action. A retry within this
    /// window replays or is refused; after it, the request runs as new.
    /// </summary>
    public const int RequestReplayRetentionHours = 24;
    /// <summary>
    /// When one of those maps reaches <see cref="MaxRecords"/>, it drops entries past the window and then its oldest down to this many,
    /// so the prune runs at most once per 512 writes instead of scanning on every one.
    /// </summary>
    public const int RetainedEntriesAfterPrune = MaxRecords * 3 / 4;
    /// <summary>
    /// Hashed keys of replays evicted before their window ended, so their retries get <c>RequestExpired</c> instead of running again. Each
    /// costs about 34 bytes in every parse and save of its aggregate, so 4 × <see cref="MaxRecords"/> (about 280 KB when full) rather than more.
    /// </summary>
    public const int MaxRequestTombstones = 4 * MaxRecords;
    /// <summary>When the tombstones pass <see cref="MaxRequestTombstones"/>, the oldest are dropped down to this many.</summary>
    public const int RequestTombstonesAfterPrune = MaxRequestTombstones * 3 / 4;

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

    /// <summary>Most standing orders one board keeps, disabled ones included; expired ones are dropped at the next <c>order_put</c>.</summary>
    public const int MaxOrders = 20;
    /// <summary>Most actions one standing order runs when its trigger fires.</summary>
    public const int MaxOrderActions = 4;
    /// <summary>Longest note a <c>notify</c> action sends, in characters.</summary>
    public const int MaxOrderNoteLength = 200;
    /// <summary>Failed runs after which a standing order is disabled.</summary>
    public const int MaxOrderFailures = 3;
    /// <summary>Days an order lives when it gives no <c>expiresAt</c>, unless <c>Relay:Orders:DefaultExpiryDays</c> says otherwise.</summary>
    public const int DefaultOrderExpiryDays = 7;
    /// <summary>Longest order lifetime, in days.</summary>
    public const int MaxOrderExpiryDays = 30;
    /// <summary>Order changes, runs and failures one board keeps in its order log; older entries are dropped.</summary>
    public const int OrderLogEntries = 20;
    /// <summary>Most boards one resource lock tells when its claim is released, for <c>claim_released</c> orders.</summary>
    public const int MaxOrderWatchBoards = 16;
    /// <summary>Order releases one lock remembers, so a redelivered release is never applied twice.</summary>
    public const int OrderReleasesRemembered = 32;

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
    /// <summary>Longest <c>ack</c> <c>waitingFor</c>, and longest text a "Waiting on you" row shows.</summary>
    public const int MaxWaitingTextLength = 200;
    /// <summary>An <c>ack</c> field saying its <c>waitingFor</c> comes from one of the chat's own client hooks, not the model. Such a call neither replaces nor clears a report the model made. It only restricts its caller, so a client sending it gains nothing.</summary>
    public const string HookWaitingField = "hookReport";
    /// <summary>
    /// An <c>ack</c> field for a hook's call only: on a report, the ID stored with it; on a clear, the report it means to clear. A clear that lands late therefore cannot erase a later report.
    /// It comes from the hook's own marker, is not a secret and grants nothing.
    /// </summary>
    public const string HookReportIdField = "hookReportId";
    /// <summary>Hours a chat's <c>waitingFor</c> lasts from its latest report.</summary>
    public const int WaitingForHours = 12;
    /// <summary>Hours renewals can carry one <c>waitingFor</c> from when it was first set; it survives a night and still ends.</summary>
    public const int WaitingForMaxHours = 24;
    /// <summary>Days a blocked task stays on "Waiting on you" from when it took the blocker, though the task stays blocked.</summary>
    public const int WaitingBlockerDays = DefaultAttentionExpiryDays;
    /// <summary>Minutes direct mail must have waited, and the chat's own last hub call have passed, before "Waiting on you" calls it unresponsive.</summary>
    public const int DefaultUnresponsiveMinutes = 10;
    /// <summary>Hours after a chat's last hub call of its own that it stops counting, so abandoned chats do not fill the list.</summary>
    public const int UnresponsiveActiveHours = 24;
    /// <summary>Most rows the dashboard's snapshot carries; <c>Count</c> stays the true total observed.</summary>
    public const int MaxWaitingItems = 50;
    /// <summary>Most coordinator holds the snapshot carries in its "maybe" group.</summary>
    public const int MaxWaitingMaybeItems = 20;
    /// <summary>Rows an administrator's <c>get_status</c> shows of the list.</summary>
    public const int WaitingStatusItems = 10;
    /// <summary>Rows an administrator's <c>get_status</c> shows of the "maybe" group.</summary>
    public const int WaitingStatusMaybeItems = 5;
    /// <summary>Most times an item that blocks another session may be snoozed.</summary>
    public const int MaxBlockingSnoozes = 2;
    /// <summary>Longest an item that blocks another session may stay snoozed, counted from its first snooze.</summary>
    public const int MaxBlockingSnoozeMinutes = 30;
    /// <summary>Closed item keys a session remembers with their source revision, so a late, older raise cannot reopen them.</summary>
    public const int MaxAttentionClosedKeys = 100;
    /// <summary>Archived tasks a session remembers with the archive revision, so a late, older raise for one never reopens it.</summary>
    public const int MaxAttentionArchivedTasks = 256;
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
    /// <summary>
    /// A session not seen (open, bound or active) for this long may be dropped from the hub's session directory once it is full, and is
    /// told so: its chat then gets <c>SessionUnknown</c> with <c>expired</c> and opens a new session. At least the attention route's idle limit.
    /// </summary>
    public const int StaleSessionHours = MaxAttentionRouteIdleHours;
    /// <summary>Hours without being seen before the hub's periodic sweep closes a session (<c>Relay:Sessions:IdleHours</c>), and its limits.</summary>
    public const int DefaultSessionIdleHours = 24;
    public const int MinSessionIdleHours = 2;
    public const int MaxSessionIdleHours = 720;
    /// <summary>Least <c>idleMinutes</c> a <c>close_sessions</c> filter takes, so a bulk close never catches a chat that is merely between turns.</summary>
    public const int MinCloseSessionsIdleMinutes = 60;
    /// <summary>
    /// Sessions one sweep or <c>close_sessions</c> closes at most, so their expiry notices fit the outbox; the reply then says <c>more</c>,
    /// and the rest close on the next run.
    /// </summary>
    public const int MaxSessionsClosedPerCall = MaxRecords / 4;
    /// <summary>
    /// Days a session kept open for unread mail stays exempt from idle closes (the sweep, <c>close_sessions</c> with <c>idleMinutes</c>)
    /// while it is not seen again. Past this an idle close takes it, mail and all, so chats that ended with mail waiting never fill the
    /// directory. A session seen again loses the exemption, and its next idle close asks it once more.
    /// </summary>
    public const int SessionMailHoldDays = 7;
    /// <summary>Most boards a session tells when it closes because one of their tasks targets it; past this the oldest is forgotten.</summary>
    public const int MaxSessionTargetBoards = 64;
    /// <summary>
    /// Minutes after which a close the session has not answered is taken as lost. An answer is retried until it lands and normally does
    /// within seconds; until this passes, a report from the session (whose entry the close removed) waits for that answer instead of
    /// closing the session again, since the answer may bring it back.
    /// </summary>
    public const int LostCloseAnswerMinutes = 30;
    /// <summary>
    /// Longest field name the log line for a malformed outbox effect shows. A longer one, or one with anything but letters and dots, shows
    /// as <c>?</c>: every message field name passes, and no secret, ID or time can pass for one.
    /// </summary>
    public const int MaxLoggedFieldNameLength = 40;
    /// <summary>
    /// A close by label alone skips chats working right now: those that made a call within this many minutes. Activity reaches the directory
    /// about hourly, so the cutoff allows for that (see ControlAggregate.IdleCutoff) and may also skip chats quiet for up to about an hour
    /// longer; Close session closes one of those on purpose.
    /// </summary>
    public const int LabelCloseActiveMinutes = 10;
    /// <summary><c>connect_app</c> requests this hub remembers, so a retry gets the first reply and does not run setup again.</summary>
    public const int RememberedConnectRequests = 64;
    /// <summary>Room an administrator tool's reply leaves under <see cref="MaxPayloadBytes"/> when it trims itself to fit, for what the hub adds on the way out.</summary>
    public const int AdminReplyRoomBytes = 1_024;
    /// <summary>Unread messages a closing session returns to their senders as <c>delivery.failed</c>; the rest are dropped with it.</summary>
    public const int MaxMessagesReturnedOnClose = 64;
    /// <summary>Closed session IDs a <c>close_sessions</c> reply lists; <c>closed</c> still counts them all.</summary>
    public const int MaxClosedSessionIds = MaxPageSize;
    /// <summary>
    /// Unresolved actions the session directory remembers per session (so it never expires a session that still answers for one). Past
    /// this, the session is marked, and stays exempt until no new action has overflowed it for <see cref="SessionWorkRetentionDays"/>.
    /// </summary>
    public const int MaxSessionOpenActions = 64;
    /// <summary>Closed actions the session directory remembers per session, so a late, reordered open for one is ignored.</summary>
    public const int MaxSessionClosedActions = 2 * MaxSessionOpenActions;
    /// <summary>
    /// Days a tracked open action (or closed tombstone, or overflow mark) counts without a new report from its board. Past this the
    /// directory forgets it, so a lost or reordered report never protects a session from expiry for good.
    /// </summary>
    public const int SessionWorkRetentionDays = 7;
    /// <summary>
    /// Least hours between a board's refreshes of the directory's record for an action that stays unresolved with the same session; well
    /// inside <see cref="SessionWorkRetentionDays"/>, so a reported long-running action never lapses.
    /// </summary>
    public const int SessionWorkRefreshHours = 24;
    /// <summary>
    /// Least minutes between a session's reports to the control directory that it is still active (made on its verified calls, with or
    /// without attention). The directory's view of a working session can therefore lag its last call by up to this long, so every idle
    /// judgment (the sweep, <c>close_sessions</c>, a full directory) waits this much longer than the idle time it is given.
    /// </summary>
    public const int AttentionActivityReportMinutes = 60;
    /// <summary>Sessions whose last idle-hold wake the hub remembers for counting; past this the memory is cleared.</summary>
    public const int MaxHoldWakeSessions = 1_024;
    /// <summary>Seconds the prompt hook reuses its last answer.</summary>
    public const int PromptHookCacheSeconds = 30;
    /// <summary>The prompt hook's hub timeout, in milliseconds, unless <c>COCERN_HOOK_TIMEOUT_MS</c> says otherwise.</summary>
    public const int DefaultPromptHookTimeoutMs = 1_000;
    /// <summary>Largest allowed <c>COCERN_HOOK_TIMEOUT_MS</c>.</summary>
    public const int MaxPromptHookTimeoutMs = 10_000;

    /// <summary>Minutes between analytics snapshots, unless <c>Relay:Analytics:SnapshotInterval</c> says otherwise.</summary>
    public const int DefaultAnalyticsSnapshotMinutes = 60;
    /// <summary>Shortest allowed snapshot interval, in minutes: each snapshot copies the whole database.</summary>
    public const int MinAnalyticsSnapshotMinutes = 1;
    /// <summary>Seconds one analytics query may run before it is interrupted, unless <c>Relay:Analytics:QueryTimeout</c> says otherwise.</summary>
    public const int DefaultAnalyticsQueryTimeoutSeconds = 10;
    /// <summary>Longest allowed analytics query timeout, in seconds.</summary>
    public const int MaxAnalyticsQueryTimeoutSeconds = 300;
    /// <summary>Rows one analytics result keeps, unless <c>Relay:Analytics:MaxRows</c> says otherwise.</summary>
    public const int DefaultAnalyticsMaxRows = 50;
    /// <summary>Largest allowed <c>Relay:Analytics:MaxRows</c>.</summary>
    public const int MaxAnalyticsMaxRows = 500;
    /// <summary>Largest <c>analytics_query</c> reply, in UTF-8 bytes; a larger result is refused with a request for a narrower query.</summary>
    public const int MaxAnalyticsReplyBytes = 8_192;
    /// <summary>Longest ad hoc analytics SQL, in characters.</summary>
    public const int MaxAnalyticsSqlLength = 4_096;
    /// <summary>Days in each period a named query compares (current against previous), when it gives no <c>days</c>.</summary>
    public const int DefaultAnalyticsPeriodDays = 7;
    /// <summary>
    /// Longest period a named query compares, in days. Two periods of 14 days fit inside the default 30 days of raw activity.
    /// </summary>
    public const int MaxAnalyticsPeriodDays = 14;
    /// <summary>Largest test-effort file the hub reads, in bytes; a larger one reads as unreadable.</summary>
    public const int MaxTestEffortFileBytes = 256 * 1024;
    /// <summary>Most days one test-effort file may hold.</summary>
    public const int MaxTestEffortDays = 120;
    /// <summary>Most costly tests one test-effort file may list.</summary>
    public const int MaxTestEffortTests = 10;
    /// <summary>Longest test name in a test-effort file.</summary>
    public const int MaxTestEffortTestNameLength = 200;
    /// <summary>Memory DuckDB may use for analytics, in megabytes, so a heavy query cannot starve the hub.</summary>
    public const int AnalyticsMemoryLimitMegabytes = 512;
    /// <summary>Threads DuckDB may use for analytics.</summary>
    public const int AnalyticsThreads = 2;
    /// <summary>Largest DuckDB extension the installer accepts once decompressed, in bytes (the sqlite extension is about 35 MB).</summary>
    public const long MaxAnalyticsExtensionBytes = 64L * 1024 * 1024;

    /// <summary>Minutes between anomaly checks, unless <c>Relay:Attention:AnomalyInterval</c> says otherwise.</summary>
    public const int DefaultAnomalyIntervalMinutes = 60;
    /// <summary>Shortest allowed anomaly interval, in minutes (an interval of zero turns the monitor off).</summary>
    public const int MinAnomalyIntervalMinutes = 1;
    /// <summary>Longest allowed anomaly interval, in minutes (7 days).</summary>
    public const int MaxAnomalyIntervalMinutes = 10_080;
    /// <summary>A value at least this many times its baseline is an anomaly, unless a per-query threshold says otherwise.</summary>
    public const double DefaultAnomalyRatio = 2.0;
    /// <summary>
    /// <c>quality_guardrails</c> closures are an anomaly when their baseline is at least this many times the current value (closures
    /// dropped), unless <c>Relay:Attention:AnomalyThresholds:quality_guardrails:DropRatio</c> says otherwise.
    /// </summary>
    public const double DefaultAnomalyDropRatio = 2.0;
    /// <summary>Rows a value and its baseline must each be computed from before either can raise an anomaly, unless set per query.</summary>
    public const int DefaultAnomalyMinSamples = 5;
    /// <summary>Largest allowed per-query minimum sample size.</summary>
    public const int MaxAnomalyMinSamples = 1_000_000;
    /// <summary>Hours after an anomaly is raised before the same named query may raise another one.</summary>
    public const int AnomalyCooldownHours = 24;
    /// <summary>Seconds one anomaly check may spend on its queries in total; queries not finished by then are skipped until the next check.</summary>
    public const int AnomalyCheckBudgetSeconds = 20;

    /// <summary>Largest markdown or text file the doc cache indexes, in UTF-8 bytes; a larger file is skipped and counted at sync.</summary>
    public const int MaxDocBytes = 262_144;
    /// <summary>Largest OpenAPI JSON file the doc cache indexes, in UTF-8 bytes; API specs are often larger than prose docs.</summary>
    public const int MaxOpenApiBytes = 8 * 1024 * 1024;
    /// <summary>Deepest JSON nesting an OpenAPI file may have; a deeper file is skipped as malformed.</summary>
    public const int MaxOpenApiDepth = 64;
    /// <summary>Most nodes (values, objects and arrays, aliases expanded) one OpenAPI file may hold; more is refused, which stops alias bombs.</summary>
    public const int MaxOpenApiNodes = 500_000;
    /// <summary>Longest first-line part of a text chunk's heading, in characters.</summary>
    public const int MaxTextHeadingChars = 60;
    /// <summary>
    /// Documents one repository's doc directory lists, unless <c>Relay:DocCache:MaxDocuments</c> says otherwise; files past this are skipped
    /// and counted at sync. With <see cref="MaxDocBytes"/> the default caps raw text at 128 MB per repository.
    /// </summary>
    public const int MaxDocsPerRepository = 500;
    /// <summary>Largest allowed <c>Relay:DocCache:MaxDocuments</c>.</summary>
    public const int MaxDocsPerRepositoryCeiling = 5_000;
    /// <summary>Repositories the doc cache may add on its own, from <c>Relay:DocCache:Roots</c> scans and sessions' workspaces.</summary>
    public const int MaxDiscoveredDocRepositories = 64;
    /// <summary>
    /// Bytes of document text one repository may hold, unless <c>Relay:DocCache:MaxRepositoryBytes</c> says otherwise; files past it are
    /// skipped and counted at sync. A loaded document holds its text once, plus its Lucene index.
    /// </summary>
    public const long MaxRepositoryDocBytes = 128L * 1024 * 1024;
    /// <summary>Largest allowed <c>Relay:DocCache:MaxRepositoryBytes</c>.</summary>
    public const long MaxRepositoryDocBytesCeiling = 1024L * 1024 * 1024;
    /// <summary>Largest read-only token file the sync reads, in bytes.</summary>
    public const int MaxDocTokenFileBytes = 4_096;
    /// <summary>Markdown sections one document may have; a file with more is skipped at sync.</summary>
    public const int MaxSectionsPerDoc = 1_000;
    /// <summary>Document grains one doc search asks at once; a search over more documents asks them in turn, this many at a time.</summary>
    public const int MaxDocFanOut = 32;
    /// <summary>Seconds one doc search may take over all its documents; past it the reply has the hits so far, <c>partial</c> and the count not searched.</summary>
    public const int DocSearchTimeoutSeconds = 10;
    /// <summary>Most sections one <c>doc_search</c> returns.</summary>
    public const int MaxDocSearchResults = 10;
    /// <summary>Sections <c>doc_search</c> returns when it gives no limit.</summary>
    public const int DefaultDocSearchResults = 5;
    /// <summary>Token budget (o200k) of one <c>doc_search</c> or <c>doc_section</c> reply; what does not fit is reported as a count.</summary>
    public const int MaxDocReplyTokens = 2_000;
    /// <summary>Byte bound on the same replies, so a reply of a few very long tokens still fits the wire.</summary>
    public const int MaxDocReplyBytes = 16_384;
    /// <summary>Long sections are split at paragraph boundaries into chunks of at most this many tokens.</summary>
    public const int DocChunkTokens = 400;
    /// <summary>Chunks are also at most this many characters.</summary>
    public const int DocChunkChars = 3_000;
    /// <summary>Longest snippet on a doc search hit, in characters.</summary>
    public const int MaxDocSnippetChars = 240;
    /// <summary>Longest <c>doc_search</c> query, in characters.</summary>
    public const int MaxDocQueryLength = 256;
    /// <summary>Longest spec name, in characters.</summary>
    public const int MaxDocSpecLength = 64;
    /// <summary>Longest document path, in characters.</summary>
    public const int MaxDocPathLength = 256;
    /// <summary>Longest section anchor, in characters.</summary>
    public const int MaxDocAnchorLength = 128;
    /// <summary>Longest branch name <c>doc_sync</c> accepts, in characters.</summary>
    public const int MaxDocBranchLength = 200;
    /// <summary>Seconds between two <c>doc_sync</c> runs for one repository.</summary>
    public const int DocSyncMinIntervalSeconds = 30;
    /// <summary>Minutes between timer syncs, unless <c>Relay:DocCache:SyncMinutes</c> says otherwise (0 turns the timer off).</summary>
    public const int DefaultDocSyncMinutes = 5;
    /// <summary>Minutes an unused document grain stays loaded, unless <c>Relay:DocCache:IdleMinutes</c> says otherwise.</summary>
    public const int DefaultDocIdleMinutes = 30;
    /// <summary>Longest allowed sync or idle setting, in minutes (one day).</summary>
    public const int MaxDocCacheMinutes = 1_440;
    /// <summary>Seconds one git command may run before it is stopped.</summary>
    public const int DocGitTimeoutSeconds = 120;
    /// <summary>Largest git listing (ls-tree of a whole commit, or diff output) the sync reads, in bytes.</summary>
    public const int MaxDocGitListingBytes = 16 * 1024 * 1024;
    /// <summary>Path patterns one repository may map to spec names.</summary>
    public const int MaxDocPatterns = 32;
}
