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
    private static readonly string[] IdentifierFields = ["requestId", "repositoryId", "sessionId", "sessionToken", "actingSessionId", "actingSessionToken", "boardId", "machineId", "label", "taskId", "resourceId", "itemId",
        "actionId", "contextId", "orderId", "recipientSessionId", "messageId", "triggerId"];

    /// <summary>Whole-number fields. A non-integer would otherwise read as 0, so a garbled version could match a task that doesn't exist yet.</summary>
    private static readonly string[] IntegerFields = ["expectedVersion", "expectedRevision", "generation", "claimGeneration", "attempt", "leaseSeconds",
        "sinceRevision", "snapshotRevision", "partOffset", "freshnessSeconds", "revision"];

    /// <summary>Returns a rejection, or null with the parsed arguments.</summary>
    public static RelayReply? Validate(RelayCaller caller, RelayTool tool, JsonElement arguments, out JsonObject args, Func<string, string?>? canonicalRepository = null)
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
            // Refuse widened decision envelopes before stripping private or telemetry fields. The hub adds its incarnation later.
            // Adapters attach acting proof to every joined call; a goal approval also carries its task, version, participants and expiry,
            // which StandingPreferences.ApproveGoal fences again. Anything else still widens the envelope.
            if (tool.Name == RelayTools.RecordDecision)
            {
                var goal = args["decision"] is JsonValue decisionValue && decisionValue.TryGetValue<string>(out var decision) && decision == "approveGoal";
                if (args.Any(field => field.Key is not
                    ("repositoryId" or "boardId" or "sessionId" or "sessionToken" or "requestId" or "proposalId" or "decision" or "humanWords" or "callOrigin" or "backgroundCall"
                        or "actingSessionId" or "actingSessionToken") &&
                    !(goal && field.Key is "taskId" or "expectedVersion" or "participants" or "expiresAt")))
                    return Replies.Error(InvalidArguments);
            }
            // Untrusted telemetry only; never part of native envelopes, replay or aggregate state.
            args.Remove("callOrigin");
            if (args.ContainsKey(CheckedNativeResolution) || args.ContainsKey(ActingRepository)) return Replies.Error(InvalidArguments);
            // A preflight cannot be combined with any allocating or caller-controlled envelope.
            if (args.ContainsKey("nativePreflight"))
            {
                if (tool.Name != RelayTools.SessionOpen || args.Count != 1 || !NativeGitOrigin.ValidCheckoutEvidence(arguments.GetProperty("nativePreflight")))
                    return Replies.Error(InvalidArguments);
                return null;
            }
            if (arguments.TryGetProperty("nativeJoin", out var native) && native.ValueKind == JsonValueKind.Object &&
                native.TryGetProperty("resolution", out var evidence) &&
                (tool.Name != RelayTools.SessionOpen || args.Count != NativeParticipant.JoinFields(args) || !NativeGitOrigin.ValidCheckoutEvidence(evidence)))
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
        foreach (var flag in new[] { "includeNextWork", "attention", "attentionDetail", "seen", "headersOnly", "presentStanding", "includeStale", "includeDetails", "openOnly" })
            if (args.ContainsKey(flag) && (args[flag] is not JsonValue value || !value.TryGetValue<bool>(out _))) return Replies.Error(InvalidArguments, new { field = flag });
        if (tool.Name == RelayTools.TaskGet && (args.ContainsKey("taskId") || args.ContainsKey("section") || args.ContainsKey("sinceRevision")))
        {
            var listField = new[] { "openOnly", "includeDetails", "snapshotTag" }.FirstOrDefault(args.ContainsKey);
            if (listField is not null) return Replies.Error(InvalidArguments, new
            {
                field = listField,
                reason = "Board-list fields cannot accompany taskId, section or sinceRevision, even when false. Omit openOnly, includeDetails and snapshotTag for a task, section or delta read."
            });
        }
        if (args["snapshotTag"] is { } listTag && (listTag is not JsonValue tagValue || !tagValue.TryGetValue<string>(out var tagText)
            || tagText.Length > 64 || tagText.Any(char.IsControl))) return Replies.Error(InvalidArguments, new { field = "snapshotTag" });
        if (args.ContainsKey("statusTag") && (tool.Name != RelayTools.GetStatus || args["statusTag"] is not JsonValue statusValue
            || !statusValue.TryGetValue<string>(out var statusText) || statusText.Length > 64 || statusText.Any(char.IsControl)))
            return Replies.Error(InvalidArguments, new { field = "statusTag" });
        // Internal readers opt into a smaller read, never a different authority or a write riding on the probe.
        if (args.ContainsKey("statusProjection") && (tool.Name != RelayTools.GetStatus || Text(args, "sessionId").Length == 0
            || args["statusProjection"] is not JsonValue projectionValue || !projectionValue.TryGetValue<string>(out var projection)
            || projection is not ("probe" or "prompt") || args.Any(field => field.Key is not
                ("repositoryId" or "sessionId" or "sessionToken" or "actingSessionId" or "actingSessionToken" or "backgroundCall" or "statusProjection"))))
            return Replies.Error(InvalidArguments, new { field = "statusProjection" });
        // Machine revocation dispatches directly to discovery, outside the control aggregate's argument fence.
        if (tool.Name == RelayTools.ControlRequest && Text(args, "command") == "revoke_machine" && args.ContainsKey("allSessions"))
            return Replies.Error(InvalidArguments, new { field = "allSessions" });
        // A doc read may name another repository to read with repository: a native chat cannot change its repositoryId. That repository is
        // authorized below exactly like a repositoryId. Acting proof proves a session in its own repository, so it stays with that repository
        // (ActingRepository): the guard verifies it there and applies the session's tool profile (CallGuard.VerifyAsync), and the read itself
        // then speaks for the credential alone, as any client without a session does. Only these two reads ever leave the bound repository.
        if (tool.Name is RelayTools.DocSearch or RelayTools.DocSection && args["repository"] is { } named)
        {
            if (named is not JsonValue namedValue || !namedValue.TryGetValue<string>(out var other) || string.IsNullOrWhiteSpace(other))
                return Replies.Error(InvalidArguments, new { field = "repository" });
            if (!string.Equals(other, Text(args, "repositoryId"), StringComparison.OrdinalIgnoreCase))
            {
                if (Text(args, "actingSessionId").Length > 0 || Text(args, "actingSessionToken").Length > 0)
                {
                    if (Canonical(Text(args, "repositoryId")) is not { } own || !caller.MayAccess(own)) return Replies.Error(RepositoryDenied);
                    args[ActingRepository] = own;
                }
                args["repositoryId"] = other;
            }
            args.Remove("repository");
        }
        string? Canonical(string supplied) => canonicalRepository is null
            ? RepositoryIds.Match(supplied, caller.Repositories) ?? (caller.IsAdministrator ? supplied : null)
            : canonicalRepository(supplied);
        var repository = Canonical(Text(args, "repositoryId"));
        if (repository is null || !caller.MayAccess(repository)) return Replies.Error(RepositoryDenied);
        args["repositoryId"] = repository;
        if (tool.IsMutation && Text(args, "requestId").Length == 0) return Replies.Error(RequestIdRequired);
        if (!IdentifiersFit(args)) return Replies.Error(InvalidArguments);
        if (tool.Name == RelayTools.SessionLabelSet &&
            (args["label"] is not JsonValue labelValue || !labelValue.TryGetValue<string>(out var label) || string.IsNullOrWhiteSpace(label) ||
             args.Any(field => field.Key is not ("repositoryId" or "sessionId" or "sessionToken" or "requestId" or "label" or "actingSessionId" or "actingSessionToken" or "backgroundCall"))))
            return Replies.Error(InvalidArguments);
        // replyTo names a message: a bounded single-line identifier, never free text.
        if (args["replyTo"] is { } replyTo && (replyTo is not JsonValue replyValue || !replyValue.TryGetValue<string>(out var replyId) || replyId.Length > RelayLimits.MaxIdentifierLength || replyId.Any(char.IsControl)))
            return Replies.Error(InvalidArguments, new { field = "replyTo" });
        if (tool.Name == RelayTools.Search && SearchProblem(args) is { } field) return Replies.Error(InvalidArguments, new { field });
        if (tool.Name == RelayTools.AnalyticsQuery && AnalyticsProblem(args) is { } analyticsField) return Replies.Error(InvalidArguments, new { field = analyticsField });
        if (tool.Name is RelayTools.DocSearch or RelayTools.DocSection or RelayTools.DocSync && DocProblem(tool.Name, args) is { } docField) return Replies.Error(InvalidArguments, new { field = docField });
        if (tool.Name == RelayTools.Peers && PeerProblem(args) is { } peerField) return Replies.Error(InvalidArguments, new { field = peerField });
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
        args.Remove(NativeParticipant.Client);
        args.Remove(NativeParticipant.WorkingCopy);
        args.Remove(NativeParticipant.ProtectedRoot);
        args.Remove(NativeParticipant.Adoption);
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

    /// <summary>
    /// Hub-set only (a client copy is refused): on a doc read of another repository, the acting session's own repository, where its proof is verified
    /// and its tool profile applied. <see cref="CallControls.Take"/> takes it out with the proof.
    /// </summary>
    internal const string ActingRepository = "__actingRepositoryId";

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
    /// one present; doc_search without a query lists repositories), and an integer limit. A path never starts with <c>/</c> or holds <c>..</c>, and a branch is checked again before git sees it.
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
            RelayTools.DocSearch => Field("query", RelayLimits.MaxDocQueryLength, false) ?? Field("spec", RelayLimits.MaxDocSpecLength, false) ?? Field("path", RelayLimits.MaxDocPathLength, false),
            RelayTools.DocSection => Field("path", RelayLimits.MaxDocPathLength, true) ?? Field("anchor", RelayLimits.MaxDocAnchorLength, true),
            _ => Field("branch", RelayLimits.MaxDocBranchLength, false)
        };
        if (problem is not null) return problem;
        if (Text(args, "path") is { Length: > 0 } path && (path.StartsWith('/') || path.Contains('\\') || path.Split('/').Contains(".."))) return "path";
        if (args["limit"] is not null && (args["limit"] is not JsonValue limit || limit.GetValueKind() != JsonValueKind.Number || !long.TryParse(limit.ToJsonString(), out _))) return "limit";
        return null;
    }

    /// <summary>The first invalid <c>peers</c> argument, or null: at most one of a whole name and a ref, each a bounded single line. A null reads as absent.</summary>
    private static string? PeerProblem(JsonObject args)
    {
        foreach (var field in new[] { "name", "ref" })
            if (args[field] is { } given && (given is not JsonValue value || !value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text) || text.Length > RelayLimits.MaxIdentifierLength || text.Any(char.IsControl)))
                return field;
        return args["name"] is not null && args["ref"] is not null ? "ref" : null;
    }

    private static int Count(JsonObject args, string key) => args[key] is JsonArray array ? array.Count : 0;
}

