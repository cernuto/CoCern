using System.Text.Json;
using System.Text.Json.Nodes;
using PkRelay.Contracts;
using static PkRelay.Domain.JsonFields;

namespace PkRelay.Domain;

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

    /// <summary>One cursor page that fits the aggregate reply budget. An item too large for any page is replaced by a marker.</summary>
    public static RelayReply Page(IEnumerable<JsonNode> source, JsonObject args)
    {
        var offset = (int)Math.Clamp(Number(args, "cursor"), 0, RelayLimits.MaxRecords);
        var limit = (int)Math.Clamp(Number(args, "limit", RelayLimits.DefaultPageSize), 1, RelayLimits.MaxPageSize);
        var all = source.ToArray();
        var items = all.Skip(offset).Take(limit).Select(x => x.DeepClone()).ToList();
        while (items.Count > 1 && !FitsWire(Ok(new { items, nextCursor = offset + items.Count, truncated = true, total = all.Length }), RelayLimits.AggregateReplyBytes)) items.RemoveAt(items.Count - 1);
        if (items.Count == 0 && offset < all.Length) items.Add(new JsonObject { ["truncated"] = true, ["coverage"] = "Oversized item; retrieve its exact detail separately." });
        var more = offset + items.Count < all.Length;
        return Ok(new { items, nextCursor = more ? (int?)(offset + items.Count) : null, truncated = more, total = all.Length });
    }
}
