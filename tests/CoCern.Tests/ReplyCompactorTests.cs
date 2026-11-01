using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Adapter;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Host;
using Xunit;

namespace CoCern.Tests;

/// <summary>Replies over their token budget shrink in fixed steps, never lose required fields, and always say how to get the rest.</summary>
public sealed class ReplyCompactorTests
{
    private static readonly ITokenCounter Tokens = new O200kTokenCounter();

    private static RelayTool Tool(string name, int budget) => RelayToolCatalog.TryGet(name, out var tool) ? tool with { TokenBudget = budget } : throw new InvalidOperationException(name);

    private static JsonObject Data(RelayReply reply) => JsonNode.Parse(reply.DataJson)!.AsObject();

    private static string Words(int count, string word = "evidence") => string.Join(' ', Enumerable.Repeat(word, count));

    /// <summary>A whole-task read: required identity and workflow, plus long lists of messages and reports that are paged sections.</summary>
    private static RelayReply TaskRead(int messages, int reportWords = 40) => Replies.Ok(new
    {
        taskId = "T12", version = 7, owner = "w-1", claimGeneration = 3, leaseUntil = "2026-11-01T12:00:00+00:00",
        workflow = new { state = "ready", priority = 1 },
        messages = Enumerable.Range(1, messages).Select(i => new { messageId = "m" + i, text = Words(30, "message") }).ToArray(),
        reports = Enumerable.Range(1, 3).Select(i => new { actionId = "a" + i, outcome = "succeeded", evidence = Words(reportWords) }).ToArray(),
        attemptHistory = Array.Empty<object>()
    }, 7);

    [Fact]
    public void A_reply_within_budget_or_of_a_measure_only_tool_goes_out_unchanged()
    {
        var reply = TaskRead(messages: 20);
        var count = Tokens.Count(reply.DataJson);
        var fits = ReplyCompactor.Compact(reply, Tool(RelayTools.TaskGet, count), Tokens, enforce: true);
        Assert.Same(reply, fits.Reply);
        Assert.Equal((count, 0, false), (fits.Tokens, fits.Step, fits.OverBudget));

        // Measure-only: counted and flagged as over, but the very same reply goes out.
        var measured = ReplyCompactor.Compact(reply, Tool(RelayTools.TaskGet, 50), Tokens, enforce: false);
        Assert.Same(reply, measured.Reply);
        Assert.True(measured.OverBudget);
        Assert.Equal(0, measured.Step);

        // Errors are never compacted: their details are required.
        var error = Replies.Error(RelayErrorCodes.VersionConflict, new { currentVersion = 9, detail = Words(400) });
        Assert.Same(error, ReplyCompactor.Compact(error, Tool(RelayTools.TaskGet, 10), Tokens, enforce: true).Reply);
    }