/// <summary>
/// The adapter's call controls: fields that steer how a call is composed or recorded and never reach an aggregate. <see cref="Take"/>
/// reads and removes them (after <see cref="RelayRequestGuard.Validate"/> has checked their shapes).
/// </summary>
internal sealed record CallControls(bool OfferWanted, bool StatusDetail, bool PageSeen, string[] SeenIds, string? KnownEtag, JsonObject? Usage, bool ModelVisible, bool? BackgroundCall,
    bool HeadersOnly = false, string? KnownInboxEtag = null, bool PresentStanding = false, string? StatusTag = null, long? StatusHoldMinutes = null,
    string? ActingSessionId = null, string? ActingSessionToken = null, string? ActingRepositoryId = null, string? StatusProjection = null)
{
    private static readonly string[] Fields = ["usage", "attention", "attentionDetail", "attentionStatus", "maxHoldMinutes", "seen", "seenMessageIds", "knownEtag", "backgroundCall", "headersOnly", "knownInboxEtag", "presentStanding", "statusTag", "statusProjection", "actingSessionId", "actingSessionToken", RelayRequestGuard.ActingRepository];

    public static CallControls Take(JsonObject args)
    {
        var offerWanted = args["attention"] is not JsonValue optOut || optOut.GetValue<bool>();
        var controls = new CallControls(
            OfferWanted: offerWanted,
            StatusDetail: args.ContainsKey("statusProjection") || args["attentionDetail"] is JsonValue detail && detail.GetValue<bool>(),
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
            PresentStanding: args["presentStanding"] is JsonValue present && present.GetValue<bool>(),
            StatusTag: args["statusTag"] is JsonValue validator && validator.TryGetValue<string>(out var statusTag) ? statusTag : null,
            StatusHoldMinutes: args["maxHoldMinutes"] is not null ? Number(args, "maxHoldMinutes") : null,
            // A chat's proof that it is the one making an administrative call. Taken out here, so no aggregate, replay fingerprint or log ever holds it.
            ActingSessionId: JsonFields.Text(args, "actingSessionId") is { Length: > 0 } acting ? acting : null,
            ActingSessionToken: JsonFields.Text(args, "actingSessionToken") is { Length: > 0 } proof ? proof : null,
            // Set by RelayRequestGuard alone: a doc read of another repository, whose acting proof is verified in the session's own repository.
            ActingRepositoryId: JsonFields.Text(args, RelayRequestGuard.ActingRepository) is { Length: > 0 } own ? own : null,
            StatusProjection: args["statusProjection"] is JsonValue projection && projection.TryGetValue<string>(out var purpose) ? purpose : null);
        foreach (var field in Fields) args.Remove(field);
        return controls;
    }
}
