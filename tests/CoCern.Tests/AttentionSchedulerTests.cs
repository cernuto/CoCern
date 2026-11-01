using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain;
using Xunit;

namespace CoCern.Tests;

public sealed class AttentionSchedulerTests
{
    private static readonly RelayCaller Hub = new("alice", false, ["repo"], true);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-29T00:00:00Z");

    private static StateEnvelope<CoordinationDocument> OpenSession()
    {
        var session = new StateEnvelope<CoordinationDocument>();
        Assert.True(Call(session, "__open", new() { ["principalId"] = "alice", ["sessionToken"] = "secret", ["sessionId"] = "s1" }).Ok);
        return session;
    }

    private static RelayReply Call(StateEnvelope<CoordinationDocument> session, string operation, JsonObject args, DateTimeOffset? at = null)
    {
        args["repositoryId"] = "repo";
        if (operation is not ("__open" or "__attention_raise" or "__attention_close")) args["sessionToken"] = "secret";
        return CoordinationStateMachine.Apply(session, "session", "s1", Hub, operation, args, at ?? Now);
    }

    private static JsonObject Data(RelayReply reply) { Assert.True(reply.Ok, reply.Code + " " + reply.DataJson); return JsonNode.Parse(reply.DataJson)!.AsObject(); }

    private static string Raise(StateEnvelope<CoordinationDocument> session, string kind, string taskId, DateTimeOffset? at = null, JsonObject? extra = null)
    {
        var args = extra ?? new JsonObject();
        args["kind"] = kind; args["taskId"] = taskId;
        return Data(Call(session, "__attention_raise", args, at))["itemId"]!.GetValue<string>();
    }

    /// <summary>The offered item's ID at this boundary, or null when nothing is offered.</summary>
    private static string? Offer(StateEnvelope<CoordinationDocument> session, DateTimeOffset? at = null, string? focus = null)
    {
        var args = new JsonObject();
        if (focus is not null) args["focusTaskId"] = focus;
        return Data(Call(session, "__attention_offer", args, at))["attention"]?["id"]?.GetValue<string>();
    }

    private static RelayReply Respond(StateEnvelope<CoordinationDocument> session, string itemId, string action, DateTimeOffset? at = null, string? until = null, string? requestId = null)
    {
        var args = new JsonObject { ["itemId"] = itemId, ["action"] = action, ["requestId"] = requestId ?? Guid.NewGuid().ToString("N") };
        if (until is not null) args["until"] = until;
        return Call(session, "attention_respond", args, at);
    }

    private static JsonObject Items(StateEnvelope<CoordinationDocument> session) => JsonNode.Parse(session.Value.Json)!["attention"]?.AsObject() ?? new();

    private static AttentionItem Item(string id, string kind, DateTimeOffset createdAt, string priority = "normal", string taskId = "T", string? blocker = null, bool blocking = false,
        int snoozeBoundaries = 0, DateTimeOffset? snoozeUntil = null, DateTimeOffset? expiresAt = null) =>
        new(id, kind, taskId, "default", "", blocker, blocking, priority, [], "", createdAt, snoozeBoundaries, snoozeUntil, null, expiresAt ?? createdAt.AddDays(7), 0);

    [Fact]
    public void Severity_tier_beats_priority()
    {
        var items = new[]
        {
            Item("a-1", AttentionKinds.NextWork, Now, priority: AttentionWeights.High),
            Item("a-2", AttentionKinds.ReviewReady, Now, priority: AttentionWeights.High),
            Item("a-3", AttentionKinds.Failed, Now, priority: AttentionWeights.Low)
        };
        Assert.Equal("a-3", AttentionScheduler.Pick(items, Now)!.Id);
        // Nine hours of aging lifts an item within its tier but never past a more urgent tier.
        var old = Now.AddHours(-9);
        Assert.Equal("a-2", AttentionScheduler.Pick([Item("a-1", AttentionKinds.NextWork, old, priority: AttentionWeights.High), Item("a-2", AttentionKinds.ReviewReady, Now, priority: AttentionWeights.Low)], Now)!.Id);
        // An item that blocks another session's claimed task ranks one tier higher: a blocking review (tier 2) beats an older, high-priority
        // duplicate warning (tier 3), and ties with an expiring claim (tier 2), where the older item wins.
        Assert.Equal("a-2", AttentionScheduler.Pick([Item("a-1", AttentionKinds.DuplicateWarning, old, priority: AttentionWeights.High), Item("a-2", AttentionKinds.ReviewReady, Now, blocking: true)], Now)!.Id);
        Assert.Equal("a-1", AttentionScheduler.Pick([Item("a-1", AttentionKinds.ClaimExpiring, Now.AddMinutes(-1)), Item("a-2", AttentionKinds.ReviewReady, Now, blocking: true)], Now)!.Id);
    }