    [Fact]
    public void Each_step_runs_in_order_until_the_reply_fits()
    {
        // Step 1: the declared detail (messages, reports, attempt history) goes, and is fetched with task_get section.
        var full = TaskRead(messages: 20);
        var stepOne = ReplyCompactor.Compact(full, Tool(RelayTools.TaskGet, 120), Tokens, enforce: true);
        var one = Data(stepOne.Reply);
        Assert.Equal(1, stepOne.Step);
        Assert.True(stepOne.Tokens <= 120, stepOne.Tokens.ToString());
        Assert.False(one.ContainsKey("messages") || one.ContainsKey("reports") || one.ContainsKey("attemptHistory"));
        Assert.True(one["compacted"]!.GetValue<bool>());
        Assert.Equal(1, one["compactStep"]!.GetValue<int>());
        Assert.True(one["detailFetchRequired"]!.GetValue<bool>());

        // Step 2: a board list has no detail fields, so its list is shortened to its first items, with more and the next cursor.
        var board = Replies.Page(Enumerable.Range(1, 30).Select(i => (JsonNode)new JsonObject { ["taskId"] = $"T{i}", ["title"] = Words(12, "title") }), new JsonObject { ["limit"] = 30 });
        var stepTwo = ReplyCompactor.Compact(board, Tool(RelayTools.TaskGet, 300), Tokens, enforce: true);
        var two = Data(stepTwo.Reply);
        Assert.Equal(2, stepTwo.Step);
        Assert.True(stepTwo.Tokens <= 300);
        var kept = two["items"]!.AsArray().Count;
        Assert.InRange(kept, 1, 29);
        Assert.Equal(30 - kept, two["more"]!.GetValue<long>());
        Assert.Equal(kept, two["nextCursor"]!.GetValue<long>());
        Assert.True(two["truncated"]!.GetValue<bool>());
        Assert.Equal(30, two["total"]!.GetValue<int>());
        Assert.Equal("T1", two["items"]![0]!["taskId"]!.GetValue<string>());

        // Step 3: one long text with no detail field or list left becomes its first line; its ID stays to fetch the rest.
        var note = Replies.Ok(new { contextId = "design", version = 4, content = "Decision: keep the byte cap.\n" + Words(300) }, 4);
        var stepThree = ReplyCompactor.Compact(note, Tool(RelayTools.ContextGet, 40), Tokens, enforce: true);
        var three = Data(stepThree.Reply);
        Assert.Equal(3, stepThree.Step);
        Assert.Equal("Decision: keep the byte cap.…", three["content"]!.GetValue<string>());
        Assert.Equal("design", three["contextId"]!.GetValue<string>());
        Assert.Equal(4, three["version"]!.GetValue<int>());
        Assert.True(three["detailFetchRequired"]!.GetValue<bool>());
        Assert.Equal(3, three["compactStep"]!.GetValue<int>());

        // A step is only reached when the one before it was not enough: a task read with long reports needs steps 1 and 3 is still step 1
        // when step 1 alone fits.
        Assert.Equal(1, ReplyCompactor.Compact(TaskRead(messages: 20, reportWords: 400), Tool(RelayTools.TaskGet, 120), Tokens, enforce: true).Step);
    }

    [Fact]
    public void A_list_without_a_cursor_is_never_cut()
    {
        // Search hits have no cursor to fetch the rest with, so step 2 leaves them whole; only long text could be shortened.
        var hits = Replies.Ok(new { hits = Enumerable.Range(1, 10).Select(i => new { id = $"T{i}", k = "task", s = "open", x = "deploy [rollback] runbook step " + i }).ToArray() });
        var search = ReplyCompactor.Compact(hits, Tool(RelayTools.Search, 50), Tokens, enforce: true);
        Assert.Same(hits, search.Reply);
        Assert.True(search.OverBudget);
        // A list beside a paged one is not cut either, and neither is "items" when the reply has no cursor.
        var unpaged = Replies.Ok(new { items = Enumerable.Range(1, 10).Select(i => new { messageId = $"m{i}", text = "short" }).ToArray() });
        Assert.Same(unpaged, ReplyCompactor.Compact(unpaged, Tool(RelayTools.CheckInbox, 20), Tokens, enforce: true).Reply);
    }

