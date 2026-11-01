using System.Text.Json.Nodes;
using PkRelay.Contracts;
using PkRelay.Domain;

namespace PkRelay.Core;

/// <summary>What compacting one reply did: the reply to send, its token count, the step reached (0 for none), and whether it is over budget.</summary>
public readonly record struct Compaction(RelayReply Reply, int Tokens, int Step, bool OverBudget);

/// <summary>
/// Shrinks a reply that is over its tool's token budget, in fixed steps, until it fits:
/// <list type="number">
/// <item>drop the tool's declared detail fields and set <c>detailFetchRequired</c>;</item>
/// <item>shorten lists to their first items, with <c>truncated</c>, a <c>more</c> count and, for a paged list, the next cursor;</item>
/// <item>shorten remaining long text to its first line, with <c>detailFetchRequired</c>; the record's ID stays to fetch the rest.</item>
/// </list>
/// Required fields (IDs, versions, revisions, codes, cursors, grants, the attention offer and standing) are never removed or changed. A
/// mutation compacts only its declared detail, so its result fields are never touched; errors are never compacted. Every compacted reply
/// says <c>compacted: true</c> and <c>compactStep</c>. Compaction is a pure function of the reply, so a retried request gets the same
/// compacted reply. A reply still over budget after the last step is left to the byte limit.
/// </summary>
public static class ReplyCompactor
{
    /// <summary>Keys whose values are never removed or changed, nor anything under them.</summary>
    private static readonly HashSet<string> Required = new(StringComparer.Ordinal)
    {
        "ok", "code", "error", "errors", "detail", "reason", "field", "version", "revision", "etag", "generation", "claimGeneration", "leaseUntil",
        "nextCursor", "cursor", "total", "truncated", "more", "compacted", "compactStep", "detailFetchRequired", "grants", "resourceIds",
        "unresolvedActions", "refs", "sections", "complete", "newlyAcquired", "replayAllowed", "attention", "standing", "workflow"
    };

    /// <summary>A required key: one of <see cref="Required"/>, or an ID, version or revision by its name.</summary>
    public static bool IsRequired(string key) =>
        Required.Contains(key) || key.EndsWith("Id", StringComparison.Ordinal) || key.EndsWith("Ids", StringComparison.Ordinal)
        || key.EndsWith("Version", StringComparison.Ordinal) || key.EndsWith("Revision", StringComparison.Ordinal);

    /// <summary>
    /// Counts the reply and, when <paramref name="enforce"/> is set and it is over <paramref name="tool"/>'s budget, compacts it.
    /// <paramref name="cursor"/> is the request's paging cursor, so a shortened page can say where to continue.
    /// </summary>
    public static Compaction Compact(RelayReply reply, RelayTool tool, ITokenCounter tokens, bool enforce, long cursor = 0)
    {
        var count = tokens.Count(reply.DataJson);
        var budget = tool.TokenBudget;
        if (count <= budget) return new(reply, count, 0, false);
        if (!enforce || !reply.Ok || JsonNode.Parse(reply.DataJson) is not JsonObject data) return new(reply, count, 0, true);

        var step = 0;
        Compaction Result() { var compacted = reply with { DataJson = data.ToJsonString() }; var measured = tokens.Count(compacted.DataJson); return new(compacted, measured, step, measured > budget); }
        bool Fits() { Mark(data, step); return tokens.Count(data.ToJsonString()) <= budget; }

        if (DropDetail(data, tool.DetailFields))
        {
            step = 1;
            data["detailFetchRequired"] = true;
            if (Fits()) return Result();
        }
        // A mutation's result is what happened; only its optional detail may go.
        if (tool.IsMutation) return step == 0 ? new(reply, count, 0, true) : Result();

        var lists = data.Where(property => property.Value is JsonArray { Count: > 1 } && !IsRequired(property.Key))
            .OrderByDescending(property => property.Value!.ToJsonString().Length).Select(property => property.Key).ToArray();
        long dropped = 0;
        foreach (var name in lists)
        {
            step = 2;
            var items = data[name]!.AsArray().Select(item => item?.DeepClone()).ToArray();
            void Keep(int kept)
            {
                data[name] = new JsonArray(items.Take(kept).Select(item => item?.DeepClone()).ToArray());
                data["truncated"] = true;
                data["more"] = dropped + items.Length - kept;
                // A paged list continues from the first item left out.
                if (name == "items" && data.ContainsKey("nextCursor")) data["nextCursor"] = cursor + kept;
            }
            // The most items that fit, found by halving; at least one item stays.
            int low = 1, high = items.Length - 1;
            while (low < high)
            {
                var middle = (low + high + 1) / 2;
                Keep(middle);
                if (Fits()) low = middle; else high = middle - 1;
            }
            Keep(low);
            dropped += items.Length - low;
            if (Fits()) return Result();
        }

        if (ShortenText(data))
        {
            step = 3;
            data["detailFetchRequired"] = true;
        }
        Mark(data, step);
        return step == 0 ? new(reply, count, 0, true) : Result();
    }

    private static void Mark(JsonObject data, int step)
    {
        if (step == 0) return;
        data["compacted"] = true;
        data["compactStep"] = step;
    }

    /// <summary>Removes each declared detail path (dotted, from the root; a list on the way applies to each item). True when anything went.</summary>
    private static bool DropDetail(JsonObject data, IReadOnlyList<string> paths)
    {
        var removed = false;
        foreach (var path in paths)
        {
            var parts = path.Split('.');
            void Walk(JsonNode? node, int depth)
            {
                if (node is JsonArray array) { foreach (var item in array) Walk(item, depth); return; }
                if (node is not JsonObject obj) return;
                if (depth == parts.Length - 1) { removed |= obj.Remove(parts[depth]); return; }
                Walk(obj[parts[depth]], depth + 1);
            }
            Walk(data, 0);
        }
        return removed;
    }

    /// <summary>Shortens every text value longer than <see cref="RelayLimits.CompactTextChars"/> outside required keys to its first line.</summary>
    private static bool ShortenText(JsonNode node)
    {
        var changed = false;
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(property => property.Key).ToArray())
            {
                if (IsRequired(key)) continue;
                if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text) && FirstLine(text) is { } shorter) { obj[key] = shorter; changed = true; }
                else if (obj[key] is { } child) changed |= ShortenText(child);
            }
        }
        else if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                if (array[index] is JsonValue value && value.TryGetValue<string>(out var text) && FirstLine(text) is { } shorter) { array[index] = shorter; changed = true; }
                else if (array[index] is { } child) changed |= ShortenText(child);
            }
        }
        return changed;
    }

    /// <summary>The first line of a long text, at most <see cref="RelayLimits.CompactTextChars"/> characters, marked with an ellipsis; null for short text.</summary>
    private static string? FirstLine(string text)
    {
        if (text.Length <= RelayLimits.CompactTextChars) return null;
        var line = text.AsSpan();
        var end = line.IndexOfAny('\r', '\n');
        if (end >= 0) line = line[..end];
        if (line.Length > RelayLimits.CompactTextChars) line = line[..RelayLimits.CompactTextChars];
        return string.Concat(line, "…");
    }
}
