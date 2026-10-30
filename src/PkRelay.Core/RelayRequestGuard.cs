using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PkRelay.Contracts;
using PkRelay.Domain;
using static PkRelay.Contracts.RelayErrorCodes;
using static PkRelay.Domain.JsonFields;

namespace PkRelay.Core;

/// <summary>Public-boundary validation: shape, size, repository scope and request identity, before any aggregate is touched.</summary>
internal static class RelayRequestGuard
{
    private static readonly string[] IdentifierFields = ["requestId", "repositoryId", "boardId", "machineId", "label", "taskId", "resourceId"];

    /// <summary>Returns a rejection, or null with the parsed arguments.</summary>
    public static RelayReply? Validate(RelayCaller caller, RelayTool tool, JsonElement arguments, out JsonObject args)
    {
        args = new();
        if (arguments.ValueKind != JsonValueKind.Object) return Replies.Error(InvalidArguments);
        var raw = arguments.GetRawText();
        if (Encoding.UTF8.GetByteCount(raw) > RelayLimits.MaxPayloadBytes) return Replies.Error(PayloadTooLarge);
        args = JsonNode.Parse(raw)!.AsObject();
        if (args.ContainsKey("includeNextWork") && (args["includeNextWork"] is not JsonValue include || !include.TryGetValue<bool>(out _))) return Replies.Error(InvalidArguments);
        if (!caller.MayAccess(Text(args, "repositoryId"))) return Replies.Error(RepositoryDenied);
        if (tool.IsMutation && Text(args, "requestId").Length == 0) return Replies.Error(RequestIdRequired);
        if (!IdentifiersFit(args)) return Replies.Error(InvalidArguments);
        if (Count(args, "resourceIds") > RelayLimits.MaxClaimResources || Count(args, "messageIds") > RelayLimits.MaxAcknowledgedMessages || Count(args, "ackMessageIds") > RelayLimits.MaxAcknowledgedMessages) return Replies.Error(PayloadTooLarge);
        return null;
    }

    private static bool IdentifiersFit(JsonObject args)
    {
        if (IdentifierFields.Any(field => Text(args, field).Length > RelayLimits.MaxIdentifierLength)) return false;
        try { return !Strings(args["resourceIds"]).Any(resource => resource.Length > RelayLimits.MaxIdentifierLength); }
        catch (InvalidOperationException) { return false; }
    }

    private static int Count(JsonObject args, string key) => args[key] is JsonArray array ? array.Count : 0;
}
