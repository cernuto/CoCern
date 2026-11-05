using System.Text;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain;
using Xunit;

namespace CoCern.Tests;

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

    private static IReadOnlyDictionary<SearchDocumentKey, string> Markers(string stateName, string json) => SearchExtraction.Markers(stateName, JsonNode.Parse(json)!.AsObject());

    /// <summary>Applying the update to an index built from the previous state gives exactly the index of the current state.</summary>
    private static void AssertApplies(string stateName, string previous, string current, SearchUpdate update)
    {
        var index = SearchExtraction.Extract(stateName, previous).ToDictionary(x => new SearchDocumentKey(x.Kind, x.DocId));
        foreach (var key in update.Removed) Assert.True(index.Remove(key));
        foreach (var document in update.Upserts.Select(x => x.Document)) index[new SearchDocumentKey(document.Kind, document.DocId)] = document;
        var expected = SearchExtraction.Extract(stateName, current).ToDictionary(x => new SearchDocumentKey(x.Kind, x.DocId));
        Assert.Equal(expected.OrderBy(x => x.Key.Kind + ":" + x.Key.DocId).ToArray(), index.OrderBy(x => x.Key.Kind + ":" + x.Key.DocId).ToArray());
    }
}
