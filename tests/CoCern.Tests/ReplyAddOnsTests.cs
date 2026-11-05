using CoCern.Core;
using Xunit;

namespace CoCern.Tests;

/// <summary>What a reply picks up after routing runs in one fixed order, which is part of the hub's behaviour.</summary>
public sealed class ReplyAddOnsTests
{
    [Fact]
    public void Reply_add_ons_run_in_their_fixed_order_with_compaction_before_mark_seen()
    {
        // The attention offer is chosen once the reply carries the rest, so it is skipped when no room is left; the offered work is raised
        // after that offer was chosen; and a page is marked seen from the reply as sent, after compaction, so a message cut from it stays unseen.
        Assert.Equal(
            new[] { "token_status", "waiting_on_you", "piggyback", "next_work", "standing", "attention_offer", "raise_offered_work", "close_claimed_work", "related_work", "status_tag", "bound", "mark_seen" },
            ReplyAddOns.Steps.Select(step => step.Name));
    }
}
