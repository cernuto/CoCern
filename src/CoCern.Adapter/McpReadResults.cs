using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace CoCern.Adapter;

/// <summary>Structured stable reads without changing a reply whose delivery effects already committed.</summary>
public static class McpReadResults
{
    private static readonly JsonElement InboxSchema = Schema("""
        {
          "items": { "type": "array", "items": { "$ref": "#/$defs/inboxItem" } },
          "total": { "type": "integer", "minimum": 0 },
          "nextCursor": { "type": ["integer", "null"], "minimum": 0 },
          "truncated": { "type": "boolean" },
          "more": { "type": "integer", "minimum": 0 },
          "unchanged": { "type": "boolean" },
          "etag": { "type": "string" },
          "attention": { "type": ["object", "null"] }
        }
        """);
    private static readonly JsonElement StatusSchema = Schema("""
        {
          "paused": { "type": "boolean" },
          "cancellations": { "type": "integer", "minimum": 0 },
          "requests": { "type": "array", "items": { "type": "object" } },
          "requestsOmitted": { "type": "boolean" },
          "manager": { "type": ["object", "null"] },
          "control": { "type": ["object", "null"] },
          "attention": { "type": ["object", "null"], "properties": {
            "actionable": { "type": "integer", "minimum": 0 },
            "urgent": { "type": "integer", "minimum": 0 },
            "inbox": { "type": "integer", "minimum": 0 },
            "top": { "type": ["object", "null"] },
            "offer": { "type": ["object", "null"] },
            "pendingWork": { "type": ["object", "null"] },
            "preview": { "type": "boolean" },
            "wake": { "type": "string" }
          } },
          "unchanged": { "type": "boolean" },
          "etag": { "type": "string" }
        }
        """);

    public static JsonElement? OutputSchema(string tool) => tool switch
    {
        RelayTools.CheckInbox => InboxSchema,
        RelayTools.GetStatus => StatusSchema,
        _ => null
    };

    public static CallToolResult FromAdapter(string tool, AdapterReply reply)
    {
        var text = ModelReply.Text(tool, reply);
        if (OutputSchema(tool) is null) return SessionContextMcp.WithLink(tool, reply.Ok, reply.Data?.GetRawText(), Plain(reply.Ok, text));
        // This is the final model projection. Never rebuild structured data from an earlier native reply.
        var safe = JsonSerializer.Deserialize<AdapterReply>(text, AdapterReply.JsonOptions)!;
        return Present(safe, text, "data", "object");
    }

    public static CallToolResult FromHost(string tool, bool ok, string code, string dataJson, long version)
    {
        var text = JsonSerializer.Serialize(new { ok, code, dataJson, version }, AdapterReply.JsonOptions);
        if (OutputSchema(tool) is null) return SessionContextMcp.WithLink(tool, ok, dataJson, Plain(ok, text));
        try
        {
            using var document = JsonDocument.Parse(dataJson);
            return Present(new(ok, code, document.RootElement.Clone(), version), text, "dataJson", "json");
        }
        catch (JsonException)
        {
            // Preserve even an unexpected legacy envelope. Its caller can reconcile it without a transport-invented outcome.
            return Result(new(ok, code, null, version), text, "dataJson", "json");
        }
    }

    private static CallToolResult Present(AdapterReply reply, string text, string property, string encoding)
    {
        var result = Result(reply, text);
        return (reply.Data is null or { ValueKind: JsonValueKind.Object or JsonValueKind.Null }) &&
            JsonSerializer.SerializeToUtf8Bytes(result, McpJsonUtilities.DefaultOptions).Length <= RelayLimits.MaxPayloadBytes
            ? result : Result(reply, text, property, encoding);
    }

