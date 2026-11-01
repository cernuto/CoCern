using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using PkRelay.Contracts;
using PkRelay.Core;
using PkRelay.Domain;
using PkRelay.Host;
using PkRelay.Storage;
using Xunit;

namespace PkRelay.Tests;

/// <summary>Fixed rules turn repeated waste into one-line tips, within strict rate limits, and switch themselves off when ignored.</summary>
public sealed class FeedbackRulesTests
{
    private static readonly ITokenCounter Tokens = new O200kTokenCounter();
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly FeedbackOptions TipsOn = new() { Tips = true };

    private static FeedbackCall Call(DateTimeOffset at, string tool, object args, string reply = "{}", string session = "s-1", bool visible = true, string? etag = null,
        string? target = null, string? hash = null, string? requestId = null) =>
        new(at, "repo", session, tool, JsonNode.Parse(JsonSerializer.Serialize(args))!.AsObject(), reply, true, visible, etag, target, hash, requestId);

    private static FeedbackCall BoardRead(DateTimeOffset at, string session = "s-1") => Call(at, RelayTools.TaskGet, new { repositoryId = "repo" }, "{\"items\":[]}", session);

    [Fact]
    public void Each_rule_fires_on_its_pattern_with_its_one_line_tip()
    {
        // Full board rereads: three in an hour. A delta read, a later page or an unchanged conditional read does not count.
        var rules = new FeedbackRules(TipsOn, Tokens);
        var board = "{\"items\":[{\"taskId\":\"T1\"}]}";
        Assert.Null(rules.Observe(BoardRead(Start)).Tip);
        Assert.Null(rules.Observe(Call(Start.AddMinutes(1), RelayTools.TaskGet, new { repositoryId = "repo", sinceRevision = 4 })).Tip);
        Assert.Null(rules.Observe(Call(Start.AddMinutes(2), RelayTools.TaskGet, new { repositoryId = "repo", cursor = 20 })).Tip);
        Assert.Null(rules.Observe(Call(Start.AddMinutes(3), RelayTools.TaskGet, new { repositoryId = "repo" }, board, etag: RelayEtag.Of(board))).Tip);
        Assert.Null(rules.Observe(BoardRead(Start.AddMinutes(4))).Tip);
        var tip = rules.Observe(BoardRead(Start.AddMinutes(5))).Tip;
        Assert.Equal((FeedbackRuleNames.BoardReread, "Read the board as changes since your last revision.", "s-1"), (tip!.Rule, tip.Text, tip.SessionId));
        // Reads spread over more than an hour never add up to three.
        var spread = new FeedbackRules(TipsOn, Tokens);
        for (var i = 0; i < 5; i++) Assert.Null(spread.Observe(BoardRead(Start.AddMinutes(40 * i))).Tip);

        // Same task reread: four full reads of one task in 30 minutes. Unchanged replies to ifNoneMatch do not count.
        rules = new FeedbackRules(TipsOn, Tokens);
        var task = "{\"taskId\":\"T12\",\"version\":3}";
        FeedbackCall Read(int minute, string? etag = null) => Call(Start.AddMinutes(minute), RelayTools.TaskGet, new { repositoryId = "repo", taskId = "T12" }, task, etag: etag);
        Assert.Null(rules.Observe(Read(0)).Tip);
        Assert.Null(rules.Observe(Read(1, RelayEtag.Of(task))).Tip);
        Assert.Null(rules.Observe(Read(2, RelayEtag.Of(task))).Tip);
        Assert.Null(rules.Observe(Read(3)).Tip);
        Assert.Null(rules.Observe(Read(4)).Tip);
        // A read whose etag no longer matches came back in full, so it counts.
        Assert.Equal("You've read T12 four times; pass its etag as ifNoneMatch.", rules.Observe(Read(5, "0000000000000000")).Tip!.Text);

        // Long messages: the last five average over 150 tokens.
        rules = new FeedbackRules(TipsOn, Tokens);
        var shortText = "Build passed on bench.";
        var longText = string.Join(' ', Enumerable.Repeat("detail", 200));
        // Three long and two short messages average under 150 tokens.
        foreach (var (text, minute) in new[] { longText, shortText, longText, shortText, longText }.Select((text, minute) => (text, minute)))
            Assert.Null(rules.Observe(Call(Start.AddMinutes(minute), RelayTools.SendMessage, new { repositoryId = "repo", text })).Tip);
        rules = new FeedbackRules(TipsOn, Tokens);
        for (var i = 0; i < 4; i++) Assert.Null(rules.Observe(Call(Start.AddMinutes(i), RelayTools.SendMessage, new { repositoryId = "repo", text = longText })).Tip);
        Assert.Equal("Keep messages under 150 tokens; put detail in task notes.", rules.Observe(Call(Start.AddMinutes(5), RelayTools.SendMessage, new { repositoryId = "repo", text = longText })).Tip!.Text);
        // Five short messages never fire.
        rules = new FeedbackRules(TipsOn, Tokens);
        for (var i = 0; i < 10; i++) Assert.Null(rules.Observe(Call(Start.AddMinutes(i), RelayTools.SendMessage, new { repositoryId = "repo", text = shortText })).Tip);

        // Inbox polling: six model-visible reads with nothing new in an hour. Background reads (attention: false) and waitSeconds
        // long-polls do not count, and a read that shows a new message is not polling.
        rules = new FeedbackRules(TipsOn, Tokens);
        var empty = "{\"items\":[],\"total\":0}";
        var mail = "{\"items\":[{\"messageId\":\"m1\",\"text\":\"hi\"}],\"total\":1}";
        FeedbackCall Inbox(int minute, string reply, bool visible = true, object? args = null) =>
            Call(Start.AddMinutes(minute), RelayTools.CheckInbox, args ?? new { repositoryId = "repo" }, reply, visible: visible);
        Assert.Null(rules.Observe(Inbox(0, mail)).Tip);
        for (var i = 1; i <= 10; i++) Assert.Null(rules.Observe(Inbox(i, empty, visible: false)).Tip);
        for (var i = 11; i <= 20; i++) Assert.Null(rules.Observe(Inbox(i, empty, args: new { repositoryId = "repo", waitSeconds = 30 })).Tip);
        for (var i = 21; i <= 25; i++) Assert.Null(rules.Observe(Inbox(i, i == 21 ? mail : empty)).Tip);
        Assert.Equal("Wait with waitSeconds, or for offers on milestone replies.", rules.Observe(Inbox(26, empty)).Tip!.Text);

        // Repeated status: step 1's fingerprint, the same text on the same target as the write before it under another requestId. An
        // exact retry (the same requestId) is not a repeat.
        rules = new FeedbackRules(TipsOn, Tokens);
        FeedbackCall Status(int minute, string requestId, string hash = "H1") =>
            Call(Start.AddMinutes(minute), RelayTools.TaskUpdate, new { repositoryId = "repo", taskId = "T1" }, target: "task:repo/default/T1", hash: hash, requestId: requestId);
        Assert.Null(rules.Observe(Status(0, "r1")).Tip);
        Assert.Null(rules.Observe(Status(1, "r1")).Tip);
        Assert.Null(rules.Observe(Status(2, "r2", "H2")).Tip);
        Assert.Equal("Report a milestone only when something changed.", rules.Observe(Status(3, "r3", "H2")).Tip!.Text);
        // Another session writing the same text to the same target did not repeat its own write, so it gets no tip.
        rules = new FeedbackRules(TipsOn, Tokens);
        FeedbackCall By(string session, int minute, string requestId) =>
            Call(Start.AddMinutes(minute), RelayTools.TaskUpdate, new { repositoryId = "repo", taskId = "T1" }, session: session, target: "task:repo/default/T1", hash: "H", requestId: requestId);
        Assert.Null(rules.Observe(By("s-1", 0, "a1")).Tip);
        Assert.Null(rules.Observe(By("s-2", 1, "b1")).Tip);
        // The session that repeats its own write is the one tipped.
        Assert.Equal("s-1", rules.Observe(By("s-1", 2, "a2")).Tip!.SessionId);
    }

