using System.Text.Json;
using ModelContextProtocol.Protocol;
using PkRelay.Contracts;
using PkRelay.Core;
using PkRelay.Domain;

namespace PkRelay.Host;

/// <summary>Projects the shared catalog and domain replies onto the hub's HTTP/MCP wire shapes.</summary>
public static class ToolResults
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEnumerable<Tool> Tools(bool administrator) => RelayToolCatalog.ForRole(administrator).Select(tool => new Tool
    {
        Name = tool.Name,
        Description = tool.Description,
        InputSchema = RelayToolCatalog.InputSchema(tool)
    });

    public static RelayReply Failure(string code) => new(false, code, "{}", 0);

    public static int Status(RelayReply reply) => reply.Ok ? StatusCodes.Status200OK : RelayErrorCodes.Classify(reply.Code) switch
    {
        RelayErrorKind.Denied => StatusCodes.Status403Forbidden,
        RelayErrorKind.Missing => StatusCodes.Status404NotFound,
        RelayErrorKind.TooLarge => StatusCodes.Status413PayloadTooLarge,
        RelayErrorKind.Conflict => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status400BadRequest
    };

    /// <summary>Last-line guard on the serialized public envelope; the application already bounds its own replies.</summary>
    public static RelayReply Bound(RelayReply reply) =>
        JsonSerializer.SerializeToUtf8Bytes(reply, JsonOptions).Length <= RelayLimits.MaxPayloadBytes ? reply : new(false, RelayErrorCodes.PayloadTooLarge, "{}", reply.Version);
}

// OpenAPI describes the shared argument envelope; each tool catalog entry supplies its required fields.
public sealed class CoordinationRequest
{
    public required string RepositoryId { get; init; }
    public string? SessionId { get; init; }
    public string? SessionToken { get; init; }
    public string? RequestId { get; init; }
    public long? ExpectedVersion { get; init; }
    public string? BoardId { get; init; }
    public string? TaskId { get; init; }
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, JsonElement>? ToolArguments { get; init; }
}
