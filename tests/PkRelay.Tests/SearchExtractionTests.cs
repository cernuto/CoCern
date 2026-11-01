using System.Text;
using System.Text.Json.Nodes;
using PkRelay.Contracts;
using PkRelay.Domain;
using Xunit;

namespace PkRelay.Tests;

public sealed class SearchExtractionTests
{
    private static readonly RelayCaller Caller = new("alice", true, ["repo"], true);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-29T00:00:00Z");

    private static RelayReply Call(StateEnvelope<CoordinationDocument> state, string kind, string op, JsonObject args, int seconds = 0)
    {
        args["repositoryId"] = "repo"; args["requestId"] ??= Guid.NewGuid().ToString("N");
        return CoordinationStateMachine.Apply(state, kind, "repo/default", Caller, op, args, Now.AddSeconds(seconds));
    }

    private static JsonObject Workflow(string state, JsonArray? blockers = null) => new()
    {
        ["state"] = state,
        ["packet"] = new JsonObject { ["goal"] = "Fix the refresh timeout", ["target"] = "auth service", ["authorityRef"] = "human:1", ["restoration"] = "r", ["stopCondition"] = "s", ["resources"] = new JsonArray("rig-1", "db-2") },
        ["criteria"] = new JsonArray(new JsonObject { ["id"] = "c", ["description"] = "d", ["result"] = "not_run" }),
        ["blockers"] = blockers ?? new JsonArray()
    };

    private static string Board(JsonObject tasks, JsonObject? context = null, JsonObject? standing = null)
    {
        var board = new JsonObject { ["repositoryId"] = "repo", ["tasks"] = tasks };
        if (context is not null) board["context"] = context;
        if (standing is not null) board["standing"] = standing;
        return board.ToJsonString();
    }

    private static SearchDocument Single(IReadOnlyList<SearchDocument> documents, string kind, string id) => Assert.Single(documents, x => x.Kind == kind && x.DocId == id);

    [Fact]
    public void Board_gives_one_document_per_task_and_context_note_and_none_for_standing()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        Assert.True(Call(state, "board", "task_update", new() { ["taskId"] = "T-1", ["expectedVersion"] = 0, ["workflow"] = Workflow("ready") }).Ok);
        var blocker = new JsonArray(new JsonObject { ["id"] = "b", ["kind"] = "provider_rejection", ["summary"] = "schema review pending", ["recheckCondition"] = "review posted" });
        var blocked = Workflow("blocked", blocker); blocked["dependsOn"] = new JsonArray("T-1");
        Assert.True(Call(state, "board", "task_update", new()
        {
            ["taskId"] = "T-2", ["expectedVersion"] = 0, ["workflow"] = blocked, ["notes"] = "token refresh fails after idle",
            ["patch"] = new JsonObject { ["title"] = "Refresh timeout follow-up" }, ["message"] = new JsonObject { ["text"] = "seen on rig-1 twice" }
        }, seconds: 5).Ok);
        Assert.True(Call(state, "board", "task_update", new() { ["taskId"] = "plain", ["expectedVersion"] = 0 }).Ok);
        Assert.True(Call(state, "board", "context_put", new() { ["contextId"] = "conventions", ["expectedVersion"] = 0, ["content"] = new JsonObject { ["build"] = "run Build-PkRelay.ps1 first" } }).Ok);
        Assert.True(Call(state, "board", "standing_put", new() { ["expectedRevision"] = 0, ["lines"] = new JsonArray(new JsonObject { ["id"] = "css", ["text"] = "Never deploy CSS without a restoration receipt" }) }).Ok);
        Assert.True(Call(state, "board", "standing_put", new() { ["expectedRevision"] = 1, ["grants"] = new JsonArray(new JsonObject { ["id"] = "rigs", ["scope"] = "resource", ["targets"] = new JsonArray("rig-1"), ["expiresAt"] = Now.AddDays(1).ToString("O") }) }).Ok);
        Assert.True(Call(state, "board", "standing_propose", new() { ["sessionId"] = "s1", ["line"] = new JsonObject { ["id"] = "pending", ["text"] = "Unapproved convention" } }).Ok);

        var documents = state.GetSearchDocuments("board");

