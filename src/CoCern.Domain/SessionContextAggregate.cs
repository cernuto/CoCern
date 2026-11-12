using System.Text;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain.State;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Domain;

/// <summary>Explicit, repository-shared knowledge. Inbox reads and acknowledgments never reach this aggregate.</summary>
internal static class SessionContextAggregate
{
    public static RelayReply Handle(AggregateContext context)
    {
        var state = new SessionContextState(context.State);
        var id = Text(context.Args, "contextId");
        if (!SessionContextLimits.ValidId(id) || context.Key != SessionContextState.Key(context.Repository, id)) return Error(RelayErrorCodes.InvalidArguments);
        if (context.Operation == RelayTools.SessionContextGet)
            return state.Exists ? Ok(state.Manifest(), state.Revision) : Error(RelayErrorCodes.NotFound);
        if (context.Operation != RelayTools.SessionContextPut) return Error(RelayErrorCodes.UnknownTool);
        if (context.Args["expectedVersion"] is null || Number(context.Args, "expectedVersion") != state.Revision) return VersionConflict(state.Revision);
        if (state.Json["deleted"]?.GetValue<bool>() == true) return Error(RelayErrorCodes.NotFound);
        var revision = checked(state.Revision + 1);
        if (state.Revision == 0) context.State["generation"] = Guid.NewGuid().ToString("N");
        var forget = context.Args["forget"]?.GetValue<bool>() == true;
        if (forget)
        {
            if (context.Args.ContainsKey("patch") || state.Revision == 0) return Error(RelayErrorCodes.InvalidArguments);
            foreach (var name in new[] { "title", "summary", "nextSteps", "taskIds", "pinnedEntryIds", "entries" }) context.State.Remove(name);
            context.State["deleted"] = true;
        }
        else
        {
            if (context.Args["patch"] is not JsonObject patch || Encoding.UTF8.GetByteCount(patch.ToJsonString()) > SessionContextLimits.PatchBytes) return Error(RelayErrorCodes.InvalidArguments);
            if (patch.Any(x => x.Key is not ("title" or "summary" or "nextSteps" or "taskIds" or "pinnedEntryIds" or "upserts" or "deleteEntryIds"))) return Error(RelayErrorCodes.InvalidArguments);
            var upserts = patch["upserts"] as JsonArray ?? new();
            if (patch.ContainsKey("upserts") && patch["upserts"] is not JsonArray) return Error(RelayErrorCodes.InvalidArguments);
            var deletes = StringList(patch, "deleteEntryIds", SessionContextLimits.Changes, true);
            if (upserts.Count + deletes.Length > SessionContextLimits.Changes) return Error(RelayErrorCodes.PayloadTooLarge);
            var entries = (JsonObject)state.Entries.DeepClone();
            var changed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in upserts)
            {
                if (value is not JsonObject entry || entry.Any(x => x.Key is not ("entryId" or "kind" or "title" or "text" or "sourceRefs"))) return Error(RelayErrorCodes.InvalidArguments);
                var entryId = Text(entry, "entryId");
                var text = Text(entry, "text");
                var title = Text(entry, "title");
                if (!SessionContextLimits.ValidId(entryId) || !changed.Add(entryId) || !SessionContextState.ValidKind(Text(entry, "kind"))
                    || title.Length is 0 or > SessionContextLimits.TitleLength || text.Length == 0 || Encoding.UTF8.GetByteCount(text) > SessionContextLimits.EntryBytes) return Error(RelayErrorCodes.InvalidArguments);
                var refs = StringList(entry, "sourceRefs", SessionContextLimits.References, false);
                entries[entryId] = new JsonObject
                {
                    ["entryId"] = entryId, ["kind"] = Text(entry, "kind"), ["title"] = title, ["text"] = text, ["version"] = revision,
                    ["sourceRefs"] = new JsonArray(refs.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                    ["author"] = context.Caller.PrincipalId, ["sourceSessionId"] = context.SessionId, ["updatedAt"] = context.Now.ToString("O")
                };
            }
            foreach (var deleted in deletes) { if (!changed.Add(deleted)) return Error(RelayErrorCodes.InvalidArguments); entries.Remove(deleted); }
            if (entries.Count > SessionContextLimits.Entries) return Error(RelayErrorCodes.CapacityExceeded);
            foreach (var name in new[] { "title", "summary" })
                if (patch.ContainsKey(name))
                {
                    if (patch[name] is not JsonValue value || !value.TryGetValue<string>(out var text) || name == "title" && text.Length is 0 or > SessionContextLimits.TitleLength) return Error(RelayErrorCodes.InvalidArguments);
                    context.State[name] = text;
                }
            foreach (var name in new[] { "nextSteps", "taskIds", "pinnedEntryIds" })
                if (patch.ContainsKey(name)) context.State[name] = new JsonArray(StringList(patch, name, SessionContextLimits.References, name == "pinnedEntryIds").Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
            if ((context.State["pinnedEntryIds"] as JsonArray ?? []).Any(x => !entries.ContainsKey(x!.GetValue<string>()))) return Error(RelayErrorCodes.InvalidArguments);
            if (Text(context.State, "title").Length == 0) return Error(RelayErrorCodes.InvalidArguments);
            context.State["entries"] = entries;
        }
        context.State["contextId"] = id;
        context.State["revision"] = revision;
        context.State["updatedAt"] = context.Now.ToString("O");
        return Ok(new { contextId = id, revision, forgotten = forget, uri = SessionContextResourceUri.ManifestUri(context.Repository, id) }, revision);
    }

    private static string[] StringList(JsonObject value, string name, int limit, bool ids)
    {
        if (!value.ContainsKey(name)) return [];
        if (value[name] is not JsonArray list || list.Count > limit) throw new ArgumentException($"Invalid {name}.");
        var result = list.Select(x => x is JsonValue v && v.TryGetValue<string>(out var text) && text.Length is > 0 and <= 1024
            && !text.Any(char.IsControl) && (!ids || SessionContextLimits.ValidId(text)) ? text : throw new ArgumentException($"Invalid {name}.")).ToArray();
        if (result.Distinct(StringComparer.Ordinal).Count() != result.Length) throw new ArgumentException($"Repeated {name}.");
        return result;
    }
}
