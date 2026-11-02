using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain;
using static CoCern.Contracts.RelayErrorCodes;
using static CoCern.Domain.JsonFields;

namespace CoCern.Core;

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
        // replyTo names a message: a bounded single-line identifier, never free text.
        if (args["replyTo"] is { } replyTo && (replyTo is not JsonValue replyValue || !replyValue.TryGetValue<string>(out var replyId) || replyId.Length > RelayLimits.MaxIdentifierLength || replyId.Any(char.IsControl)))
            return Replies.Error(InvalidArguments, new { field = "replyTo" });
        if (tool.Name == RelayTools.Search && SearchProblem(args) is { } field) return Replies.Error(InvalidArguments, new { field });
        if (tool.Name == RelayTools.AnalyticsQuery && AnalyticsProblem(args) is { } analyticsField) return Replies.Error(InvalidArguments, new { field = analyticsField });
        if (tool.Name is RelayTools.DocSearch or RelayTools.DocSection or RelayTools.DocSync && DocProblem(tool.Name, args) is { } docField) return Replies.Error(InvalidArguments, new { field = docField });
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

    /// <summary>
    /// The first invalid analytics argument, or null: a known name (or <c>sql</c> with SQL text within its length, and SQL only then) and an
    /// optional params object. The analytics service checks the parameter values.
    /// </summary>
    private static string? AnalyticsProblem(JsonObject args)
    {
        if (args["name"] is not JsonValue name || !name.TryGetValue<string>(out var text) || text != AnalyticsQueryNames.AdHocSql && !AnalyticsQueryNames.All.Contains(text)) return "name";
        var adHoc = text == AnalyticsQueryNames.AdHocSql;
        if (adHoc != args.ContainsKey("sql")) return "sql";
        if (adHoc && (args["sql"] is not JsonValue sql || !sql.TryGetValue<string>(out var statement) || string.IsNullOrWhiteSpace(statement) || statement.Length > RelayLimits.MaxAnalyticsSqlLength)) return "sql";
        if (args["params"] is not null && (adHoc || args["params"] is not JsonObject)) return "params";
        return null;
    }

    /// <summary>
    /// The first invalid doc cache argument, or null: a nonempty query, spec, path, anchor and branch within their lengths (each required
    /// one present), and an integer limit. A path never starts with <c>/</c> or holds <c>..</c>, and a branch is checked again before git sees it.
    /// </summary>
    private static string? DocProblem(string tool, JsonObject args)
    {
        string? Field(string field, int maximum, bool required)
        {
            if (args[field] is null) return required ? field : null;
            return args[field] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) && text.Length <= maximum ? null : field;
        }
        var problem = tool switch
        {
            RelayTools.DocSearch => Field("query", RelayLimits.MaxDocQueryLength, true) ?? Field("spec", RelayLimits.MaxDocSpecLength, false) ?? Field("path", RelayLimits.MaxDocPathLength, false),
            RelayTools.DocSection => Field("path", RelayLimits.MaxDocPathLength, true) ?? Field("anchor", RelayLimits.MaxDocAnchorLength, true),
            _ => Field("branch", RelayLimits.MaxDocBranchLength, false)
        };
        if (problem is not null) return problem;
        if (Text(args, "path") is { Length: > 0 } path && (path.StartsWith('/') || path.Contains('\\') || path.Split('/').Contains(".."))) return "path";
        if (args["limit"] is not null && (args["limit"] is not JsonValue limit || limit.GetValueKind() != JsonValueKind.Number || !long.TryParse(limit.ToJsonString(), out _))) return "limit";
        return null;
    }

    private static int Count(JsonObject args, string key) => args[key] is JsonArray array ? array.Count : 0;
}
