using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using static CoCern.Domain.JsonFields;

namespace CoCern.Domain;

/// <summary>Reply construction and wire bounding. Every reply must fit the public envelope; optional detail shrinks first.</summary>
public static class Replies
{
    public static RelayReply Ok(object value, long version = 0) => new(true, "Ok", JsonSerializer.Serialize(value), version);
    public static RelayReply Error(string code, object? data = null, long version = 0) => new(false, code, JsonSerializer.Serialize(data ?? new { }), version);
    public static RelayReply VersionConflict(long current) => Error(RelayErrorCodes.VersionConflict, new { currentVersion = current }, current);

    public static bool FitsWire(RelayReply reply, int maxBytes = RelayLimits.MaxPayloadBytes) => JsonSerializer.SerializeToUtf8Bytes(reply).Length <= maxBytes;

    public static RelayReply BoundWire(RelayReply reply, int maxBytes = RelayLimits.MaxPayloadBytes)
    {
        if (FitsWire(reply, maxBytes)) return reply;
        var data = JsonNode.Parse(reply.DataJson)!;
        // The optional offer is the first detail to shrink; mandatory grant/action fields win.
        if (data["nextWork"]?["offer"] is JsonObject offer && offer.Remove("workflow")) offer["detailFetchRequired"] = true;
        var optionalTrimmed = reply with { DataJson = data.ToJsonString() };
        if (FitsWire(optionalTrimmed, maxBytes)) return optionalTrimmed;
        // Details remain durable and separately retrievable. Authorization, grants, replay
        // decisions, outcomes, attempts, restoration state and resource blockers are retained.
        void Trim(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                if (obj["workflow"] is JsonObject workflow) { obj["workflow"] = TaskWorkflow.Summary(workflow); obj["workflowDetailFetchRequired"] = true; }
                foreach (var key in new[] { "facts", "messages", "reports", "attemptHistory", "binding", "report", "provenance", "content", "notes" })
                    if (obj.Remove(key)) { obj[key + "Truncated"] = true; obj["truncated"] = true; }
                foreach (var child in obj.Select(x => x.Value).OfType<JsonNode>().ToArray()) Trim(child);
            }
            else if (node is JsonArray array) foreach (var child in array.OfType<JsonNode>()) Trim(child);
        }
        Trim(data);
        if (data is JsonObject root) root["truncated"] = true;
        return reply with { DataJson = data.ToJsonString() };
    }

    /// <summary>
    /// One cursor page that fits the aggregate reply budget. An item too large for any page comes back alone as a piece of its JSON
    /// (<see cref="Piece"/>; <c>partOffset</c> asks for a later piece), and <c>nextCursor</c> still moves past it.
    /// </summary>
    public static RelayReply Page(IEnumerable<JsonNode> source, JsonObject args)
    {
        var offset = (int)Math.Clamp(Number(args, "cursor"), 0, RelayLimits.MaxRecords);
        var limit = (int)Math.Clamp(Number(args, "limit", RelayLimits.DefaultPageSize), 1, RelayLimits.MaxPageSize);
        var all = source.ToArray();
        var items = all.Skip(offset).Take(args.ContainsKey("partOffset") ? 1 : limit).Select(x => x.DeepClone()).ToList();
        RelayReply Reply(List<JsonNode> page)
        {
            var more = offset + page.Count < all.Length;
            return Ok(new { items = page, nextCursor = more ? (int?)(offset + page.Count) : null, truncated = more, total = all.Length });
        }
        while (items.Count > 1 && !FitsWire(Reply(items), RelayLimits.AggregateReplyBytes)) items.RemoveAt(items.Count - 1);
        if (items.Count == 1 && (args.ContainsKey("partOffset") || !FitsWire(Reply(items), RelayLimits.AggregateReplyBytes)))
            items[0] = Piece(items[0], Number(args, "partOffset"), piece => FitsWire(Reply([piece]), RelayLimits.AggregateReplyBytes));
        return Reply(items);
    }

    /// <summary>
    /// Whether <paramref name="item"/> alone fits a page as readers get it. A write measures what it stores with this, so nothing it
    /// accepts is unreadable once escaped for the wire.
    /// </summary>
    public static RelayReply? CheckReadable(JsonNode item)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Ok(new { items = new[] { item }, nextCursor = (int?)RelayLimits.MaxRecords, truncated = true, total = RelayLimits.MaxRecords })).Length;
        return bytes > RelayLimits.AggregateReplyBytes ? Error(RelayErrorCodes.PayloadTooLarge, new { maxBytes = RelayLimits.AggregateReplyBytes, bytes }) : null;
    }

    /// <summary>
    /// A piece of an item too large to return whole: <c>truncated</c>, the item's full JSON size in <c>bytes</c> and <c>chars</c>, its short
    /// top-level fields (such as its ID, so it can still be acknowledged), and <c>part</c>, its JSON text from <c>partOffset</c>.
    /// <c>nextPartOffset</c> is where the next piece starts, or null at the end; joining the parts gives the item's JSON.
    /// </summary>
    public static JsonObject Piece(JsonNode item, long partOffset, Func<JsonObject, bool> fits)
    {
        var json = item.ToJsonString();
        var start = (int)Math.Clamp(partOffset, 0, json.Length);
        var piece = new JsonObject { ["truncated"] = true, ["bytes"] = Encoding.UTF8.GetByteCount(json), ["chars"] = json.Length };
        if (item is JsonObject fields)
            foreach (var field in fields.Where(x => x.Value is JsonValue value && (value.GetValueKind() != JsonValueKind.String || value.GetValue<string>().Length <= RelayLimits.MaxIdentifierLength)).Take(16))
                piece.TryAdd(field.Key, field.Value!.DeepClone());
        JsonObject With(int length)
        {
            var end = start + length;
            // Never split a surrogate pair.
            if (end < json.Length && end > start && char.IsHighSurrogate(json[end - 1])) end--;
            var candidate = piece.DeepClone().AsObject();
            candidate["part"] = json[start..end];
            candidate["partOffset"] = start;
            candidate["nextPartOffset"] = end < json.Length ? end : null;
            return candidate;
        }
        // The longest part that fits: escaping makes the size depend on the text, so search for it.
        int low = 0, high = json.Length - start;
        while (low < high) { var middle = (low + high + 1) / 2; if (fits(With(middle))) low = middle; else high = middle - 1; }
        return With(low);
    }
}
