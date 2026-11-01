using System.Globalization;
using System.Text.Json.Nodes;
using PkRelay.Contracts;
using static PkRelay.Domain.JsonFields;

namespace PkRelay.Core;

/// <summary>
/// Host settings for feedback (<c>Relay:Feedback:*</c>): tips to agents and proposed team rules, both off by default. Manager alerts are
/// anomaly monitor checks (<see cref="AnomalyOptions.Alerts"/>).
/// </summary>
public sealed record FeedbackOptions
{
    public bool Tips { get; init; }
    public bool ProposeRules { get; init; }
    public double MinFollowRate { get; init; } = RelayLimits.DefaultMinFollowRate;
    /// <summary>The rules' clock. Tests replace it to move through the hourly and daily limits.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    public bool Enabled => Tips || ProposeRules;

    public FeedbackOptions Validated() =>
        MinFollowRate is >= 0 and <= 1 ? this : throw new InvalidOperationException("Relay:Feedback:MinFollowRate must be from 0 to 1.");
}

/// <summary>The tip rules, each with its one-line tip and the standing line the hub may propose for it.</summary>
public static class FeedbackRuleNames
{
    public const string BoardReread = "board_reread";
    public const string TaskReread = "task_reread";
    public const string LongMessages = "long_messages";
    public const string InboxPolling = "inbox_polling";
    public const string RepeatedStatus = "repeated_status";

    public static readonly IReadOnlyList<string> All = [BoardReread, TaskReread, LongMessages, InboxPolling, RepeatedStatus];

    /// <summary>The standing line proposed for a rule that fires across many sessions.</summary>
    public static string StandingLine(string rule) => rule switch
    {
        BoardReread => "Read the board as changes since your last revision.",
        TaskReread => "Pass a read's etag as ifNoneMatch when you read it again.",
        LongMessages => "Keep messages under 150 tokens; put detail in task notes.",
        InboxPolling => "Wait for mail with waitSeconds instead of polling check_inbox.",
        _ => "Report a milestone only when something changed."
    };
}

/// <summary>One finished public call, as the feedback rules see it.</summary>
/// <param name="ModelVisible">False for an adapter's background read (<c>attention: false</c>) or a <c>waitSeconds</c> long-poll.</param>
/// <param name="KnownEtag">The etag the agent passed as <c>ifNoneMatch</c>, which the adapter forwards as <c>knownEtag</c>.</param>
/// <param name="Target">The record a status write went to and <paramref name="ContentHash"/> its fingerprint, as the activity log has them.</param>
public sealed record FeedbackCall(DateTimeOffset At, string RepositoryId, string SessionId, string Tool, JsonObject Args, string ReplyJson, bool Ok,
    bool ModelVisible = true, string? KnownEtag = null, string? Target = null, string? ContentHash = null, string? RequestId = null);

/// <summary>A tip to deliver to one session as a lowest-tier <c>tip</c> attention item.</summary>
public sealed record FeedbackTip(string Rule, string Text, string RepositoryId, string SessionId, long Revision);

/// <summary>A standing line for the hub to propose on a repository's default board.</summary>
public sealed record FeedbackProposal(string Rule, string RepositoryId, string LineId, string Text);

/// <summary>What one call led to: at most one tip and at most one proposal.</summary>
public readonly record struct FeedbackActions(FeedbackTip? Tip, FeedbackProposal? Proposal);