    [Fact]
    public void Every_tip_stays_under_forty_tokens_even_with_a_long_task_id()
    {
        var rules = new FeedbackRules(TipsOn, Tokens);
        foreach (var rule in FeedbackRuleNames.All)
        {
            Assert.InRange(Tokens.Count(rules.Tip(rule, "T12", 4)), 1, RelayLimits.MaxTipTokens - 1);
            Assert.InRange(Tokens.Count(FeedbackRuleNames.StandingLine(rule)), 1, RelayLimits.MaxTipTokens - 1);
        }
        var longId = rules.Tip(FeedbackRuleNames.TaskReread, new string('x', 200), 4);
        Assert.Equal("You've read one task four times; pass its etag as ifNoneMatch.", longId);
    }

    [Fact]
    public void A_session_gets_at_most_one_tip_an_hour_and_each_rule_once_a_day()
    {
        var rules = new FeedbackRules(TipsOn, Tokens);
        void Boards(DateTimeOffset at) { for (var i = 0; i < 2; i++) Assert.Null(rules.Observe(BoardRead(at.AddSeconds(i))).Tip); }
        FeedbackTip? Third(DateTimeOffset at) => rules.Observe(BoardRead(at.AddSeconds(2))).Tip;
        FeedbackTip? Repeat(DateTimeOffset at, string requestId) =>
            rules.Observe(Call(at, RelayTools.TaskUpdate, new { repositoryId = "repo" }, target: "task:repo/default/T1", hash: "H", requestId: requestId)).Tip;

        Boards(Start);
        Assert.NotNull(Third(Start));
        // Another rule within the hour: no tip, even though its pattern fired.
        Assert.Null(Repeat(Start.AddMinutes(10), "r1"));
        Assert.Null(Repeat(Start.AddMinutes(11), "r2"));
        // After the hour, the other rule may tip.
        Assert.NotNull(Repeat(Start.AddMinutes(70), "r3"));
        // The board rule again after two hours: still within its day.
        Boards(Start.AddHours(3));
        Assert.Null(Third(Start.AddHours(3)));
        // A day later it may tip again.
        Boards(Start.AddHours(25));
        Assert.NotNull(Third(Start.AddHours(25)));
        // Another session has its own limits.
        for (var i = 0; i < 2; i++) rules.Observe(BoardRead(Start.AddHours(25).AddSeconds(i), "s-2"));
        Assert.Equal("s-2", rules.Observe(BoardRead(Start.AddHours(25).AddSeconds(3), "s-2")).Tip!.SessionId);

        // With tips off the rules give no tips at all.
        var off = new FeedbackRules(new FeedbackOptions(), Tokens);
        for (var i = 0; i < 6; i++) Assert.Null(off.Observe(BoardRead(Start.AddSeconds(i))).Tip);
    }

