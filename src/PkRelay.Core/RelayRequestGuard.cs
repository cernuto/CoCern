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
    private static readonly string[] IdentifierFields = ["requestId", "repositoryId", "sessionId", "sessionToken", "boardId", "machineId", "label", "taskId", "resourceId", "itemId"];

    /// <summary>Returns a rejection, or null with the parsed arguments.</summary>
    public static RelayReply? Validate(RelayCaller caller, RelayTool tool, JsonElement arguments, out JsonObject args)
    {
        args = new();
        if (arguments.ValueKind != JsonValueKind.Object) return Replies.Error(InvalidArguments);
        var raw = arguments.GetRawText();
        if (Encoding.UTF8.GetByteCount(raw) > RelayLimits.MaxPayloadBytes) return Replies.Error(PayloadTooLarge);
        args = JsonNode.Parse(raw)!.AsObject();
        foreach (var flag in new[] { "includeNextWork", "attention", "attentionDetail", "seen" })
            if (args.ContainsKey(flag) && (args[flag] is not JsonValue value || !value.TryGetValue<bool>(out _))) return Replies.Error(InvalidArguments, new { field = flag });
        if (!caller.MayAccess(Text(args, "repositoryId"))) return Replies.Error(RepositoryDenied);
        if (tool.IsMutation && Text(args, "requestId").Length == 0) return Replies.Error(RequestIdRequired);
        if (!IdentifiersFit(args)) return Replies.Error(InvalidArguments);
        if (tool.Name == RelayTools.Search && SearchProblem(args) is { } field) return Replies.Error(InvalidArguments, new { field });
        if (args.ContainsKey("seenMessageIds") && (args["seenMessageIds"] is not JsonArray seen || seen.Count > RelayLimits.MaxAcknowledgedMessages
            || seen.Any(id => id is not JsonValue value || !value.TryGetValue<string>(out var text) || text.Length is 0 or > RelayLimits.MaxIdentifierLength))) return Replies.Error(InvalidArguments, new { field = "seenMessageIds" });
        if (Count(args, "resourceIds") > RelayLimits.MaxClaimResources || Count(args, "messageIds") > RelayLimits.MaxAcknowledgedMessages || Count(args, "ackMessageIds") > RelayLimits.MaxAcknowledgedMessages) return Replies.Error(PayloadTooLarge);
        return null;
    }

    private static bool IdentifiersFit(JsonObject args)
    {
        if (IdentifierFields.Any(field => Text(args, field).Length > RelayLimits.MaxIdentifierLength)) return false;
        try { return !Strings(args["resourceIds"]).Any(resource => resource.Length > RelayLimits.MaxIdentifierLength); }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>The first invalid search argument, or null: a nonempty query within its length, known kinds, a bounded status and an integer limit.</summary>
    private static string? SearchProblem(JsonObject args)
    {
        if (args["query"] is not JsonValue query || !query.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text) || text.Length > RelayLimits.MaxSearchQueryLength) return "query";
        if (args["kinds"] is not null && (args["kinds"] is not JsonArray kinds || kinds.Count > SearchKinds.All.Count
            || kinds.Any(kind => kind is not JsonValue value || !value.TryGetValue<string>(out var name) || !SearchKinds.All.Contains(name)))) return "kinds";
        if (args["status"] is not null && (args["status"] is not JsonValue status || !status.TryGetValue<string>(out var state) || state.Length > RelayLimits.MaxSearchStatusLength)) return "status";
        if (args["limit"] is not null && (args["limit"] is not JsonValue limit || limit.GetValueKind() != JsonValueKind.Number || !long.TryParse(limit.ToJsonString(), out _))) return "limit";
        return null;
    }

    private static int Count(JsonObject args, string key) => args[key] is JsonArray array ? array.Count : 0;
}