    [Fact]
    public void Aging_lifts_a_starved_item()
    {
        var fresh = Item("a-2", AttentionKinds.NextWork, Now, priority: AttentionWeights.High);
        // Under four hours a low item earns nothing, so the high one wins.
        Assert.Equal("a-2", AttentionScheduler.Pick([Item("a-1", AttentionKinds.NextWork, Now.AddHours(-3), priority: AttentionWeights.Low), fresh], Now)!.Id);
        // After eight hours the low item has +2 and catches up; as the older item it wins the tie.
        Assert.Equal("a-1", AttentionScheduler.Pick([Item("a-1", AttentionKinds.NextWork, Now.AddHours(-8), priority: AttentionWeights.Low), fresh], Now)!.Id);
        Assert.Equal((0, 1, 2, 2), (AttentionWeights.AgingBonus(TimeSpan.FromHours(3)), AttentionWeights.AgingBonus(TimeSpan.FromHours(4)), AttentionWeights.AgingBonus(TimeSpan.FromHours(8)), AttentionWeights.AgingBonus(TimeSpan.FromDays(30))));
    }

    [Fact]
    public void Ties_go_to_the_oldest_item()
    {
        var items = new[] { Item("a-1", AttentionKinds.Info, Now.AddMinutes(-1)), Item("a-2", AttentionKinds.Info, Now.AddMinutes(-2)), Item("a-3", AttentionKinds.Info, Now) };
        Assert.Equal("a-2", AttentionScheduler.Pick(items, Now)!.Id);
        Assert.Equal(new[] { "a-2", "a-1", "a-3" }, AttentionScheduler.Order(items, Now).Select(item => item.Id));
    }

    [Fact]
    public void Focused_task_snoozed_and_expired_items_are_skipped()
    {
        var items = new[]
        {
            Item("a-1", AttentionKinds.Blocked, Now, taskId: "T-1"),
            Item("a-2", AttentionKinds.Failed, Now, taskId: "T-2", snoozeBoundaries: 1),
            Item("a-3", AttentionKinds.Interrupted, Now, taskId: "T-3", snoozeUntil: Now.AddMinutes(5)),
            Item("a-4", AttentionKinds.ClaimExpiring, Now.AddDays(-8), taskId: "T-4", expiresAt: Now),
            Item("a-5", AttentionKinds.Info, Now, taskId: "T-5")
        };
        Assert.Equal("a-1", AttentionScheduler.Pick(items, Now)!.Id);
        Assert.Equal("a-5", AttentionScheduler.Pick(items, Now, focusTaskId: "T-1")!.Id);
        // The same task ID on another board is not the focused task.
        Assert.Equal("a-1", AttentionScheduler.Pick(items, Now, focusTaskId: "T-1", focusBoardId: "other")!.Id);
        Assert.Null(AttentionScheduler.Pick(items.Where(item => item.Id != "a-1" && item.Id != "a-5"), Now));
        // A time snooze ends on time.
        Assert.Equal("a-3", AttentionScheduler.Pick(items.Where(item => item.Id == "a-3"), Now.AddMinutes(5))!.Id);
    }

