using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using static CoCern.Contracts.RelayErrorCodes;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Domain;

/// <summary>
/// A board's standing preferences (state key <c>standing</c>): short convention lines, typed authority grants and board settings
/// under one <c>revision</c>, plus pending proposals and a bounded change history. Only an administrator changes them. Any session
/// may propose a line or grant; a proposal changes nothing until an administrator approves it, so it does not advance the revision.
/// </summary>
internal static class StandingPreferences
{
    /// <summary>A packet's <c>authorityRef</c> names a grant as <c>grant:&lt;id&gt;</c>; any other text stays a prose reference.</summary>
    public const string GrantPrefix = "grant:";
    private static readonly string[] Scopes = ["repository", "resource", "action"];
    public static RelayReply Put(AggregateContext context)
    {
        if (!context.Caller.IsAdministrator) return Error(AdministratorRequired);
        var args = context.Args;
        var current = context.State[BoardReadModels.StandingKey] as JsonObject ?? new JsonObject();
        var revision = Number(current, "revision");
        if (args["expectedRevision"] is not JsonValue expected || !long.TryParse(expected.ToJsonString(), out var expectedRevision) || expectedRevision != revision)
            return Error(RelayErrorCodes.VersionConflict, new { currentRevision = revision }, revision);
        if (!new[] { "lines", "grants", "remove", "approve", "settings" }.Any(args.ContainsKey)) return Error(InvalidArguments, new { required = "lines, grants, remove, approve or settings" });

        var next = current.DeepClone().AsObject();
        var lines = ObjectAt(next, "lines");
        var grants = ObjectAt(next, "grants");
        var proposals = ObjectAt(next, "proposals");
        var admin = context.Caller.PrincipalId;
        var now = context.Now.ToString("O");
        // "+" adds, "~" replaces (an edit), "-" removes; only line, grant and setting changes advance the revision.
        var changes = new List<string>();
        void SetLine(string id, string text, string author)
        {
            if (lines[id] is JsonObject existing && Text(existing, "text") == text) return;
            changes.Add((lines.ContainsKey(id) ? "~line:" : "+line:") + id);
            lines[id] = Line(id, text, author, admin, now);
        }
        void SetGrant(string id, JsonObject grant, string? proposer)
        {
            if (grants[id] is JsonObject existing && SameGrant(existing, grant)) return;
            changes.Add((grants.ContainsKey(id) ? "~grant:" : "+grant:") + id);
            grants[id] = Grant(id, grant, admin, now, proposer);
        }

        if (args.ContainsKey("remove"))
        {
            if (Ids(args["remove"]) is not { } ids) return Invalid("remove");
            foreach (var id in ids)
            {
                if (lines.Remove(id)) changes.Add("-line:" + id);
                else if (grants.Remove(id)) changes.Add("-grant:" + id);
                else if (proposals.Remove(id)) changes.Add("-proposal:" + id);
                else return Error(NotFound, new { id });
            }
        }
        if (args.ContainsKey("approve"))
        {
            if (Ids(args["approve"]) is not { } ids) return Invalid("approve");
            foreach (var id in ids)
            {
                if (proposals[id] is not JsonObject proposal) return Error(NotFound, new { id });
                proposals.Remove(id);
                changes.Add("approve:" + id);
                var proposer = Text(proposal, "proposedBy");
                if (proposal["line"] is JsonObject line)
                {
                    var lineId = Text(line, "id", id);
                    if (grants.ContainsKey(lineId)) return Invalid("approve", lineId);
                    SetLine(lineId, Text(line, "text"), proposer);
                }
                else if (proposal["grant"] is JsonObject grant)
                {
                    var grantId = Text(grant, "id", id);
                    if (lines.ContainsKey(grantId)) return Invalid("approve", grantId);
                    if (Date(grant, "expiresAt") <= context.Now) return Error(GrantInvalid, new { grantId, reason = "expired" });
                    SetGrant(grantId, grant, proposer);
                }
            }
        }
        if (args.ContainsKey("lines"))
        {
            if (args["lines"] is not JsonArray items) return Invalid("lines");
            foreach (var item in items)
            {
                if (ReadLine(item, idRequired: true, out var id, out var text) is { } field) return Invalid("lines." + field);
                if (grants.ContainsKey(id)) return Invalid("lines.id", id);
                SetLine(id, text, admin);
            }
        }
        if (args.ContainsKey("grants"))
        {
            if (args["grants"] is not JsonArray items) return Invalid("grants");
            foreach (var item in items)
            {
                if (ReadGrant(item, idRequired: true, context.Repository, context.Now, out var grant) is { } field) return Invalid("grants." + field);
                var id = Text(grant, "id");
                if (lines.ContainsKey(id)) return Invalid("grants.id", id);
                SetGrant(id, grant, proposer: null);
            }
        }
        if (args.ContainsKey("settings"))
        {
            if (args["settings"] is not JsonObject settings || settings.Count == 0) return Invalid("settings");
            foreach (var setting in settings)
            {
                if (setting.Key != "requireResources" || setting.Value is not JsonValue value || !value.TryGetValue<bool>(out var enabled)) return Invalid("settings." + setting.Key);
                var stored = ObjectAt(next, "settings");
                if (stored[setting.Key] is JsonValue prior && prior.TryGetValue<bool>(out var was) && was == enabled) continue;
                stored[setting.Key] = enabled;
                changes.Add("settings:" + setting.Key + "=" + (enabled ? "true" : "false"));
            }
        }

        if (lines.Count > RelayLimits.MaxStandingLines || grants.Count > RelayLimits.MaxStandingGrants) return Error(CapacityExceeded);
        // A put that changes nothing (lines: [], an identical line) commits nothing; one that only approves or rejects proposals keeps the
        // revision, so chats are not sent standing that did not change.
        if (changes.Count == 0) return Ok(new { revision, changed = false, lines = lines.Count, grants = grants.Count, pending = proposals.Count }, revision);
        var standingChanged = changes.Any(change => !change.StartsWith("approve:", StringComparison.Ordinal) && !change.StartsWith("-proposal:", StringComparison.Ordinal));
        if (standingChanged) next["revision"] = revision + 1;
        var updated = Number(next, "revision");
        // Measure exactly what readers get from the new standing: the reply sessions' attachments come from, and the task_get view.
        if (Oversized(next, context.Now) is { } tooLarge) return tooLarge;
        var history = ArrayAt(next, "history");
        var entry = new JsonObject { ["revision"] = updated, ["by"] = admin, ["at"] = now, ["changes"] = new JsonArray(changes.Take(RelayLimits.StandingHistoryChanges).Select(change => (JsonNode)change).ToArray()) };
        if (changes.Count > RelayLimits.StandingHistoryChanges) entry["changesTotal"] = changes.Count;
        history.Add(entry);
        while (history.Count > RelayLimits.StandingHistoryEntries) history.RemoveAt(0);
        context.State[BoardReadModels.StandingKey] = next;
        foreach (var decided in changes.Where(change => change.StartsWith("approve:", StringComparison.Ordinal) || change.StartsWith("-proposal:", StringComparison.Ordinal)))
            AttentionSources.OnProposalDecided(context, decided[(decided.IndexOf(':') + 1)..]);
        return Ok(new { revision = updated, changed = standingChanged, lines = lines.Count, grants = grants.Count, pending = proposals.Count }, updated);
    }

