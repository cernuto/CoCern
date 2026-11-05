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