    [Fact]
    public void Required_fields_are_never_removed_or_changed()
    {
        var reply = Replies.Ok(new
        {
            taskId = "T" + Words(60, "x").Replace(" ", ""), version = 3, boardRevision = 12, code = "Ok", etag = "abc", claimGeneration = 2,
            resourceIds = Enumerable.Range(1, 8).Select(i => "resource-" + i).ToArray(), unresolvedActions = new[] { new { actionId = "build", status = Words(80) } },
            attention = new { id = "a-1", k = "tip", t = 4, x = Words(30), more = 0 }, standing = new { revision = 2, lines = new[] { new { id = "l1", text = Words(40) } } },
            notes = Words(300), items = Enumerable.Range(1, 10).Select(i => new { messageId = "m" + i, text = Words(40) }).ToArray()
        }, 3);
        var compacted = Data(ReplyCompactor.Compact(reply, Tool(RelayTools.CheckInbox, 30), Tokens, enforce: true).Reply);
        var original = Data(reply);
        foreach (var key in new[] { "taskId", "version", "boardRevision", "code", "etag", "claimGeneration", "resourceIds", "unresolvedActions", "attention", "standing" })
            Assert.True(JsonNode.DeepEquals(original[key], compacted[key]), key);
        // Every message keeps its ID while its text is cut to a first line.
        Assert.All(compacted["items"]!.AsArray(), item => Assert.StartsWith("m", item!["messageId"]!.GetValue<string>()));
        Assert.True(ReplyCompactor.IsRequired("sessionId") && ReplyCompactor.IsRequired("expectedVersion") && ReplyCompactor.IsRequired("nextCursor") && !ReplyCompactor.IsRequired("text"));
    }

    [Fact]
    public void A_mutation_compacts_only_its_optional_detail_and_never_its_result()
    {
        var workflow = new { state = "ready", goal = Words(500), packet = new { resources = new[] { "bench" } } };
        var reply = Replies.Ok(new
        {
            taskId = "T1", version = 8, changeRevision = 31, notes = Words(400),
            nextWork = new { ok = true, offer = new { taskId = "T2", snapshotRevision = 31, workflow } }
        }, 8);
        var result = ReplyCompactor.Compact(reply, Tool(RelayTools.TaskUpdate, 60), Tokens, enforce: true);
        var data = Data(result.Reply);
        Assert.Equal(1, result.Step);
        // Still over budget: a mutation never goes on to shorten its result, and the byte limit stays the safety net.
        Assert.True(result.OverBudget);
        Assert.Null(data["nextWork"]!["offer"]!["workflow"]);
        Assert.Equal("T2", data["nextWork"]!["offer"]!["taskId"]!.GetValue<string>());
        Assert.Equal(Words(400), data["notes"]!.GetValue<string>());
        foreach (var key in new[] { "taskId", "version", "changeRevision", "notes" }) Assert.True(JsonNode.DeepEquals(Data(reply)[key], data[key]), key);

        // A mutation without detail to drop is sent unchanged.
        var plain = Replies.Ok(new { taskId = "T1", version = 9, notes = Words(400) }, 9);
        Assert.Same(plain, ReplyCompactor.Compact(plain, Tool(RelayTools.TaskUpdate, 60), Tokens, enforce: true).Reply);
    }

    [Fact]
    public void A_shortened_page_continues_from_its_cursor()
    {
        var messages = Enumerable.Range(1, 40).Select(i => (JsonNode)new JsonObject { ["messageId"] = $"m{i}", ["text"] = Words(25, "inbox") }).ToArray();
        JsonObject Read(long cursor) => Data(Replies.Page(messages, new JsonObject { ["cursor"] = cursor, ["limit"] = 30 }));
        var tool = Tool(RelayTools.CheckInbox, 400);
        var seen = new List<string>();
        long cursor = 5;
        for (var page = 0; page < 40 && cursor < messages.Length; page++)
        {
            var full = Replies.Ok(Read(cursor));
            var compacted = Data(ReplyCompactor.Compact(full, tool, Tokens, enforce: true, cursor).Reply);
            seen.AddRange(compacted["items"]!.AsArray().Select(item => item!["messageId"]!.GetValue<string>()));
            if (compacted["nextCursor"] is not JsonValue next) break;
            cursor = next.GetValue<long>();
        }
        // Following nextCursor from 5 reads every later message once, in order, with none skipped.
        Assert.Equal(Enumerable.Range(6, 35).Select(i => $"m{i}"), seen);
    }

