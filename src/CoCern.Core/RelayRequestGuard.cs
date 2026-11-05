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
    private static readonly string[] IdentifierFields = ["requestId", "repositoryId", "sessionId", "sessionToken", "boardId", "machineId", "label", "taskId", "resourceId", "itemId",
        "actionId", "contextId", "orderId", "recipientSessionId", "messageId", "triggerId"];

    /// <summary>Whole-number fields. A non-integer would otherwise read as 0, so a garbled version could match a task that doesn't exist yet.</summary>
    private static readonly string[] IntegerFields = ["expectedVersion", "expectedRevision", "generation", "claimGeneration", "attempt", "leaseSeconds",
        "sinceRevision", "snapshotRevision", "partOffset", "freshnessSeconds", "revision"];

    /// <summary>Returns a rejection, or null with the parsed arguments.</summary>
    public static RelayReply? Validate(RelayCaller caller, RelayTool tool, JsonElement arguments, out JsonObject args)
    {
        args = new();
        if (arguments.ValueKind != JsonValueKind.Object) return Replies.Error(InvalidArguments);
        try
        {
            var raw = arguments.GetRawText();
            if (Encoding.UTF8.GetByteCount(raw) > RelayLimits.MaxPayloadBytes) return Replies.Error(PayloadTooLarge);
            // A repeated key would let checks and the operation read different values, so it is refused (building the object throws on one).
            args = JsonNode.Parse(raw)!.AsObject();
            _ = args.Count;
            // Untrusted telemetry only; never part of native envelopes, replay or aggregate state.
            args.Remove("callOrigin");
            if (args.ContainsKey(CheckedNativeResolution)) return Replies.Error(InvalidArguments);
            // A preflight cannot be combined with any allocating or caller-controlled envelope.
            if (args.ContainsKey("nativePreflight"))
            {
                if (tool.Name != RelayTools.SessionOpen || args.Count != 1 || !NativeGitOrigin.ValidCheckoutEvidence(arguments.GetProperty("nativePreflight")))
                    return Replies.Error(InvalidArguments);
                return null;
            }
            if (arguments.TryGetProperty("nativeJoin", out var native) && native.ValueKind == JsonValueKind.Object &&
                native.TryGetProperty("resolution", out var evidence) &&
                (tool.Name != RelayTools.SessionOpen || args.Count != 4 || !NativeGitOrigin.ValidCheckoutEvidence(evidence)))
                return Replies.Error(InvalidArguments);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or JsonException)
        {
            args = new();
            return Replies.Error(InvalidArguments);
        }
        // Some clients send null for every optional parameter: a null number is absent, never 0.
        foreach (var numeric in IntegerFields) if (args.ContainsKey(numeric) && args[numeric] is null) args.Remove(numeric);
        if (NotWholeNumber(args) is { } numberField) return Replies.Error(InvalidArguments, new { field = numberField });
        if (tool.Name == RelayTools.ContextPut && args["content"] is null) return Replies.Error(InvalidArguments, new { field = "content" });
        foreach (var flag in new[] { "includeNextWork", "attention", "attentionDetail", "seen", "headersOnly", "presentStanding" })
            if (args.ContainsKey(flag) && (args[flag] is not JsonValue value || !value.TryGetValue<bool>(out _))) return Replies.Error(InvalidArguments, new { field = flag });
        // Machine revocation dispatches directly to discovery, outside the control aggregate's argument fence.
        if (tool.Name == RelayTools.ControlRequest && Text(args, "command") == "revoke_machine" && args.ContainsKey("allSessions"))
            return Replies.Error(InvalidArguments, new { field = "allSessions" });
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
        if (args["knownInboxEtag"] is { } inboxEtag && (inboxEtag is not JsonValue etagValue || !etagValue.TryGetValue<string>(out var etagText) || etagText.Length is 0 or > RelayLimits.MaxKnownEtagLength || etagText.Any(char.IsControl)))
            return Replies.Error(InvalidArguments, new { field = "knownInboxEtag" });
        if (Count(args, "resourceIds") > RelayLimits.MaxClaimResources || Count(args, "messageIds") > RelayLimits.MaxAcknowledgedMessages || Count(args, "ackMessageIds") > RelayLimits.MaxAcknowledgedMessages) return Replies.Error(PayloadTooLarge);
        if (args.ContainsKey("nativeJoin"))
        {
            if (tool.Name != RelayTools.SessionOpen) return Replies.Error(InvalidArguments);
            JsonNode? evidence = null;
            try { if (args["nativeJoin"] is JsonObject proof) proof.Remove("resolution", out evidence); }
            catch (ArgumentException) { return Replies.Error(InvalidArguments); }
            if (!NativeParticipant.Admit(caller, args)) return Replies.Error(InvalidArguments);
            if (evidence is not null) args[CheckedNativeResolution] = evidence;
            return null;
        }
        args.Remove(NativeParticipant.Id);
        args.Remove(NativeParticipant.Generation);
        args.Remove(NativeParticipant.Request);
        args.Remove(NativeParticipant.ConfirmedExpiry);
        return null;
    }

    private static string? NotWholeNumber(JsonObject args) => IntegerFields.FirstOrDefault(field => args.ContainsKey(field) && !IsWholeNumber(args[field]));

    /// <summary>A JSON integer within a long, or a string holding one (agents sometimes quote numbers).</summary>
    private static bool IsWholeNumber(JsonNode? node) => node is JsonValue value && (value.GetValueKind() switch
    {
        JsonValueKind.Number => value.TryGetValue<long>(out _),
        JsonValueKind.String => long.TryParse(value.GetValue<string>(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out _),
        _ => false
    });

    /// <summary>Within their length, and (when present) not blank and free of control characters, which would break logs and displays.</summary>
    internal const string CheckedNativeResolution = "__checkedNativeResolution";

    private static bool IdentifiersFit(JsonObject args)
    {
        if (IdentifierFields.Any(field => Text(args, field).Length > RelayLimits.MaxIdentifierLength)) return false;
        if (IdentifierFields.Any(field => args[field] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 && (string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl)))) return false;
        try { return !Strings(args["resourceIds"]).Any(resource => resource.Length > RelayLimits.MaxIdentifierLength); }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException) { return false; }
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
        if (args["name"] is not JsonValue name || !name.TryGetValue<string>(out var text)) return "name";
        if (text == "batch") return AnalyticsBatch.Problem(args);
        if (text != AnalyticsQueryNames.AdHocSql && !AnalyticsQueryNames.All.Contains(text)) return "name";
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