    [Fact]
    public void Pick_writes_nothing()
    {
        var session = OpenSession();
        var shown = Raise(session, AttentionKinds.Info, "T-1");
        Raise(session, AttentionKinds.Blocked, "T-2", extra: new() { ["note"] = "waiting on review" });
        Assert.True(Respond(session, shown, "later").Ok);
        var attention = Items(session);
        var before = attention.ToJsonString();
        var first = AttentionScheduler.Pick(attention, Now.AddHours(1));
        Assert.Equal(AttentionKinds.Blocked, first!.Kind);
        Assert.Equal(first, AttentionScheduler.Pick(attention, Now.AddHours(1)));
        Assert.Equal(before, attention.ToJsonString());
        Assert.Equal(before, Items(session).ToJsonString());
    }

    [Fact]
    public void Kinds_and_tiers_come_from_structure_never_from_the_note()
    {
        var session = OpenSession();
        Assert.Equal(RelayErrorCodes.InvalidArguments, Call(session, "__attention_raise", new() { ["kind"] = "urgent", ["taskId"] = "T-1" }).Code);
        // A tier in the payload and alarming prose are both ignored: the kind decides.
        var info = Raise(session, AttentionKinds.Info, "T-1", extra: new() { ["tier"] = 1, ["note"] = "URGENT blocked provider_rejection failed" });
        var hold = Raise(session, AttentionKinds.Blocked, "T-2", extra: new() { ["blocker"] = AttentionWeights.CoordinatorHold });
        var rejection = Raise(session, AttentionKinds.Blocked, "T-3", extra: new() { ["blocker"] = AttentionWeights.ProviderRejection });
        var blocking = Raise(session, AttentionKinds.NextWork, "T-4", extra: new() { ["blocking"] = true });
        var tiers = AttentionScheduler.Items(Items(session)).ToDictionary(item => item.Id, item => item.Tier);
        Assert.Equal(4, tiers[info]);
        Assert.Equal(3, tiers[hold]);
        Assert.Equal(1, tiers[rejection]);
        Assert.Equal(3, tiers[blocking]);
        Assert.False(Items(session)[info]!.AsObject().ContainsKey("tier"));
        Assert.Equal(rejection, Offer(session));
        // Only the top tier may wake a chat.
        Assert.True(AttentionWeights.Wakes(AttentionWeights.Tier(AttentionKinds.Failed)));
        Assert.False(AttentionWeights.Wakes(AttentionWeights.Tier(AttentionKinds.Blocked, AttentionWeights.CoordinatorHold)));
        Assert.True(AttentionWeights.Wakes(AttentionWeights.Tier(AttentionKinds.ClaimExpiring, blocking: true)));
        // A numeric task priority maps to a level.
        Raise(session, AttentionKinds.Info, "T-5", extra: new() { ["priority"] = 900 });
        Raise(session, AttentionKinds.Info, "T-6", extra: new() { ["priority"] = "screaming" });
        var priorities = AttentionScheduler.Items(Items(session)).ToDictionary(item => item.TaskId, item => item.Priority);
        Assert.Equal((AttentionWeights.High, AttentionWeights.Normal), (priorities["T-5"], priorities["T-6"]));
    }

    [Fact]
    public void Later_never_acknowledges_the_message()
    {
        var session = OpenSession();
        Assert.True(Call(session, "__deliver", new() { ["messageId"] = "m-1", ["text"] = "please review T-7" }).Ok);
        var later = Raise(session, AttentionKinds.Info, "T-7", extra: new() { ["refs"] = new JsonArray("m-1") });
        Assert.Equal(later, Offer(session));
        Assert.Equal("later", Data(Respond(session, later, "later"))["outcome"]!.GetValue<string>());
        Assert.Equal("dismissed", Data(Respond(session, later, "dismiss"))["outcome"]!.GetValue<string>());
        var state = JsonNode.Parse(session.Value.Json)!.AsObject();
        Assert.True(state["inbox"]!.AsObject().ContainsKey("m-1"));
        Assert.False(state["acked"]?.AsObject().ContainsKey("m-1") ?? false);
        Assert.Single(Data(Call(session, "check_inbox", new()))["items"]!.AsArray());
        Assert.Equal(RelayErrorCodes.NotFound, Respond(session, later, "accept").Code);
    }