/// <summary>
/// Fixed rules that watch for known wasteful patterns and nudge the agent that can fix them. Plain code: every tip is pre-written text
/// with numbers filled in. Per-session counts live in memory (a restart forgets them; tips are advisory). Each rule's on/off state and
/// follow totals are stored, so a rule switched off stays off.
/// <list type="bullet">
/// <item>At most one tip per session per hour, each rule at most once per session per day, each tip under <see cref="RelayLimits.MaxTipTokens"/> tokens.</item>
/// <item>After a tip, a repeat of its pattern in that session within an hour records "ignored"; no repeat records "followed". A rule
/// followed less than the minimum follow rate across <see cref="RelayLimits.MinTipOutcomes"/> or more outcomes switches itself off.</item>
/// <item>A rule that fires for <see cref="RelayLimits.TeamRuleSessions"/> or more sessions of a repository within
/// <see cref="RelayLimits.TeamRuleDays"/> days yields a proposed standing line, at most once per rule and repository in that time.</item>
/// </list>
/// False-positive guards: only model-visible <c>check_inbox</c> reads count as polling; only full-content reads count as rereads (a read
/// that came back unchanged for the agent's <c>ifNoneMatch</c> does not); repeated status uses the activity log's status fingerprint.
/// </summary>
public sealed class FeedbackRules
{
    private readonly FeedbackOptions options;
    private readonly ITokenCounter tokens;
    private readonly IFeedbackRuleStore? store;
    private readonly Lock gate = new();
    private readonly Dictionary<string, FeedbackRuleState> rules = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SessionCounts> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Hash, string? RequestId)> statuses = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Repository, string Rule), Dictionary<string, DateTimeOffset>> fired = [];
    private readonly Dictionary<(string Repository, string Rule), DateTimeOffset> proposed = [];
    private DateTimeOffset lastSweep = DateTimeOffset.MinValue;
    private Task saving = Task.CompletedTask;

    public FeedbackRules(FeedbackOptions options, ITokenCounter tokens, IFeedbackRuleStore? store = null)
    {
        this.options = options;
        this.tokens = tokens;
        this.store = store;
        foreach (var rule in FeedbackRuleNames.All) rules[rule] = new(rule);
        try
        {
            foreach (var saved in store?.LoadFeedbackRules() ?? []) if (rules.ContainsKey(saved.Rule)) rules[saved.Rule] = saved;
        }
        catch (Exception) { /* Advisory: a store that cannot be read starts every rule on. */ }
    }

    public FeedbackOptions Options => options;

    /// <summary>Each rule's state, in the order of <see cref="FeedbackRuleNames.All"/>.</summary>
    public IReadOnlyList<FeedbackRuleState> Rules { get { lock (gate) return FeedbackRuleNames.All.Select(rule => rules[rule]).ToArray(); } }

    /// <summary>The tip for a rule, with its numbers filled in; it always stays under <see cref="RelayLimits.MaxTipTokens"/> tokens.</summary>
    public string Tip(string rule, string? taskId = null, int count = 0)
    {
        var text = rule switch
        {
            FeedbackRuleNames.BoardReread => "Read the board as changes since your last revision.",
            FeedbackRuleNames.TaskReread => $"You've read {taskId} {Times(count)}; pass its etag as ifNoneMatch.",
            FeedbackRuleNames.LongMessages => $"Keep messages under {RelayLimits.LongMessageTokens} tokens; put detail in task notes.",
            FeedbackRuleNames.InboxPolling => "Wait with waitSeconds, or for offers on milestone replies.",
            _ => "Report a milestone only when something changed."
        };
        // A long task ID could push the tip past its budget; the tip then leaves the ID out.
        return tokens.Count(text) < RelayLimits.MaxTipTokens ? text : $"You've read one task {Times(count)}; pass its etag as ifNoneMatch.";
    }

    private static string Times(int count) => count switch
    {
        4 => "four times", 5 => "five times", 6 => "six times", 7 => "seven times", 8 => "eight times", 9 => "nine times", 10 => "ten times",
        _ => count.ToString(CultureInfo.InvariantCulture) + " times"
    };

    /// <summary>Feeds one finished call to the rules. Never throws; any problem means no feedback for this call.</summary>
    public FeedbackActions Observe(FeedbackCall call)
    {
        if (!options.Enabled || !call.Ok || call.SessionId.Length == 0) return default;
        try
        {
            lock (gate)
            {
                var now = call.At;
                if (now - lastSweep >= TimeSpan.FromMinutes(1)) SweepLocked(now);
                var session = Session(call.SessionId, call.RepositoryId, now);
                SettleLocked(session, now);
                var (rule, taskId, count) = Detect(call, session, now);
                return rule is null ? default : FiredLocked(call, session, rule, taskId, count, now);
            }
        }
        catch (Exception) { return default; }
    }

    /// <summary>Records "followed" for every tip whose hour has passed without a repeat. Runs on its own about once a minute.</summary>
    public void Sweep(DateTimeOffset now) { lock (gate) SweepLocked(now); }

    private (string? Rule, string? TaskId, int Count) Detect(FeedbackCall call, SessionCounts session, DateTimeOffset now)
    {
        var args = call.Args;
        var firstPage = Number(args, "cursor") == 0;
        // Step 1's duplicate status write: the same fingerprint on the same target as the write before it, under another requestId.
        if (call.Target is { } target && call.ContentHash is { } hash)
        {
            var repeated = statuses.TryGetValue(target, out var previous) && previous.Hash == hash && previous.RequestId != call.RequestId;
            if (statuses.Count >= RelayLimits.MaxFeedbackTargets && !statuses.ContainsKey(target)) statuses.Clear();
            statuses[target] = (hash, call.RequestId);
            if (repeated) return (FeedbackRuleNames.RepeatedStatus, null, 0);
        }
        switch (call.Tool)
        {
            case RelayTools.TaskGet when firstPage && !args.ContainsKey("sinceRevision") && Text(args, "section").Length == 0 && !Unchanged(call):
                var taskId = Text(args, "taskId");
                if (taskId.Length == 0)
                    return Window(session.BoardReads, now, TimeSpan.FromHours(1), RelayLimits.BoardRereadsPerHour) ? (FeedbackRuleNames.BoardReread, null, RelayLimits.BoardRereadsPerHour) : default;
                var key = Text(args, "boardId", "default") + "/" + taskId;
                if (!session.TaskReads.TryGetValue(key, out var reads))
                {
                    if (session.TaskReads.Count >= RelayLimits.MaxPageSize) session.TaskReads.Clear();
                    session.TaskReads[key] = reads = new();
                }
                // The window starts over each time it fires, so it always fires at exactly the threshold.
                return Window(reads, now, TimeSpan.FromMinutes(RelayLimits.TaskRereadMinutes), RelayLimits.TaskRereads) ? (FeedbackRuleNames.TaskReread, taskId, RelayLimits.TaskRereads) : default;
            case RelayTools.SendMessage when Text(args, "text") is { Length: > 0 } text:
                session.MessageTokens.Enqueue(tokens.Count(text));
                while (session.MessageTokens.Count > RelayLimits.LongMessageWindow) session.MessageTokens.Dequeue();
                if (session.MessageTokens.Count < RelayLimits.LongMessageWindow || session.MessageTokens.Average() <= RelayLimits.LongMessageTokens) return default;
                session.MessageTokens.Clear();
                return (FeedbackRuleNames.LongMessages, null, 0);
            case RelayTools.CheckInbox when call.ModelVisible && firstPage && !args.ContainsKey("waitSeconds"):
                var fresh = false;
                if (JsonNode.Parse(call.ReplyJson)?["items"] is JsonArray items)
                    foreach (var id in items.OfType<JsonObject>().Select(message => Text(message, "messageId")).Where(id => id.Length > 0))
                        if (session.SeenMessages.Add(id))
                        {
                            fresh = true;
                            session.SeenOrder.Enqueue(id);
                            while (session.SeenOrder.Count > RelayLimits.MaxFeedbackSeenMessages) session.SeenMessages.Remove(session.SeenOrder.Dequeue());
                        }
                if (fresh) return default;
                return Window(session.EmptyPolls, now, TimeSpan.FromHours(1), RelayLimits.InboxPollsPerHour) ? (FeedbackRuleNames.InboxPolling, null, 0) : default;
        }
        return default;
    }

    /// <summary>A read the agent asked for with <c>ifNoneMatch</c> that came back the same, so the adapter answered it with only the etag.</summary>
    private static bool Unchanged(FeedbackCall call) => call.KnownEtag is { Length: > 0 } known && known == RelayEtag.Of(call.ReplyJson);

    /// <summary>Adds one event; true, and the window starts over, when it holds <paramref name="threshold"/> events within <paramref name="span"/>.</summary>
    private static bool Window(Queue<DateTimeOffset> events, DateTimeOffset now, TimeSpan span, int threshold)
    {
        events.Enqueue(now);
        while (events.Count > 0 && now - events.Peek() > span) events.Dequeue();
        if (events.Count < threshold) return false;
        events.Clear();
        return true;
    }

    private FeedbackActions FiredLocked(FeedbackCall call, SessionCounts session, string rule, string? taskId, int count, DateTimeOffset now)
    {
        // A repeat within the hour after a tip means it was not followed.
        if (session.Pending.Remove(rule, out var tippedAt) && now - tippedAt <= TimeSpan.FromMinutes(RelayLimits.TipOutcomeMinutes)) RecordOutcomeLocked(rule, followed: false, now);
        else if (tippedAt != default) RecordOutcomeLocked(rule, followed: true, now);

        FeedbackProposal? proposal = null;
        var key = (call.RepositoryId, rule);
        if (!fired.TryGetValue(key, out var bySession)) fired[key] = bySession = new(StringComparer.Ordinal);
        bySession[call.SessionId] = now;
        foreach (var stale in bySession.Where(entry => now - entry.Value > TimeSpan.FromDays(RelayLimits.TeamRuleDays)).Select(entry => entry.Key).ToArray()) bySession.Remove(stale);
        if (bySession.Count > RelayLimits.MaxFeedbackSessions) bySession.Remove(bySession.MinBy(entry => entry.Value).Key);
        if (options.ProposeRules && bySession.Count >= RelayLimits.TeamRuleSessions
            && (!proposed.TryGetValue(key, out var proposedAt) || now - proposedAt > TimeSpan.FromDays(RelayLimits.TeamRuleDays)))
        {
            proposed[key] = now;
            proposal = new(rule, call.RepositoryId, "tip-" + rule.Replace('_', '-'), FeedbackRuleNames.StandingLine(rule));
        }

        FeedbackTip? tip = null;
        var lastRuleTip = session.RuleTipped.TryGetValue(rule, out var ruleAt) ? ruleAt : DateTimeOffset.MinValue;
        if (options.Tips && rules[rule].Enabled && now - session.LastTip >= TimeSpan.FromHours(1.0 / RelayLimits.TipsPerSessionPerHour)
            && now - lastRuleTip >= TimeSpan.FromHours(RelayLimits.TipRuleCooldownHours))
        {
            session.LastTip = now;
            session.RuleTipped[rule] = now;
            session.Pending[rule] = now;
            rules[rule] = rules[rule] with { Tips = rules[rule].Tips + 1 };
            SaveLocked(rules[rule]);
            tip = new(rule, Tip(rule, taskId, count), call.RepositoryId, call.SessionId, now.ToUnixTimeMilliseconds());
        }
        return new(tip, proposal);
    }

    private void SettleLocked(SessionCounts session, DateTimeOffset now)
    {
        foreach (var (rule, at) in session.Pending.ToArray())
            if (now - at > TimeSpan.FromMinutes(RelayLimits.TipOutcomeMinutes)) { session.Pending.Remove(rule); RecordOutcomeLocked(rule, followed: true, now); }
    }

    private void SweepLocked(DateTimeOffset now)
    {
        lastSweep = now;
        foreach (var session in sessions.Values) SettleLocked(session, now);
    }

    private void RecordOutcomeLocked(string rule, bool followed, DateTimeOffset now)
    {
        var state = rules[rule];
        state = followed ? state with { Followed = state.Followed + 1 } : state with { Ignored = state.Ignored + 1 };
        if (state.Enabled && state.Outcomes >= RelayLimits.MinTipOutcomes && (double)state.Followed / state.Outcomes < options.MinFollowRate)
            state = state with { Enabled = false, DisabledAt = now };
        rules[rule] = state;
        SaveLocked(state);
    }

    /// <summary>Saves in order, off the call's path; a failed save is retried with the next change of that rule.</summary>
    private void SaveLocked(FeedbackRuleState state)
    {
        if (store is null) return;
        saving = saving.ContinueWith(async _ =>
        {
            try { await store.SaveFeedbackRuleAsync(state); }
            catch (Exception) { /* Advisory. */ }
        }, TaskScheduler.Default).Unwrap();
    }

    /// <summary>Waits for queued saves; tests use it.</summary>
    public Task FlushAsync() { lock (gate) return saving; }

    private SessionCounts Session(string sessionId, string repository, DateTimeOffset now)
    {
        if (!sessions.TryGetValue(sessionId, out var session))
        {
            if (sessions.Count >= RelayLimits.MaxFeedbackSessions) sessions.Remove(sessions.MinBy(entry => entry.Value.LastSeen).Key);
            sessions[sessionId] = session = new() { Repository = repository };
        }
        session.LastSeen = now;
        return session;
    }

    private sealed class SessionCounts
    {
        public string Repository { get; init; } = "";
        public DateTimeOffset LastSeen { get; set; }
        public DateTimeOffset LastTip { get; set; } = DateTimeOffset.MinValue;
        public Queue<DateTimeOffset> BoardReads { get; } = new();
        public Dictionary<string, Queue<DateTimeOffset>> TaskReads { get; } = new(StringComparer.Ordinal);
        public Queue<int> MessageTokens { get; } = new();
        public Queue<DateTimeOffset> EmptyPolls { get; } = new();
        public HashSet<string> SeenMessages { get; } = new(StringComparer.Ordinal);
        public Queue<string> SeenOrder { get; } = new();
        public Dictionary<string, DateTimeOffset> RuleTipped { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, DateTimeOffset> Pending { get; } = new(StringComparer.Ordinal);
    }
}
