using System.Text.Json.Nodes;
using CoCern.Contracts;
using static CoCern.Domain.JsonFields;

namespace CoCern.Domain.State;

/// <summary>A retained collection is independent of the lifetime of its contributing sessions.</summary>
public sealed class SessionContextState(JsonObject json)
{
    public JsonObject Json => json;
    public long Revision => Number(json, "revision");
    public bool Exists => Revision > 0 && json["deleted"]?.GetValue<bool>() != true;
    public JsonObject Entries => json["entries"] as JsonObject ?? new();
    public string RepositoryId => Text(json, "repositoryId");
    public string ContextId => Text(json, "contextId");
    public static string Key(string repository, string contextId) => $"{repository.Length}:{repository}{contextId}";
    public static bool ValidKind(string kind) => kind is "finding" or "decision" or "open_question" or "evidence" or "handoff";
    public JsonObject Manifest()
    {
        var pinned = new JsonArray();
        foreach (var id in (json["pinnedEntryIds"] as JsonArray ?? []).Select(x => x!.GetValue<string>()))
            if (Entries[id] is JsonObject entry) pinned.Add(new JsonObject
            {
                ["entryId"] = id, ["version"] = Number(entry, "version"), ["title"] = Text(entry, "title"), ["kind"] = Text(entry, "kind"),
                ["uri"] = SessionContextResourceUri.PartUri(RepositoryId, ContextId, id, Number(entry, "version"), 0)
            });
        return new JsonObject
        {
            ["contextId"] = ContextId, ["revision"] = Revision, ["title"] = Text(json, "title"), ["summary"] = Text(json, "summary"),
            ["nextSteps"] = json["nextSteps"]?.DeepClone() ?? new JsonArray(), ["taskIds"] = json["taskIds"]?.DeepClone() ?? new JsonArray(),
            ["pinnedEntries"] = pinned, ["entryCount"] = Entries.Count, ["updatedAt"] = json["updatedAt"]?.DeepClone(),
            ["uri"] = SessionContextResourceUri.ManifestUri(RepositoryId, ContextId), ["untrusted"] = "Retained reports, not current authority. Recheck tasks, claims and evidence."
        };
    }
}
