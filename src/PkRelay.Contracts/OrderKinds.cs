namespace PkRelay.Contracts;

/// <summary>What fires a standing order: a fixed set, not a condition language.</summary>
public static class OrderTriggers
{
    /// <summary>A named task's workflow becomes completed (every criterion passed with an evidence reference).</summary>
    public const string WorkflowCompleted = "workflow_completed";
    /// <summary>A named task leaves the blocked state.</summary>
    public const string BlockerCleared = "blocker_cleared";
    /// <summary>A claim on a named registered resource is released.</summary>
    public const string ClaimReleased = "claim_released";

    public static readonly IReadOnlyList<string> All = [WorkflowCompleted, BlockerCleared, ClaimReleased];
}

/// <summary>What a standing order may do: coordination only. Acceptance, grants and blocker changes are never actions.</summary>
public static class OrderActions
{
    /// <summary>Releases a task's remaining claims: its task claim and the resource claims its owner holds for it.</summary>
    public const string ReleaseClaims = "release_claims";
    /// <summary>Offers a named task to a named session as a <c>next_work</c> attention item; it still needs <c>task_claim</c>.</summary>
    public const string Offer = "offer";
    /// <summary>Sends a named session an <c>info</c> attention item.</summary>
    public const string Notify = "notify";

    public static readonly IReadOnlyList<string> All = [ReleaseClaims, Offer, Notify];
}
