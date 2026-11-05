using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;

namespace CoCern.Adapter;

/// <summary>
/// The text a chat's context actually pays for: one tool reply as the model reads it. Runs last, on the way out of the MCP tool, so everything the adapter learns from a reply has already seen it whole.
/// It removes only what the chat already holds or what an absent field says just as well. A failed reply, and any reply of another shape, keeps every field.
/// </summary>
internal static class ModelReply
{
    // Read by a model, never rendered as HTML, so an apostrophe need not cost six characters.
    private static readonly JsonSerializerOptions Json = new(AdapterReply.JsonOptions) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static string Text(string tool, AdapterReply reply) => JsonSerializer.Serialize(Compact(tool, reply), Json);

    internal static AdapterReply Compact(string tool, AdapterReply reply)
    {
        if (!reply.Ok || reply.Data is not { ValueKind: JsonValueKind.Object } data || tool is not (RelayTools.CheckInbox or RelayTools.ContextPut)) return reply;
        var node = JsonNode.Parse(data.GetRawText())!.AsObject();
        if (tool == RelayTools.ContextPut) node.Remove("content");
        else CompactInbox(node);
        return reply with { Data = JsonSerializer.SerializeToElement(node) };
    }

    private static void CompactInbox(JsonObject inbox)
    {
        if (inbox["items"] is not JsonArray items) return;
        // Only the absent forms that mean the same: a page that is cut short still says so and still gives its cursor.
        if (inbox["nextCursor"] is null) inbox.Remove("nextCursor");
        if (inbox["truncated"] is JsonValue truncated && truncated.TryGetValue<bool>(out var cut) && !cut) inbox.Remove("truncated");
        foreach (var item in items.OfType<JsonObject>())
        {
            // The chat named the repository to read this inbox.
            item.Remove("repositoryId");
            if (item.ContainsKey("replyTo") && item["replyTo"] is null) item.Remove("replyTo");
            if (item["sentAt"] is JsonValue sent && sent.TryGetValue<string>(out var text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
                item["sentAt"] = at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }
    }
}
