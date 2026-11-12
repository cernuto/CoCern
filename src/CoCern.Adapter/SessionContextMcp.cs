using System.Text.Json;
using CoCern.Contracts;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace CoCern.Adapter;

/// <summary>One resource vocabulary for the HTTP host, stdio adapter and development proxy.</summary>
public static class SessionContextMcp
{
    public static ListResourceTemplatesResult Templates() => new()
    {
        ResourceTemplates =
        [
            new ResourceTemplate { UriTemplate = SessionContextResourceUri.ManifestTemplate, Name = "session-context-manifest", MimeType = "text/markdown", Description = "Compact retained repository-shared handoff. Discover context IDs with session_context_get." },
            new ResourceTemplate { UriTemplate = SessionContextResourceUri.PartTemplate, Name = "session-context-entry", MimeType = "text/markdown", Description = "Exact current retained entry version and bounded text part. Old versions are never substituted." }
        ]
    };

    public static ReadResourceResult ReadResult(SessionContextResourceReply reply)
    {
        if (!reply.Ok) throw new McpProtocolException("Retained context resource unavailable: " + reply.Code, McpErrorCode.InvalidParams);
        return new() { Contents = [new TextResourceContents { Uri = reply.Uri, MimeType = "text/markdown", Text = reply.Text }] };
    }

    internal static CallToolResult WithLink(string tool, bool ok, string? dataJson, CallToolResult result)
    {
        if (!ok || !tool.StartsWith("session_context_", StringComparison.Ordinal) || dataJson is null) return result;
        try
        {
            using var document = JsonDocument.Parse(dataJson);
            var data = document.RootElement;
            string? uri = data.TryGetProperty("uri", out var own) ? own.GetString() : null;
            if (uri is null)
                foreach (var name in new[] { "hits", "items" })
                    if (data.TryGetProperty(name, out var rows) && rows.ValueKind == JsonValueKind.Array && rows.GetArrayLength() > 0 && rows[0].TryGetProperty("uri", out var link)) { uri = link.GetString(); break; }
            if (uri is not null && uri.Length <= 256 && SessionContextResourceUri.TryParse(uri, out _))
                result.Content.Add(new ResourceLinkBlock { Uri = uri, Name = "retained-context", MimeType = "text/markdown", Description = "Read this retained context resource, or use session_context_get." });
        }
        catch (JsonException) { /* Keep the original result if an older server returns another shape. */ }
        return result;
    }
}