    private static CallToolResult Plain(bool ok, string text) => new() { IsError = !ok, Content = [new TextContentBlock { Text = text }] };
    private static CallToolResult Result(AdapterReply reply, string text, string? property = null, string? encoding = null)
    {
        var structured = new JsonObject
        {
            ["representation"] = property is null ? "structured" : "text", ["ok"] = reply.Ok, ["code"] = reply.Code,
            ["data"] = property is null && reply.Data is { } data ? JsonNode.Parse(data.GetRawText()) : null,
            ["version"] = reply.Version
        };
        if (reply.ConnectionTraceId is not null) structured["connectionTrace"] = reply.ConnectionTraceId;
        if (property is not null) structured["dataInText"] = new JsonObject { ["contentIndex"] = 0, ["property"] = property, ["encoding"] = encoding };
        return new()
        {
            IsError = !reply.Ok, Content = [new TextContentBlock { Text = text }],
            StructuredContent = JsonSerializer.SerializeToElement(structured)
        };
    }

    private static JsonElement Schema(string dataProperties)
    {
        var schema = JsonNode.Parse("""
            {
              "type": "object", "required": ["representation", "ok", "code", "data"], "additionalProperties": false,
              "properties": {
                "representation": { "enum": ["structured", "text"], "description": "structured carries parsed data here; text preserves a large committed reply in content[0] without duplicating it." },
                "ok": { "type": "boolean" }, "code": { "type": "string" },
                "data": { "type": ["object", "null"], "description": "Authorized read data, an unchanged/partial page, or error details. Optional fields depend on the read mode; check ok and code first." },
                "version": { "type": ["integer", "null"] }, "connectionTrace": { "type": ["string", "null"] },
                "dataInText": {
                  "type": "object", "required": ["contentIndex", "property", "encoding"], "additionalProperties": false,
                  "description": "Parse content[contentIndex].text as JSON, then read property. encoding=json means parse that string as JSON again; encoding=object means use that JSON value directly.",
                  "properties": { "contentIndex": { "const": 0 }, "property": { "enum": ["data", "dataJson"] }, "encoding": { "enum": ["object", "json"] } },
                  "oneOf": [
                    { "properties": { "property": { "const": "data" }, "encoding": { "const": "object" } } },
                    { "properties": { "property": { "const": "dataJson" }, "encoding": { "const": "json" } } }
                  ]
                }
              },
              "$defs": { "inboxItem": {
                "type": "object", "description": "A complete inbox item or canonical JSON piece. Join part values using partOffset/nextPartOffset before treating a piece as complete.",
                "properties": {
                  "messageId": { "type": "string" }, "repositoryId": { "type": "string" },
                  "senderSessionId": { "type": ["string", "null"] }, "recipientSessionId": { "type": ["string", "null"] },
                  "taskId": { "type": ["string", "null"] }, "boardId": { "type": ["string", "null"] },
                  "kind": { "type": ["string", "null"] }, "text": { "type": ["string", "null"] },
                  "sentAt": { "type": ["string", "null"] }, "replyTo": { "type": ["string", "null"] },
                  "truncated": { "type": "boolean" }, "part": { "type": "string" },
                  "partOffset": { "type": "integer", "minimum": 0 }, "nextPartOffset": { "type": ["integer", "null"], "minimum": 0 },
                  "bytes": { "type": "integer", "minimum": 0 }, "chars": { "type": "integer", "minimum": 0 }
                }
              } }
            }
            """)!.AsObject();
        // Refusal details are not a successful read payload and can legitimately use different fields.
        var properties = JsonNode.Parse(dataProperties)!.AsObject();
        schema["$defs"]!["readData"] = new JsonObject { ["type"] = new JsonArray("object", "null"), ["properties"] = properties };
        if (!properties.ContainsKey("items")) schema["$defs"]!.AsObject().Remove("inboxItem");
        schema["allOf"] = JsonNode.Parse("""
            [{ "if": { "properties": { "representation": { "const": "text" } } },
               "then": { "required": ["dataInText"], "properties": { "data": { "type": "null" } } },
               "else": { "not": { "required": ["dataInText"] } } },
             { "if": { "properties": { "ok": { "const": true }, "representation": { "const": "structured" } } },
               "then": { "properties": { "data": { "$ref": "#/$defs/readData" } } } }]
            """);
        return JsonSerializer.SerializeToElement(schema);
    }
}