    [Fact]
    public void Blocking_items_are_capped_at_two_snoozes_and_thirty_minutes()
    {
        var session = OpenSession();
        var blocker = Raise(session, AttentionKinds.Blocked, "T-1", extra: new() { ["blocking"] = true });
        Assert.True(Respond(session, blocker, "later").Ok);
        // A two-hour snooze is cut to thirty minutes after the first snooze.
        var timed = Data(Respond(session, blocker, "later", Now.AddMinutes(1), until: Now.AddHours(2).ToString("O")));
        Assert.Equal(Now.AddMinutes(30), DateTimeOffset.Parse(timed["snoozeUntil"]!.GetValue<string>()));
        var refused = Respond(session, blocker, "later", Now.AddMinutes(2));
        Assert.Equal(RelayErrorCodes.SnoozeLimit, refused.Code);
        Assert.Equal(RelayLimits.MaxBlockingSnoozes, JsonNode.Parse(refused.DataJson)!["maxSnoozes"]!.GetValue<int>());
        Assert.Equal(blocker, Offer(session, Now.AddMinutes(30)));
        // Ignoring it now is not another snooze: it is offered again at the very next boundary.
        Assert.Equal(blocker, Offer(session, Now.AddMinutes(31)));
        Assert.Equal(blocker, Offer(session, Now.AddMinutes(32)));

        // Thirty minutes after the first snooze, even a first-time "later" is refused.
        var other = Raise(session, AttentionKinds.Failed, "T-2", Now.AddMinutes(40), new() { ["blocking"] = true });
        Assert.True(Respond(session, other, "later", Now.AddMinutes(40)).Ok);
        Assert.Equal(RelayErrorCodes.SnoozeLimit, Respond(session, other, "later", Now.AddMinutes(71)).Code);
        // An item that blocks no one may be snoozed as often as the agent likes.
        var plain = Raise(session, AttentionKinds.Failed, "T-3");
        for (var attempt = 0; attempt < 4; attempt++) Assert.True(Respond(session, plain, "later", Now.AddHours(attempt)).Ok);
    }

    [Fact]
    public void Items_are_heard_only_when_delivered()
    {
        var session = OpenSession();
        var item = Raise(session, AttentionKinds.Blocked, "T-1", extra: new() { ["note"] = "waiting on schema review" });
        string? Shown() => Items(session)[item]!["shownAt"]?.GetValue<string>();
        // Touching the same task, reading the inbox, answering another item or refreshing this one never marks it seen.
        Assert.True(Call(session, "check_inbox", new()).Ok);
        var other = Raise(session, AttentionKinds.Info, "T-2");
        Assert.True(Respond(session, other, "dismiss").Ok);
        Raise(session, AttentionKinds.Blocked, "T-1", extra: new() { ["note"] = "still waiting" });
        Assert.Null(Offer(session, focus: "T-1"));
        Assert.Null(Shown());
        Assert.Equal(item, Offer(session));
        Assert.NotNull(Shown());
        var offered = Data(Call(session, "__attention_offer", new(), Now.AddMinutes(1)));
        Assert.Null(offered["attention"]);
    }

    [Fact]
    public void More_counts_only_items_that_could_be_offered_now()
    {
        var session = OpenSession();
        Raise(session, AttentionKinds.Blocked, "T-1");
        Raise(session, AttentionKinds.Info, "T-1");
        var next = Raise(session, AttentionKinds.NextWork, "T-2");
        var reply = Data(Call(session, "__attention_offer", new() { ["focusTaskId"] = "T-1" }));
        // The two items for the focused task are still actionable, but neither could be offered at this boundary.
        Assert.Equal((next, 0, 3), (reply["attention"]!["id"]!.GetValue<string>(), reply["attention"]!["more"]!.GetValue<int>(), reply["actionable"]!.GetValue<int>()));
    }