        Assert.Equal(4, documents.Count);
        Assert.Equal(SearchKinds.All.Order().ToArray(), new[] { "context", "message", "task" });
        Assert.Equal(documents, SearchExtraction.Extract("board", JsonNode.Parse(state.Value.Json)!.AsObject()));
        Assert.All(documents, x => { Assert.Equal("repo", x.RepositoryId); Assert.Null(x.OwnerSessionId); });
        var ready = Single(documents, SearchKinds.Task, "T-1");
        Assert.Equal("Fix the refresh timeout", ready.Title);
        Assert.Equal("ready", ready.Status);
        Assert.Contains("auth service", ready.Body);
        Assert.Contains("resources rig-1 db-2", ready.Body);
        Assert.DoesNotContain("Fix the refresh timeout", ready.Body);
        var task = Single(documents, SearchKinds.Task, "T-2");
        Assert.Equal("Refresh timeout follow-up", task.Title);
        Assert.Equal("blocked", task.Status);
        Assert.Equal(Now.AddSeconds(5), task.UpdatedAt);
        foreach (var text in new[] { "Fix the refresh timeout", "blocked provider_rejection schema review pending review posted", "depends on T-1", "token refresh fails after idle", "seen on rig-1 twice" })
            Assert.Contains(text, task.Body);
        var plain = Single(documents, SearchKinds.Task, "plain");
        Assert.Equal(("plain", "open", ""), (plain.Title, plain.Status, plain.Body));
        var note = Single(documents, SearchKinds.Context, "conventions");
        Assert.Equal(("conventions", "note", "run Build-PkRelay.ps1 first"), (note.Title, note.Status, note.Body));
        // Standing already reaches every chat with session_open and milestone replies, so lines, grants and proposals are not indexed.
        Assert.DoesNotContain(documents, x => x.DocId is "css" or "rigs" or "pending" || x.Body.Contains("CSS") || x.Body.Contains("Unapproved"));
    }

    [Fact]
    public void Session_gives_one_owned_document_per_unacknowledged_message_with_text()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        Assert.True(Call(state, "session", "__open", new() { ["sessionId"] = "s1", ["principalId"] = "alice", ["sessionToken"] = "session-secret-value" }).Ok);
        Assert.True(Call(state, "session", "__deliver", new() { ["messageId"] = "m1", ["text"] = "Schema review is done\nSee T-118 for the details", ["senderSessionId"] = "s2", ["sentAt"] = Now.ToString("O") }).Ok);
        Assert.True(Call(state, "session", "__deliver", new() { ["messageId"] = "m2", ["text"] = "Second note", ["sentAt"] = Now.ToString("O") }).Ok);
        Assert.True(Call(state, "session", "__deliver", new() { ["messageId"] = "n1", ["taskId"] = "T-118", ["kind"] = "task.changed", ["taskVersion"] = 3 }).Ok);

        var documents = state.GetSearchDocuments("session");

        var message = Assert.Single(documents, x => x.DocId == "m1");
        Assert.Equal((SearchKinds.Message, "repo", "s1", "unread", Now), (message.Kind, message.RepositoryId, message.OwnerSessionId, message.Status, message.UpdatedAt));
        Assert.Equal("Schema review is done", message.Title);
        Assert.Equal("Schema review is done\nSee T-118 for the details", message.Body);
        // A task.changed notification has no text; the task it points to is indexed on its board.
        Assert.Equal(new[] { "m1", "m2" }, documents.Select(x => x.DocId).Order().ToArray());
        Assert.DoesNotContain(documents, x => (x.Title + x.Body).Contains("session-secret-value"));

        Assert.True(Call(state, "session", "ack", new() { ["sessionId"] = "s1", ["sessionToken"] = "session-secret-value", ["messageId"] = "m1" }).Ok);
        Assert.Equal("m2", Assert.Single(state.GetSearchDocuments("session")).DocId);
    }

    [Fact]
    public void Lock_control_discovery_unknown_and_unreadable_states_give_no_documents()
    {
        var board = Board(new JsonObject { ["t"] = new JsonObject { ["taskId"] = "t", ["messages"] = new JsonArray(new JsonObject { ["text"] = "hello" }) } });
        Assert.Single(SearchExtraction.Extract("board", board));
        foreach (var name in new[] { "lock", "control", "discovery", "other", "" }) Assert.Empty(SearchExtraction.Extract(name, board));
        foreach (var json in new[] { "", "not json", "[]", "null", "{\"tasks\":{\"t\":{}}}", "{\"repositoryId\":5,\"tasks\":{\"t\":{}}}" })
            Assert.Empty(SearchExtraction.Extract("board", json));
        var session = new JsonObject { ["repositoryId"] = "repo", ["inbox"] = new JsonObject { ["m"] = new JsonObject { ["text"] = "hi" } } };
        Assert.Empty(SearchExtraction.Extract("session", session.ToJsonString()));
        Assert.Empty(new StateEnvelope<CoordinationDocument>().GetSearchDocuments("board"));
    }

    [Fact]
    public void Bodies_and_titles_are_capped_without_splitting_characters()
    {
        var huge = string.Concat(Enumerable.Repeat("é🙂x", 40_000));
        var messages = new JsonArray(Enumerable.Range(0, 20).Select(i => (JsonNode)new JsonObject { ["text"] = $"message-{i} " + huge }).ToArray());
        var json = Board(new JsonObject
        {
            ["big"] = new JsonObject { ["taskId"] = "big", ["facts"] = new JsonObject { ["title"] = huge, ["notes"] = huge }, ["messages"] = messages },
            ["small"] = new JsonObject { ["taskId"] = "small", ["facts"] = new JsonObject { ["notes"] = "short" } }
        }, new JsonObject { ["note"] = new JsonObject { ["content"] = huge } });

        var documents = SearchExtraction.Extract("board", json);

        var big = Single(documents, SearchKinds.Task, "big");
        Assert.InRange(Encoding.UTF8.GetByteCount(big.Body), RelayLimits.MaxSearchBodyBytes - 4, RelayLimits.MaxSearchBodyBytes);
        Assert.InRange(Encoding.UTF8.GetByteCount(big.Title), RelayLimits.MaxSearchTitleBytes - 4, RelayLimits.MaxSearchTitleBytes);
        // Each part takes at most its own share, so the newest messages still get room after a huge note.
        Assert.All(big.Body.Split('\n'), part => Assert.True(Encoding.UTF8.GetByteCount(part) <= RelayLimits.MaxSearchFieldBytes));
        Assert.Contains("message-19", big.Body);
        Assert.InRange(Encoding.UTF8.GetByteCount(Single(documents, SearchKinds.Context, "note").Body), RelayLimits.MaxSearchFieldBytes - 4, RelayLimits.MaxSearchFieldBytes);
        foreach (var document in documents)
            foreach (var text in new[] { document.Title, document.Body })
                Assert.Equal(text, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text)));
        Assert.Equal("short", Single(documents, SearchKinds.Task, "small").Body);
    }

    [Fact]
    public void Only_the_latest_recent_entries_of_messages_and_reports_are_indexed()
    {
        var count = RelayLimits.RecentTaskEntries + 10;
        var task = new JsonObject
        {
            ["taskId"] = "t",
            ["messages"] = new JsonArray(Enumerable.Range(0, count).Select(i => (JsonNode)new JsonObject { ["text"] = $"msg{i:D2}", ["at"] = Now.AddMinutes(i).ToString("O") }).ToArray()),
            ["reports"] = new JsonArray(Enumerable.Range(0, count).Select(i => (JsonNode)new JsonObject { ["actionId"] = $"act{i:D2}", ["outcome"] = "completed", ["report"] = new JsonObject { ["summary"] = $"rep{i:D2}" }, ["at"] = Now.ToString("O") }).ToArray()),
            ["updatedAt"] = Now.ToString("O")
        };

        var document = Assert.Single(SearchExtraction.Extract("board", Board(new JsonObject { ["t"] = task })));

        for (var i = 0; i < count; i++)
        {
            var recent = i >= count - RelayLimits.RecentTaskEntries;
            Assert.Equal(recent, document.Body.Contains($"msg{i:D2}"));
            Assert.Equal(recent, document.Body.Contains($"act{i:D2} completed rep{i:D2}"));
        }
        // Newest first, so the body cap keeps the most recent entries; the latest message dates the task.
        Assert.True(document.Body.IndexOf($"msg{count - 1}", StringComparison.Ordinal) < document.Body.IndexOf($"msg{count - 2}", StringComparison.Ordinal));
        Assert.Equal(Now.AddMinutes(count - 1), document.UpdatedAt);
    }

    [Fact]
    public void Credential_like_fields_are_never_indexed()
    {
        var notes = new JsonObject
        {
            ["summary"] = "visible summary", ["apiKey"] = "leak-1", ["github_token"] = "leak-2", ["Authorization"] = "leak-3",
            ["nested"] = new JsonObject { ["db-password"] = "leak-4", ["items"] = new JsonArray(new JsonObject { ["clientSecret"] = "leak-5", ["kept"] = "visible item" }) }
        };
        var task = new JsonObject
        {
            ["taskId"] = "t", ["facts"] = new JsonObject { ["notes"] = notes },
            ["reports"] = new JsonArray(new JsonObject { ["actionId"] = "a", ["outcome"] = "failed", ["report"] = new JsonObject { ["error"] = "visible error", ["sessionToken"] = "leak-6", ["Cookie"] = "leak-7" } })
        };
        var context = new JsonObject { ["creds"] = new JsonObject { ["content"] = new JsonObject { ["host"] = "visible host", ["private_key"] = "leak-8", ["bearer"] = "leak-9" } } };

        var documents = SearchExtraction.Extract("board", Board(new JsonObject { ["t"] = task }, context));

        var text = string.Join("\n", documents.Select(x => x.Title + "\n" + x.Body));
        foreach (var visible in new[] { "visible summary", "visible item", "visible error", "visible host" }) Assert.Contains(visible, text);
        Assert.DoesNotContain("leak-", text);
        foreach (var name in new[] { "token", "accessToken", "API_KEY", "passwd", "Passphrase", "credentials", "x-auth-cookie", "privateKey" }) Assert.True(SearchExtraction.IsCredentialName(name), name);
        foreach (var name in new[] { "author", "summary", "key", "notes", "factKey", "keyboard" }) Assert.False(SearchExtraction.IsCredentialName(name), name);
    }

    [Fact]
    public void Unexpected_shapes_are_skipped_rather_than_thrown()
    {
        var tasks = new JsonObject
        {
            ["legacy"] = new JsonObject { ["taskId"] = "legacy", ["workflow"] = new JsonObject { ["state"] = "ready", ["dependsOn"] = "T-1", ["packet"] = new JsonObject { ["resources"] = 5 }, ["blockers"] = "none" }, ["facts"] = new JsonObject { ["title"] = 7, ["notes"] = new JsonArray(1, true, null, "kept") }, ["messages"] = new JsonObject() },
            ["odd"] = "not a task",
            ["flow"] = new JsonObject { ["taskId"] = "flow", ["workflow"] = "not an object", ["updatedAt"] = "not a date" }
        };
        var standing = new JsonObject { ["lines"] = new JsonArray("x"), ["grants"] = new JsonObject { ["g"] = new JsonObject { ["targets"] = "rig" } } };

        var documents = SearchExtraction.Extract("board", Board(tasks, new JsonObject { ["c"] = 3 }, standing));

        Assert.Equal(new[] { "flow", "legacy" }, documents.Select(x => x.DocId).Order().ToArray());
        var legacy = Single(documents, SearchKinds.Task, "legacy");
        Assert.Equal(("legacy", "ready", "1\nkept"), (legacy.Title, legacy.Status, legacy.Body));
        Assert.Equal(("open", DateTimeOffset.MinValue), (Single(documents, SearchKinds.Task, "flow").Status, Single(documents, SearchKinds.Task, "flow").UpdatedAt));
    }

    [Fact]
    public void Reports_and_messages_share_the_body_newest_first_across_both()
    {
        var large = new string('r', RelayLimits.MaxSearchFieldBytes);
        var task = new JsonObject
        {
            ["taskId"] = "t",
            ["reports"] = new JsonArray(new[] { 1, 3, 5, 7 }.Select(minute => (JsonNode)new JsonObject { ["actionId"] = $"act{minute}", ["outcome"] = "completed", ["report"] = large, ["at"] = Now.AddMinutes(minute).ToString("O") }).ToArray()),
            ["messages"] = new JsonArray(new[] { 2, 4, 6, 8 }.Select(minute => (JsonNode)new JsonObject { ["text"] = $"message at {minute}", ["at"] = Now.AddMinutes(minute).ToString("O") }).ToArray())
        };

        var body = Assert.Single(SearchExtraction.Extract("board", Board(new JsonObject { ["t"] = task }))).Body;

        // Four 1 KB reports would fill the 4 KB body on their own; interleaved by time, every newer message still gets in.
        var order = new[] { "message at 8", "act7", "message at 6", "act5", "message at 4", "act3", "message at 2" }.Select(text => body.IndexOf(text, StringComparison.Ordinal)).ToArray();
        Assert.DoesNotContain(-1, order);
        Assert.Equal(order.Order().ToArray(), order);
        Assert.True(Encoding.UTF8.GetByteCount(body) <= RelayLimits.MaxSearchBodyBytes);
    }

    [Fact]
    public void Changes_rewrite_only_changed_documents_and_list_removed_keys()
    {
        var board = new StateEnvelope<CoordinationDocument>();
        foreach (var id in new[] { "T-1", "T-2", "T-3" }) Assert.True(Call(board, "board", "task_update", new() { ["taskId"] = id, ["expectedVersion"] = 0, ["notes"] = "first " + id }).Ok);
        Assert.True(Call(board, "board", "context_put", new() { ["contextId"] = "n1", ["expectedVersion"] = 0, ["content"] = "note one" }).Ok);
        Assert.True(Call(board, "board", "context_put", new() { ["contextId"] = "n2", ["expectedVersion"] = 0, ["content"] = "note two" }).Ok);
        var initial = board.Value.Json;

        var first = SearchExtraction.Changes("board", null, initial);
        Assert.True(first.ReplaceAll);
        Assert.Equal(SearchExtraction.Extract("board", initial), first.Upserts);

        var before = board.Value.Json;
        var snapshot = new StateEnvelope<CoordinationDocument> { Value = new() { Json = before } };
        Assert.True(Call(board, "board", "send_message", new() { ["taskId"] = "T-2", ["text"] = "a new finding" }).Ok);
        Assert.True(Call(board, "board", "context_put", new() { ["contextId"] = "n2", ["expectedVersion"] = 1, ["content"] = "note two, revised" }).Ok);
        Assert.True(Call(board, "board", "task_get", new() { ["taskId"] = "T-1" }).Ok);

        var delta = board.GetSearchChanges("board", snapshot);
        Assert.False(delta.ReplaceAll);
        Assert.Equal(new[] { "context:n2", "task:T-2" }, delta.Upserts.Select(x => x.Kind + ":" + x.DocId).Order().ToArray());
        Assert.Empty(delta.Removed);
        Assert.Contains("a new finding", Single(delta.Upserts, SearchKinds.Task, "T-2").Body);
        Assert.Equal(SearchExtraction.ExtractTasks(JsonNode.Parse(board.Value.Json)!.AsObject(), ["T-2", "missing", "T-2"]), delta.Upserts.Where(x => x.Kind == SearchKinds.Task).ToArray());
        AssertApplies("board", before, board.Value.Json, delta);
        Assert.Empty(SearchExtraction.Changes("board", board.Value.Json, board.Value.Json).Upserts);

        // A task or note without its change marker (a legacy board) is compared whole, so an unchanged one is not rewritten.
        var legacy = JsonNode.Parse(board.Value.Json)!.AsObject();
        legacy["tasks"]!["T-3"]!.AsObject().Remove("changeRevision");
        legacy["context"]!["n1"]!.AsObject().Remove("version");
        Assert.Empty(SearchExtraction.Changes("board", legacy, legacy.DeepClone().AsObject()).Upserts);
        var edited = legacy.DeepClone().AsObject();
        edited["tasks"]!["T-3"]!["facts"]!["notes"] = "edited";
        Assert.Equal("T-3", Assert.Single(SearchExtraction.Changes("board", legacy, edited).Upserts).DocId);

        var session = new StateEnvelope<CoordinationDocument>();
        Assert.True(Call(session, "session", "__open", new() { ["sessionId"] = "s1", ["principalId"] = "alice", ["sessionToken"] = "secret" }).Ok);
        foreach (var id in new[] { "m1", "m2" }) Assert.True(Call(session, "session", "__deliver", new() { ["messageId"] = id, ["text"] = "text " + id }).Ok);
        var inbox = session.Value.Json;
        Assert.True(Call(session, "session", "ack", new() { ["sessionId"] = "s1", ["sessionToken"] = "secret", ["messageId"] = "m1" }).Ok);
        Assert.True(Call(session, "session", "__deliver", new() { ["messageId"] = "m3", ["text"] = "text m3" }).Ok);
        Assert.True(Call(session, "session", "__deliver", new() { ["messageId"] = "n1", ["taskId"] = "T-1", ["kind"] = "task.changed" }).Ok);
        var messages = SearchExtraction.Changes("session", inbox, session.Value.Json);
        Assert.False(messages.ReplaceAll);
        Assert.Equal("m3", Assert.Single(messages.Upserts).DocId);
        Assert.Equal(new SearchDocumentKey(SearchKinds.Message, "m1"), Assert.Single(messages.Removed));
        AssertApplies("session", inbox, session.Value.Json, messages);

        // Clearing a state, or a state whose repository cannot be read, replaces every row with nothing.
        foreach (var cleared in new[] { SearchExtraction.Changes("board", before, null), SearchExtraction.Changes("board", before, "{}") })
            Assert.Equal((true, 0, 0), (cleared.ReplaceAll, cleared.Upserts.Count, cleared.Removed.Count));
        Assert.Equal(SearchDelta.None, SearchExtraction.Changes("lock", before, board.Value.Json));
    }

    [Fact]
    public void Update_compares_markers_with_the_index_and_lists_what_it_no_longer_holds()
    {
        var board = new StateEnvelope<CoordinationDocument>();
        foreach (var id in new[] { "T-1", "T-2" }) Assert.True(Call(board, "board", "task_update", new() { ["taskId"] = id, ["expectedVersion"] = 0, ["notes"] = "first " + id }).Ok);
        var state = JsonNode.Parse(board.Value.Json)!.AsObject();
        var markers = SearchExtraction.Markers("board", state);
        Assert.Equal(new[] { "r:1", "r:2" }, markers.Values.Order().ToArray());

        var first = SearchExtraction.Update("board", state, new Dictionary<SearchDocumentKey, string>());
        Assert.Equal(2, first.Upserts.Count);
        Assert.Equal(SearchExtraction.Extract("board", state), first.Upserts.Select(x => x.Document));
        Assert.Equal(markers.Values.Order(), first.Upserts.Select(x => x.Marker).Order());

        // Only a key with a different marker is rebuilt; a key the state no longer has is removed.
        var indexed = new Dictionary<SearchDocumentKey, string>(markers) { [new(SearchKinds.Task, "T-2")] = "stale", [new(SearchKinds.Task, "gone")] = "r:9" };
        var update = SearchExtraction.Update("board", state, indexed);
        Assert.Equal("T-2", Assert.Single(update.Upserts).Document.DocId);
        Assert.Equal(new SearchDocumentKey(SearchKinds.Task, "gone"), Assert.Single(update.Removed));
        Assert.Empty(SearchExtraction.Update("board", state, markers).Upserts);

        // A cleared, unreadable or unindexed state removes everything the index holds.
        Assert.Equal(2, SearchExtraction.Update("board", (JsonObject?)null, markers).Removed.Count);
        Assert.Equal(2, SearchExtraction.Update("board", "not json", markers).Removed.Count);
        Assert.Equal(2, SearchExtraction.Update("lock", state, markers).Removed.Count);
        // Without a change counter (a legacy item), the marker is a hash of the item, stable until the item changes.
        state["tasks"]!["T-1"]!.AsObject().Remove("changeRevision");
        var legacy = SearchExtraction.Markers("board", state)[new(SearchKinds.Task, "T-1")];
        Assert.StartsWith("h:", legacy);
        Assert.Equal(legacy, SearchExtraction.Markers("board", JsonNode.Parse(state.ToJsonString())!.AsObject())[new(SearchKinds.Task, "T-1")]);
        Assert.True(Call(board, "board", "task_get", new() { ["taskId"] = "T-1" }).Ok);
        Assert.Empty(board.GetSearchUpdate("board", SearchExtraction.Markers("board", JsonNode.Parse(board.Value.Json)!.AsObject())).Upserts);
    }

    /// <summary>Applying the delta to an index built from the previous state gives exactly the index of the current state.</summary>
    private static void AssertApplies(string stateName, string previous, string current, SearchDelta delta)
    {
        var index = SearchExtraction.Extract(stateName, previous).ToDictionary(x => new SearchDocumentKey(x.Kind, x.DocId));
        foreach (var key in delta.Removed) Assert.True(index.Remove(key));
        foreach (var document in delta.Upserts) index[new SearchDocumentKey(document.Kind, document.DocId)] = document;
        var expected = SearchExtraction.Extract(stateName, current).ToDictionary(x => new SearchDocumentKey(x.Kind, x.DocId));
        Assert.Equal(expected.OrderBy(x => x.Key.Kind + ":" + x.Key.DocId).ToArray(), index.OrderBy(x => x.Key.Kind + ":" + x.Key.DocId).ToArray());
    }
}
