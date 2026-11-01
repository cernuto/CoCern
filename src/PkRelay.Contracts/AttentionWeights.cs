using System.Collections.Frozen;

namespace PkRelay.Contracts;

/// <summary>Attention item kinds. A kind always comes from structure (a blocker kind, a closure packet, a claim), never from prose.</summary>
public static class AttentionKinds
{
    public const string Blocked = "blocked";
    public const string Failed = "failed";
    public const string Interrupted = "interrupted";
    public const string ClaimExpiring = "claim_expiring";
    public const string Anomaly = "anomaly";
    public const string OrderFailed = "order_failed";
    public const string ReviewReady = "review_ready";
    public const string DuplicateWarning = "duplicate_warning";
    public const string StandingProposal = "standing_proposal";
    public const string RelatedWork = "related_work";
    public const string NextWork = "next_work";
    public const string Info = "info";
    /// <summary>A one-line efficiency tip from a feedback rule; lowest tier, so it never wakes a chat.</summary>
    public const string Tip = "tip";

    public static readonly IReadOnlyList<string> All =
        [Blocked, Failed, Interrupted, ClaimExpiring, Anomaly, OrderFailed, ReviewReady, DuplicateWarning, StandingProposal, RelatedWork, NextWork, Info, Tip];
}

/// <summary>The answers to an attention offer. <see cref="Later"/> snoozes the item; the other two close it.</summary>
public static class AttentionActions
{
    public const string Accept = "accept";
    public const string Later = "later";
    public const string Dismiss = "dismiss";

    public static readonly IReadOnlyList<string> All = [Accept, Later, Dismiss];
}

/// <summary>
/// Tiers and weights shared by the hub and the adapter, so both rank attention items the same way. Tier 1 is the most urgent.
/// Items are ordered by tier, then by priority weight plus an aging bonus, then oldest first.
/// </summary>
public static class AttentionWeights
{
    public const int TopTier = 1;
    public const int LowestTier = 4;

    /// <summary>A <c>blocked</c> item whose blocker is a provider rejection; it keeps the top tier.</summary>
    public const string ProviderRejection = "provider_rejection";
    /// <summary>A <c>blocked</c> item whose blocker is a coordinator hold; it ranks with reviews (tier 3).</summary>
    public const string CoordinatorHold = "coordinator_hold";

    public const string Low = "low";
    public const string Normal = "normal";
    public const string High = "high";
    public static readonly IReadOnlyList<string> Priorities = [Low, Normal, High];

    /// <summary>Hours an item waits for each point of aging bonus.</summary>
    public const int AgingHours = 4;
    /// <summary>Most aging bonus an item can earn.</summary>
    public const int MaxAgingBonus = 2;

    private static readonly FrozenDictionary<string, int> Tiers = new Dictionary<string, int>
    {
        [AttentionKinds.Blocked] = 1, [AttentionKinds.Failed] = 1, [AttentionKinds.Interrupted] = 1,
        [AttentionKinds.ClaimExpiring] = 2, [AttentionKinds.Anomaly] = 2, [AttentionKinds.OrderFailed] = 2,
        [AttentionKinds.ReviewReady] = 3, [AttentionKinds.DuplicateWarning] = 3, [AttentionKinds.StandingProposal] = 3,
        [AttentionKinds.RelatedWork] = 4, [AttentionKinds.NextWork] = 4, [AttentionKinds.Info] = 4, [AttentionKinds.Tip] = 4
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The kind's tier; a <c>blocked</c> item from a coordinator hold ranks in tier 3. Unknown kinds rank lowest.</summary>
    public static int BaseTier(string kind, string? blocker = null) =>
        kind == AttentionKinds.Blocked && blocker == CoordinatorHold ? 3 : Tiers.TryGetValue(kind, out var tier) ? tier : LowestTier;

    /// <summary>The tier used for ranking: an item that blocks another session's claimed task ranks one tier higher.</summary>
    public static int Tier(string kind, string? blocker = null, bool blocking = false) => Math.Max(TopTier, BaseTier(kind, blocker) - (blocking ? 1 : 0));

    /// <summary>The wake ID of an idle period: this prefix plus the ticks of the boundary that began it (0 before any boundary).</summary>
    public const string HoldWakePrefix = "hold:";

    /// <summary>Only the top tier may wake a chat; every other item waits for a reply the agent was already getting.</summary>
    public static bool Wakes(int tier) => tier == TopTier;

    public static int PriorityWeight(string? priority) => priority switch { High => 2, Low => 0, _ => 1 };

    /// <summary>The level for a task's numeric workflow priority: above zero is high, below zero is low.</summary>
    public static string PriorityLevel(long taskPriority) => taskPriority > 0 ? High : taskPriority < 0 ? Low : Normal;

    /// <summary>One point per <see cref="AgingHours"/> waited, at most <see cref="MaxAgingBonus"/>, so a starved item rises within its tier.</summary>
    public static int AgingBonus(TimeSpan waited) => waited <= TimeSpan.Zero ? 0 : (int)Math.Min(MaxAgingBonus, waited.TotalHours / AgingHours);
}