    [Fact]
    public void Later_hides_an_item_for_exactly_three_boundaries_through_offer()
    {
        var session = OpenSession();
        var item = Raise(session, AttentionKinds.Failed, "T-1", extra: new() { ["note"] = "tests failed", ["refs"] = new JsonArray("T-9") });
        var offer = Data(Call(session, "__attention_offer", new()));
        Assert.Equal(new JsonObject { ["id"] = item, ["k"] = "failed", ["t"] = "T-1", ["x"] = "T-1 failed: tests failed refs T-9", ["more"] = 0 }.ToJsonString(), offer["attention"]!.ToJsonString());
        Assert.Equal(3, Data(Respond(session, item, "later"))["snoozeBoundaries"]!.GetValue<int>());
        Assert.Equal(new string?[] { null, null, null, item }, Enumerable.Range(1, 4).Select(_ => Offer(session)).ToArray());
        // The hub's configured snooze is passed to the session; two boundaries here.
        var args = new JsonObject { ["itemId"] = item, ["action"] = "later", ["requestId"] = "two", ["snoozeBoundaries"] = 2 };
        Assert.True(Call(session, "attention_respond", args).Ok);
        Assert.Equal(new string?[] { null, null, item }, Enumerable.Range(1, 3).Select(_ => Offer(session)).ToArray());
    }

    [Fact]
    public void An_ignored_offer_counts_as_later()
    {
        var session = OpenSession();
        var item = Raise(session, AttentionKinds.ReviewReady, "T-1");
        var next = Raise(session, AttentionKinds.NextWork, "T-2");
        Assert.Equal(item, Offer(session));
        // Not answered: at the next boundary it counts as later and sits out three boundaries, while the other item takes its turn.
        Assert.Equal(next, Offer(session));
        Assert.Equal(1, Items(session)[item]!["snoozes"]!.GetValue<int>());
        Assert.Null(Offer(session));
        Assert.Null(Offer(session));
        Assert.Equal(item, Offer(session));
        Assert.Equal(next, Offer(session));
    }

    [Fact]
    public void A_time_snooze_returns_on_time()
    {
        var session = OpenSession();
        var item = Raise(session, AttentionKinds.ClaimExpiring, "T-1");
        Assert.Equal(RelayErrorCodes.InvalidArguments, Respond(session, item, "later", until: Now.AddMinutes(-1).ToString("O")).Code);
        Assert.Equal(RelayErrorCodes.InvalidArguments, Respond(session, item, "snooze").Code);
        Assert.True(Respond(session, item, "later", until: Now.AddHours(1).ToString("O")).Ok);
        // Boundaries do not count a time snooze down.
        for (var minute = 1; minute < 60; minute += 10) Assert.Null(Offer(session, Now.AddMinutes(minute)));
        Assert.Equal(item, Offer(session, Now.AddHours(1)));
        var reply = Data(Call(session, "__attention_offer", new(), Now.AddHours(2)));
        // Shown and ignored, so now snoozed: nothing is actionable and the reply is tiny.
        Assert.Equal(0, reply["actionable"]!.GetValue<int>());
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(reply).Length < 64);
    }

    [Fact]
    public void An_offer_stays_small_enough_for_the_composition_reserve()
    {
        var session = OpenSession();
        var longId = new string('t', RelayLimits.MaxIdentifierLength);
        Raise(session, AttentionKinds.Blocked, longId, extra: new() { ["boardId"] = new string('b', RelayLimits.MaxIdentifierLength), ["note"] = new string('é', 1_000),
            ["refs"] = new JsonArray(Enumerable.Range(0, 20).Select(index => (JsonNode)(index + new string('r', 250))).ToArray()) });
        var offer = Data(Call(session, "__attention_offer", new()))["attention"]!.AsObject();
        Assert.Equal(RelayLimits.MaxAttentionTextLength, offer["x"]!.GetValue<string>().Length);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(offer).Length < RelayLimits.CompositionReserveBytes / 4);
        Assert.Equal(RelayLimits.MaxAttentionRefs, Items(session).Single().Value!["refs"]!.AsArray().Count);
    }
}