    [Fact]
    public async Task Outcomes_are_recorded_and_a_rule_followed_too_rarely_switches_itself_off_and_stays_off()
    {
        var store = new MemoryStore();
        var rules = new FeedbackRules(TipsOn, Tokens, store);
        var at = Start;
        FeedbackTip? Pattern(string session)
        {
            FeedbackTip? tip = null;
            for (var i = 0; i < 3; i++) tip = rules.Observe(BoardRead(at = at.AddSeconds(1), session)).Tip;
            return tip;
        }

        // Followed: no repeat within the hour after the tip.
        Assert.NotNull(Pattern("followed"));
        rules.Sweep(at.AddMinutes(61));
        Assert.Equal((1L, 1L, 0L), Totals(rules));

        // Not followed: the pattern repeats within the hour.
        at = at.AddMinutes(62);
        Assert.NotNull(Pattern("ignored-0"));
        at = at.AddMinutes(10);
        Assert.Null(Pattern("ignored-0"));
        Assert.Equal((2L, 1L, 1L), Totals(rules));

        // Eighteen more sessions ignore it: 1 followed out of 20 is 5%, under the 20% minimum, so the rule switches off.
        for (var i = 1; i <= 18; i++)
        {
            at = at.AddMinutes(1);
            Assert.NotNull(Pattern("ignored-" + i));
            Assert.Null(Pattern("ignored-" + i));
        }
        var board = rules.Rules.Single(rule => rule.Rule == FeedbackRuleNames.BoardReread);
        Assert.Equal((20L, 1L, 19L, false), (board.Tips, board.Followed, board.Ignored, board.Enabled));
        Assert.NotNull(board.DisabledAt);
        Assert.Null(Pattern("fresh-session"));
        await rules.FlushAsync();

        // The state is stored, so the rule stays off after a restart; the other rules are untouched.
        var restarted = new FeedbackRules(TipsOn, Tokens, store);
        Assert.False(restarted.Rules.Single(rule => rule.Rule == FeedbackRuleNames.BoardReread).Enabled);
        Assert.All(restarted.Rules.Where(rule => rule.Rule != FeedbackRuleNames.BoardReread), rule => Assert.True(rule.Enabled));
        for (var i = 0; i < 3; i++) Assert.Null(restarted.Observe(BoardRead(at.AddHours(30).AddSeconds(i), "after-restart")).Tip);

        // A rule followed often enough stays on past 20 outcomes.
        var kept = new FeedbackRules(TipsOn, Tokens);
        at = Start;
        for (var i = 0; i < 25; i++)
        {
            for (var read = 0; read < 3; read++) kept.Observe(BoardRead(at = at.AddSeconds(1), "kept-" + i));
            at = at.AddMinutes(1);
            if (i % 2 == 0) kept.Sweep(at.AddMinutes(61));
            else for (var read = 0; read < 3; read++) kept.Observe(BoardRead(at = at.AddSeconds(1), "kept-" + i));
        }
        kept.Sweep(at.AddHours(2));
        Assert.True(kept.Rules.Single(rule => rule.Rule == FeedbackRuleNames.BoardReread).Enabled);

        static (long Tips, long Followed, long Ignored) Totals(FeedbackRules rules) =>
            rules.Rules.Single(rule => rule.Rule == FeedbackRuleNames.BoardReread) is var state ? (state.Tips, state.Followed, state.Ignored) : default;
    }