    private static bool SameGrant(JsonObject existing, JsonObject grant) =>
        Text(existing, "scope") == Text(grant, "scope") && Text(existing, "expiresAt") == Text(grant, "expiresAt")
        && StringSet(existing["targets"]).SetEquals(StringSet(grant["targets"]));

    public static RelayReply Propose(AggregateContext context)
    {
        var args = context.Args;
        if (args.ContainsKey("line") == args.ContainsKey("grant")) return Error(InvalidArguments, new { required = "line or grant" });
        var standing = context.State[BoardReadModels.StandingKey] as JsonObject ?? new JsonObject();
        var proposals = ObjectAt(standing, "proposals");
        if (proposals.Count >= RelayLimits.MaxStandingProposals) return Error(CapacityExceeded);
        if (proposals.Count(x => x.Value is JsonObject pending && Text(pending, "sessionId") == context.SessionId) >= RelayLimits.MaxStandingProposalsPerSession)
            return Error(CapacityExceeded, new { maxPerSession = RelayLimits.MaxStandingProposalsPerSession });
        var id = "p-" + Guid.NewGuid().ToString("N")[..12];
        var proposal = new JsonObject { ["id"] = id, ["proposedBy"] = context.Caller.PrincipalId, ["sessionId"] = context.SessionId, ["proposedAt"] = context.Now.ToString("O") };
        if (args.ContainsKey("line"))
        {
            if (ReadLine(args["line"], idRequired: false, out var lineId, out var text) is { } field) return Invalid("line." + field);
            proposal["line"] = lineId.Length > 0 ? new JsonObject { ["id"] = lineId, ["text"] = text } : new JsonObject { ["text"] = text };
        }
        else
        {
            if (ReadGrant(args["grant"], idRequired: false, context.Repository, context.Now, out var grant) is { } field) return Invalid("grant." + field);
            proposal["grant"] = grant;
        }
        proposals[id] = proposal;
        context.State[BoardReadModels.StandingKey] = standing;
        AttentionSources.OnProposal(context, id, proposal["line"] is JsonObject proposed ? "line: " + Text(proposed, "text")
            : "grant: " + Text(proposal["grant"]!.AsObject(), "scope") + " " + string.Join(",", Strings(proposal["grant"]!["targets"])));
        return Ok(new { proposalId = id, pending = true }, Number(standing, "revision"));
    }