    [Fact]
    public void A_compacted_read_is_the_same_on_retry_and_has_its_own_etag()
    {
        var full = TaskRead(messages: 20);
        var first = ReplyCompactor.Compact(full, Tool(RelayTools.TaskGet, 120), Tokens, enforce: true).Reply;
        var again = ReplyCompactor.Compact(full, Tool(RelayTools.TaskGet, 120), Tokens, enforce: true).Reply;
        Assert.Equal(first.DataJson, again.DataJson);
        Assert.Equal(first.Version, again.Version);
        // ifNoneMatch with the full copy's etag can never claim the compacted reply is that copy, or the other way round.
        Assert.NotEqual(RelayEtag.Of(full.DataJson), RelayEtag.Of(first.DataJson));
    }

    [Fact]
    public async Task The_adapter_never_answers_unchanged_for_a_compacted_copy_of_a_full_read()
    {
        var full = TaskRead(messages: 20).DataJson;
        var compacted = ReplyCompactor.Compact(TaskRead(messages: 20), Tool(RelayTools.TaskGet, 120), Tokens, enforce: true).Reply.DataJson;
        var hub = new SequenceReply(full, compacted);
        using var client = new HttpClient(hub);
        var forwarder = new RelayForwarder(client, new AdapterOptions("http://127.0.0.1:5271", "client-secret"));
        var first = await ConditionalRead.ForwardAsync(forwarder, RelayTools.TaskGet, JsonSerializer.SerializeToElement(new { repositoryId = "repo", taskId = "T12" }), CancellationToken.None);
        var etag = first.Data!.Value.GetProperty("etag").GetString()!;
        var second = await ConditionalRead.ForwardAsync(forwarder, RelayTools.TaskGet, JsonSerializer.SerializeToElement(new { repositoryId = "repo", taskId = "T12", ifNoneMatch = etag }), CancellationToken.None);
        Assert.False(second.Data!.Value.TryGetProperty("unchanged", out _));
        Assert.True(second.Data.Value.GetProperty("compacted").GetBoolean());
        Assert.NotEqual(etag, second.Data.Value.GetProperty("etag").GetString());
        // The hub sees only the etag, under its own name; ifNoneMatch itself never reaches it.
        Assert.Contains("\"knownEtag\":\"" + etag + "\"", hub.LastBody);
        Assert.DoesNotContain("ifNoneMatch", hub.LastBody);
    }

    [Fact]
    public void A_reply_still_too_big_after_the_last_step_falls_to_the_byte_limit()
    {
        // Everything here is required, so no step can shrink it below the byte limit.
        var reply = Replies.Ok(new { taskId = "T1", resourceIds = Enumerable.Range(1, 3_000).Select(i => "resource-with-a-long-name-" + i).ToArray() });
        var result = ReplyCompactor.Compact(reply, Tool(RelayTools.TaskGet, 100), Tokens, enforce: true);
        Assert.True(result.OverBudget);
        Assert.Same(reply, result.Reply);
        Assert.Equal(RelayErrorCodes.PayloadTooLarge, ToolResults.Bound(Replies.BoundWire(result.Reply)).Code);

        // A reply that steps 1 to 3 shrink but not under the budget is still sent compacted, as long as it fits the byte limit.
        var long_ = Replies.Ok(new { taskId = "T1", resourceIds = Enumerable.Range(1, 200).Select(i => "resource-" + i).ToArray(), notes = Words(400) });
        var partly = ReplyCompactor.Compact(long_, Tool(RelayTools.TaskGet, 100), Tokens, enforce: true);
        Assert.True(partly.OverBudget);
        Assert.Equal(3, partly.Step);
        Assert.True(Data(partly.Reply)["compacted"]!.GetValue<bool>());
        Assert.True(ToolResults.Bound(Replies.BoundWire(partly.Reply)).Ok);
    }

    private sealed class SequenceReply(params string[] data) : HttpMessageHandler
    {
        private int next;
        public string LastBody = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            var body = data[Math.Min(next++, data.Length - 1)];
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { ok = true, code = "Ok", dataJson = body, version = 7 }), System.Text.Encoding.UTF8, "application/json") };
        }
    }
}