    [Fact]
    public async Task Rule_state_survives_in_sqlite()
    {
        var path = Path.Combine(Path.GetTempPath(), "pkrelay-feedback-" + Guid.NewGuid().ToString("N"), "relay.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var off = new FeedbackRuleState(FeedbackRuleNames.InboxPolling, false, 21, 2, 19, Start);
        using (var store = new SqliteStore(path))
        {
            Assert.Empty(store.LoadFeedbackRules());
            await store.SaveFeedbackRuleAsync(off with { Enabled = true });
            await store.SaveFeedbackRuleAsync(off);
        }
        using (var reopened = new SqliteStore(path))
            Assert.Equal(off, Assert.Single(reopened.LoadFeedbackRules()));
    }

    [Fact]
    public async Task A_tip_is_delivered_as_a_lowest_tier_attention_item_on_the_next_boundary_reply()
    {
        await using var host = new RelayHostFixture(settings: new Dictionary<string, string?> { ["Relay:Feedback:Tips"] = "true" });
        using var client = host.AuthorizedClient();
        var session = Data(await host.Call(client, "session_open", new { repositoryId = "repo", requestId = "tip-open", role = "worker" }));
        object Args(object? extra = null)
        {
            var args = new JsonObject { ["repositoryId"] = "repo", ["sessionId"] = session.GetProperty("sessionId").GetString(), ["sessionToken"] = session.GetProperty("sessionToken").GetString() };
            if (extra is not null) foreach (var field in JsonNode.Parse(JsonSerializer.Serialize(extra))!.AsObject()) args[field.Key] = field.Value?.DeepClone();
            return args;
        }
        for (var i = 0; i < 3; i++) Assert.True((await host.Call(client, "task_get", Args())).GetProperty("ok").GetBoolean());

        // The tip waits for a boundary; a background read never shows it.
        JsonElement offer = default;
        for (var retry = 0; retry < 50 && offer.ValueKind != JsonValueKind.Object; retry++)
        {
            await Task.Delay(100);
            var background = Data(await host.Call(client, "check_inbox", Args(new { attention = false })));
            Assert.False(background.TryGetProperty("attention", out _));
            var status = Data(await host.Call(client, "get_status", Args(new { attentionDetail = true })));
            if (status.GetProperty("attention").GetProperty("actionable").GetInt32() == 0) continue;
            var inbox = Data(await host.Call(client, "check_inbox", Args()));
            if (inbox.TryGetProperty("attention", out var attached)) offer = attached;
        }
        Assert.Equal(JsonValueKind.Object, offer.ValueKind);
        Assert.Equal(AttentionKinds.Tip, offer.GetProperty("k").GetString());
        Assert.Equal("tip: Read the board as changes since your last revision.", offer.GetProperty("x").GetString());
        // A tip names no task, ranks in the lowest tier, and so never wakes a chat.
        Assert.False(offer.TryGetProperty("t", out _));
        Assert.Equal(AttentionWeights.LowestTier, AttentionWeights.Tier(AttentionKinds.Tip));
        Assert.False(AttentionWeights.Wakes(AttentionWeights.Tier(AttentionKinds.Tip)));
        // Tips are only ever attention items: no reply carries a tips field.
        Assert.False(Data(await host.Call(client, "task_get", Args())).TryGetProperty("tips", out _));

        // With tips off (the default) the same reads raise nothing.
        await using var quiet = new RelayHostFixture();
        using var quietClient = quiet.AuthorizedClient();
        var other = Data(await quiet.Call(quietClient, "session_open", new { repositoryId = "repo", requestId = "tip-quiet", role = "worker" }));
        var quietArgs = new { repositoryId = "repo", sessionId = other.GetProperty("sessionId").GetString(), sessionToken = other.GetProperty("sessionToken").GetString() };
        for (var i = 0; i < 4; i++) await quiet.Call(quietClient, "task_get", quietArgs);
        await Task.Delay(300);
        Assert.False(Data(await quiet.Call(quietClient, "check_inbox", quietArgs)).TryGetProperty("attention", out _));
        Assert.False(quiet.Services.GetRequiredService<FeedbackOptions>().Enabled);
    }

    private static JsonElement Data(JsonElement reply)
    {
        Assert.True(reply.GetProperty("ok").GetBoolean(), reply.ToString());
        return JsonDocument.Parse(reply.GetProperty("dataJson").GetString()!).RootElement.Clone();
    }

    private sealed class MemoryStore : IFeedbackRuleStore
    {
        private readonly Dictionary<string, FeedbackRuleState> rows = new(StringComparer.Ordinal);
        public IReadOnlyList<FeedbackRuleState> LoadFeedbackRules() { lock (rows) return rows.Values.ToArray(); }
        public Task SaveFeedbackRuleAsync(FeedbackRuleState state) { lock (rows) rows[state.Rule] = state; return Task.CompletedTask; }
    }
}