    /// <summary>
    /// The hub proposes a feedback rule's standing line. It reaches the board only through the internal operation, which the state machine
    /// refuses to any public caller, so no session can propose as the hub or skip its own cap. It has no session, so it never counts
    /// against a session's cap; the board's cap still applies. It proposes nothing while that line exists or is pending, or within
    /// <see cref="RelayLimits.TeamRuleDays"/> days of its last proposal of that line, so a rejected line is not proposed again at once.
    /// Like every proposal it changes nothing until an administrator approves it.
    /// </summary>
    public static RelayReply ProposeFromHub(AggregateContext context)
    {
        if (ReadLine(context.Args["line"], idRequired: true, out var lineId, out var text) is { } field) return Invalid("line." + field);
        var standing = context.State[BoardReadModels.StandingKey] as JsonObject ?? new JsonObject();
        var revision = Number(standing, "revision");
        var proposals = standing["proposals"] as JsonObject ?? new JsonObject();
        if ((standing["lines"] as JsonObject)?.ContainsKey(lineId) == true) return Ok(new { pending = false, reason = "exists" }, revision);
        if (proposals.Any(x => x.Value is JsonObject pending && pending["line"] is JsonObject line && Text(line, "id") == lineId)) return Ok(new { pending = false, reason = "pending" }, revision);
        var recent = standing["hubProposals"] as JsonObject ?? new JsonObject();
        if (Date(recent, lineId) > context.Now.AddDays(-RelayLimits.TeamRuleDays)) return Ok(new { pending = false, reason = "recent" }, revision);
        if (proposals.Count >= RelayLimits.MaxStandingProposals) return Error(CapacityExceeded);
        var id = "p-" + Guid.NewGuid().ToString("N")[..12];
        var proposal = new JsonObject
        {
            ["id"] = id, ["proposedBy"] = SessionRoles.HubProposer, ["hub"] = true, ["proposedAt"] = context.Now.ToString("O"),
            ["line"] = new JsonObject { ["id"] = lineId, ["text"] = text }
        };
        ObjectAt(standing, "proposals")[id] = proposal;
        ObjectAt(standing, "hubProposals")[lineId] = context.Now.ToString("O");
        context.State[BoardReadModels.StandingKey] = standing;
        AttentionSources.OnProposal(context, id, "line: " + text);
        return Ok(new { proposalId = id, pending = true }, revision);
    }