/// <summary>
/// The adapter's call controls: fields that steer how a call is composed or recorded and never reach an aggregate. <see cref="Take"/>
/// reads and removes them (after <see cref="RelayRequestGuard.Validate"/> has checked their shapes).
/// </summary>
internal sealed record CallControls(bool OfferWanted, bool StatusDetail, bool PageSeen, string[] SeenIds, string? KnownEtag, JsonObject? Usage, bool ModelVisible, bool? BackgroundCall,
    bool HeadersOnly = false, string? KnownInboxEtag = null, bool PresentStanding = false)
{
    private static readonly string[] Fields = ["usage", "attention", "attentionDetail", "attentionStatus", "maxHoldMinutes", "seen", "seenMessageIds", "knownEtag", "backgroundCall", "headersOnly", "knownInboxEtag", "presentStanding"];

    public static CallControls Take(JsonObject args)
    {
        var offerWanted = args["attention"] is not JsonValue optOut || optOut.GetValue<bool>();
        var controls = new CallControls(
            OfferWanted: offerWanted,
            StatusDetail: args["attentionDetail"] is JsonValue detail && detail.GetValue<bool>(),
            // A reader that showed the model a reply without an offer reports what it showed, so those info items close.
            PageSeen: args["seen"] is JsonValue seen && seen.GetValue<bool>(),
            SeenIds: args["seenMessageIds"] is JsonArray seenList ? JsonFields.Strings(seenList).ToArray() : [],
            // The agent's ifNoneMatch, forwarded only so the feedback rules can tell an unchanged read.
            KnownEtag: CallTelemetry.Bounded(JsonFields.Text(args, "knownEtag"), RelayLimits.MaxKnownEtagLength),
            // Reported usage is telemetry only.
            Usage: args["usage"] as JsonObject,
            ModelVisible: offerWanted && !args.ContainsKey("waitSeconds"),
            // This untrusted report affects telemetry only. Unmarked older adapters and malformed reports remain unknown.
            BackgroundCall: args["backgroundCall"] is JsonValue origin && origin.TryGetValue<bool>(out var background) ? background : null,
            // A background check_inbox reader that needs only which messages wait (the wake watcher) asks for their headers, and for an
            // unchanged answer when the inbox is the one it last read.
            HeadersOnly: args["headersOnly"] is JsonValue headers && headers.GetValue<bool>(),
            KnownInboxEtag: JsonFields.Text(args, "knownInboxEtag") is { Length: > 0 } inboxEtag ? inboxEtag : null,
            PresentStanding: args["presentStanding"] is JsonValue present && present.GetValue<bool>());
        foreach (var field in Fields) args.Remove(field);
        return controls;
    }
}