    /// <summary>
    /// <c>PayloadTooLarge</c> (with <c>maxBytes</c> and <c>bytes</c>) when standing could not be read whole: its delivered reply, measured on the
    /// wire as the hub reads it, is over <see cref="RelayLimits.MaxStandingBytes"/>, or its <c>task_get</c> view would not fit even with
    /// history and proposals left out. Null when both fit.
    /// </summary>
    private static RelayReply? Oversized(JsonObject standing, DateTimeOffset now)
    {
        var delivered = DeliveredStanding(standing, now);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Ok(delivered, Number(delivered, "revision"))).Length;
        if (bytes > RelayLimits.MaxStandingBytes) return Error(PayloadTooLarge, new { maxBytes = RelayLimits.MaxStandingBytes, bytes });
        var core = standing.DeepClone().AsObject();
        core.Remove("history"); core.Remove("proposals");
        var view = ViewOf(core, now);
        var viewBytes = JsonSerializer.SerializeToUtf8Bytes(view).Length;
        return viewBytes > RelayLimits.AggregateReplyBytes ? Error(PayloadTooLarge, new { maxBytes = RelayLimits.AggregateReplyBytes, bytes = viewBytes }) : null;
    }

    /// <summary>The standing a chat receives: lines and active grants without provenance, and settings when any are set.</summary>
    public static JsonObject Delivered(JsonObject board, DateTimeOffset now) => DeliveredStanding(board[BoardReadModels.StandingKey] as JsonObject ?? new JsonObject(), now);

    private static JsonObject DeliveredStanding(JsonObject standing, DateTimeOffset now)
    {
        var lines = (standing["lines"] as JsonObject ?? new()).Select(x => x.Value).OfType<JsonObject>()
            .Select(line => (JsonNode)new JsonObject { ["id"] = Text(line, "id"), ["text"] = Text(line, "text") });
        var grants = (standing["grants"] as JsonObject ?? new()).Select(x => x.Value).OfType<JsonObject>().Where(grant => Date(grant, "expiresAt") > now)
            .Select(grant => (JsonNode)new JsonObject { ["id"] = Text(grant, "id"), ["scope"] = Text(grant, "scope"), ["targets"] = grant["targets"]?.DeepClone(), ["expiresAt"] = Text(grant, "expiresAt") });
        var delivered = new JsonObject { ["revision"] = Number(standing, "revision"), ["lines"] = new JsonArray(lines.ToArray()), ["grants"] = new JsonArray(grants.ToArray()) };
        if (standing["settings"] is JsonObject { Count: > 0 } settings) delivered["settings"] = settings.DeepClone();
        return delivered;
    }

    /// <summary>The hub's internal read of <see cref="Delivered"/>; its version is the standing revision.</summary>
    public static RelayReply Read(JsonObject board, DateTimeOffset now)
    {
        var delivered = Delivered(board, now);
        return Ok(delivered, Number(delivered, "revision"));
    }

    /// <summary>The full record for <c>task_get</c> with <c>section: "standing"</c>: provenance, expired grants, proposals and history, newest first.</summary>
    public static RelayReply View(JsonObject board, DateTimeOffset now) => ViewOf(board[BoardReadModels.StandingKey] as JsonObject ?? new JsonObject(), now);

    private static RelayReply ViewOf(JsonObject standing, DateTimeOffset now)
    {
        var grants = new JsonArray((standing["grants"] as JsonObject ?? new()).Select(x => x.Value).OfType<JsonObject>().Select(grant =>
        {
            var copy = grant.DeepClone().AsObject();
            if (Date(grant, "expiresAt") <= now) copy["expired"] = true;
            return (JsonNode)copy;
        }).ToArray());
        var view = new JsonObject
        {
            ["revision"] = Number(standing, "revision"),
            ["lines"] = new JsonArray((standing["lines"] as JsonObject ?? new()).Select(x => x.Value?.DeepClone()).ToArray()),
            ["grants"] = grants,
            ["settings"] = standing["settings"]?.DeepClone() ?? new JsonObject(),
            ["proposals"] = new JsonArray((standing["proposals"] as JsonObject ?? new()).Select(x => x.Value).OfType<JsonObject>().Select(proposal => (JsonNode)Proposal(standing, proposal)).ToArray()),
            ["history"] = new JsonArray((standing["history"] as JsonArray ?? new()).Reverse().Select(x => x?.DeepClone()).ToArray())
        };
        var revision = Number(standing, "revision");
        // Lines and grants are bounded by MaxStandingBytes; the history and then proposals give way when the whole view would not fit.
        foreach (var section in new[] { "history", "proposals" })
        {
            var items = view[section]!.AsArray();
            while (items.Count > 0 && !FitsWire(Ok(view, revision), RelayLimits.AggregateReplyBytes)) { items.RemoveAt(items.Count - 1); view[section + "Truncated"] = true; }
        }
        return Ok(view, revision);
    }

    /// <summary>A proposal as the view shows it; <c>replaces</c> marks one whose ID would edit an existing line or grant when approved.</summary>
    private static JsonObject Proposal(JsonObject standing, JsonObject proposal)
    {
        var copy = proposal.DeepClone().AsObject();
        var (item, collection) = proposal["line"] is JsonObject line ? (line, "lines") : (proposal["grant"] as JsonObject ?? new JsonObject(), "grants");
        if ((standing[collection] as JsonObject)?.ContainsKey(Text(item, "id", Text(proposal, "id"))) == true) copy["replaces"] = true;
        return copy;
    }

    /// <summary>
    /// Why a workflow's <c>authorityRef</c> cannot authorize it: <c>unknown</c>, <c>expired</c> or <c>scope</c>. Null when it names no
    /// grant, or names one that exists, has not expired, and covers the board's repository or every resource in <c>packet.resources</c>.
    /// An action-class grant has nothing on the task to match, so it never satisfies readiness.
    /// </summary>
    public static string? GrantProblem(JsonObject board, JsonObject workflow, DateTimeOffset now)
    {
        var reference = workflow["packet"] is JsonObject packet ? Text(packet, "authorityRef") : "";
        if (!reference.StartsWith(GrantPrefix, StringComparison.Ordinal)) return null;
        var id = reference[GrantPrefix.Length..];
        if ((board[BoardReadModels.StandingKey] as JsonObject)?["grants"] is not JsonObject grants || grants[id] is not JsonObject grant) return "unknown";
        if (Date(grant, "expiresAt") <= now) return "expired";
        var targets = StringSet(grant["targets"]);
        var resources = TaskWorkflow.Resources(workflow).ToArray();
        return Text(grant, "scope") switch
        {
            "repository" when targets.Contains(Text(board, "repositoryId")) => null,
            "resource" when resources.Length > 0 && resources.All(targets.Contains) => null,
            _ => "scope"
        };
    }

    private static HashSet<string> StringSet(JsonNode? node) =>
        (node as JsonArray ?? new()).Select(item => item is JsonValue value && value.TryGetValue<string>(out var text) ? text : null).OfType<string>().ToHashSet(StringComparer.Ordinal);

    private static JsonObject Line(string id, string text, string author, string approvedBy, string approvedAt) =>
        new() { ["id"] = id, ["text"] = text, ["author"] = author, ["approvedBy"] = approvedBy, ["approvedAt"] = approvedAt };

    private static JsonObject Grant(string id, JsonObject grant, string grantor, string grantedAt, string? proposer)
    {
        var record = new JsonObject { ["id"] = id, ["scope"] = Text(grant, "scope"), ["targets"] = grant["targets"]?.DeepClone(), ["expiresAt"] = Text(grant, "expiresAt"), ["grantor"] = grantor, ["grantedAt"] = grantedAt };
        if (proposer is not null) record["proposedBy"] = proposer;
        return record;
    }

    /// <summary>Returns the invalid field, or null with the line's ID (empty when optional and absent) and text.</summary>
    private static string? ReadLine(JsonNode? node, bool idRequired, out string id, out string text)
    {
        id = ""; text = "";
        if (node is not JsonObject line) return "line";
        if ((idRequired || line["id"] is not null) && !Identifier(line["id"], out id)) return "id";
        if (line["text"] is not JsonValue value || !value.TryGetValue<string>(out var content) || string.IsNullOrWhiteSpace(content) || content.Length > RelayLimits.MaxStandingLineLength) return "text";
        text = content;
        return null;
    }

    /// <summary>Returns the invalid field, or null with the normalized grant: scope, distinct targets, and a future UTC expiry.</summary>
    private static string? ReadGrant(JsonNode? node, bool idRequired, string repository, DateTimeOffset now, out JsonObject grant)
    {
        grant = new JsonObject();
        if (node is not JsonObject source) return "grant";
        var id = "";
        if ((idRequired || source["id"] is not null) && !Identifier(source["id"], out id)) return "id";
        var scope = Text(source, "scope");
        if (!Scopes.Contains(scope)) return "scope";
        string[] targets;
        if (source["targets"] is null && scope == "repository") targets = [repository];
        else if (source["targets"] is not JsonArray items || items.Count is 0 or > RelayLimits.MaxGrantTargets) return "targets";
        else
        {
            var values = items.Select(item => item is JsonValue value && value.TryGetValue<string>(out var target) && !string.IsNullOrWhiteSpace(target) && target.Length <= RelayLimits.MaxIdentifierLength ? target : null).ToArray();
            if (values.Any(value => value is null)) return "targets";
            targets = values.OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        }
        // A board's grant can only speak for the board's own repository.
        if (scope == "repository" && targets.Any(target => target != repository)) return "targets";
        if (!DateTimeOffset.TryParse(Text(source, "expiresAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expires) || expires <= now) return "expiresAt";
        if (id.Length > 0) grant["id"] = id;
        grant["scope"] = scope;
        grant["targets"] = new JsonArray(targets.Select(target => (JsonNode)target).ToArray());
        grant["expiresAt"] = expires.ToUniversalTime().ToString("O");
        return null;
    }

    private static bool Identifier(JsonNode? node, out string id)
    {
        id = node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
        return !string.IsNullOrWhiteSpace(id) && id.Length <= RelayLimits.MaxStandingIdLength;
    }

    private static string[]? Ids(JsonNode? node)
    {
        if (node is not JsonArray items || items.Count > RelayLimits.MaxStandingLines + RelayLimits.MaxStandingGrants + RelayLimits.MaxStandingProposals) return null;
        var ids = new List<string>();
        foreach (var item in items)
        {
            if (!Identifier(item, out var id)) return null;
            ids.Add(id);
        }
        return ids.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static RelayReply Invalid(string field, string? id = null) => Error(InvalidArguments, id is null ? new { field } : (object)new { field, id });
}
