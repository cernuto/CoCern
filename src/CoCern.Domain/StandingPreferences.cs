using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain.Messages;
using CoCern.Domain.State;
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
        var board = new BoardState(context.State);
        var current = board.Standing ?? new StandingState(new JsonObject());
        var revision = current.Revision;
        if (args["expectedRevision"] is not JsonValue expected || !long.TryParse(expected.ToJsonString(), out var expectedRevision) || expectedRevision != revision)
            return Error(RelayErrorCodes.VersionConflict, new { currentRevision = revision }, revision);
        if (!new[] { "lines", "grants", "remove", "approve", "settings" }.Any(args.ContainsKey)) return Error(InvalidArguments, new { required = "lines, grants, remove, approve or settings" });

        var next = new StandingState(current.Json.DeepClone().AsObject());
        var lines = next.Lines;
        var grants = next.Grants;
        var proposals = next.Proposals;
        var admin = context.Caller.PrincipalId;
        // "+" adds, "~" replaces (an edit), "-" removes; only line, grant and setting changes advance the revision.
        var changes = new List<string>();
        void SetLine(string id, string text, string author)
        {
            if (next.Line(id) is { } existing && existing.Text == text) return;
            changes.Add((lines.ContainsKey(id) ? "~line:" : "+line:") + id);
            lines[id] = StandingLine.Create(id, text, author, admin, context.Now).Json;
        }
        void SetGrant(string id, StandingGrant grant, string? proposer)
        {
            if (next.Grant(id) is { } existing && SameGrant(existing, grant)) return;
            changes.Add((grants.ContainsKey(id) ? "~grant:" : "+grant:") + id);
            grants[id] = StandingGrant.Create(id, grant, admin, context.Now, proposer).Json;
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
                if (next.Proposal(id) is not { } proposal) return Error(NotFound, new { id });
                proposals.Remove(id);
                changes.Add("approve:" + id);
                var proposer = proposal.ProposedBy;
                if (proposal.Line is { } line)
                {
                    var lineId = line.Id(id);
                    if (grants.ContainsKey(lineId)) return Invalid("approve", lineId);
                    SetLine(lineId, line.Text, proposer);
                }
                else if (proposal.Grant is { } grant)
                {
                    var grantId = grant.Id(id);
                    if (lines.ContainsKey(grantId)) return Invalid("approve", grantId);
                    if (grant.ExpiresAt <= context.Now) return Error(GrantInvalid, new { grantId, reason = "expired" });
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
                var id = grant.Id();
                if (lines.ContainsKey(id)) return Invalid("grants.id", id);
                SetGrant(id, grant, proposer: null);
            }
        }
        if (args.ContainsKey("settings"))
        {
            if (args["settings"] is not JsonObject settings || settings.Count == 0) return Invalid("settings");
            foreach (var setting in settings)
            {
                if (setting.Key != StandingSettings.RequireResourcesKey || setting.Value is not JsonValue value || !value.TryGetValue<bool>(out var enabled)) return Invalid("settings." + setting.Key);
                var stored = next.Settings;
                if (stored.RequireResources == enabled) continue;
                stored.RequireResources = enabled;
                changes.Add("settings:" + setting.Key + "=" + (enabled ? "true" : "false"));
            }
        }

        if (lines.Count > RelayLimits.MaxStandingLines || grants.Count > RelayLimits.MaxStandingGrants) return Error(CapacityExceeded);
        // A put that changes nothing (lines: [], an identical line) commits nothing; one that only approves or rejects proposals keeps the
        // revision, so chats are not sent standing that did not change.
        if (changes.Count == 0) return Ok(new { revision, changed = false, lines = lines.Count, grants = grants.Count, pending = proposals.Count }, revision);
        var standingChanged = changes.Any(change => !change.StartsWith("approve:", StringComparison.Ordinal) && !change.StartsWith("-proposal:", StringComparison.Ordinal));
        if (standingChanged) next.Revision = revision + 1;
        var updated = next.Revision;
        // Measure exactly what readers get from the new standing: the reply sessions' attachments come from, and the task_get view.
        if (Oversized(next, context.Now) is { } tooLarge) return tooLarge;
        var history = next.History;
        history.Add(StandingChange.Create(updated, admin, context.Now, changes));
        while (history.Count > RelayLimits.StandingHistoryEntries) history.RemoveAt(0);
        board.SetStanding(next);
        foreach (var decided in changes.Where(change => change.StartsWith("approve:", StringComparison.Ordinal) || change.StartsWith("-proposal:", StringComparison.Ordinal)))
            AttentionSources.OnProposalDecided(context, decided[(decided.IndexOf(':') + 1)..]);
        return Ok(new { revision = updated, changed = standingChanged, lines = lines.Count, grants = grants.Count, pending = proposals.Count }, updated);
    }

    private static bool SameGrant(StandingGrant existing, StandingGrant grant) =>
        existing.Scope == grant.Scope && existing.ExpiresAtText == grant.ExpiresAtText && existing.TargetSet.SetEquals(grant.TargetSet);

    public static RelayReply Propose(AggregateContext context)
    {
        var args = context.Args;
        if (args.ContainsKey("line") == args.ContainsKey("grant")) return Error(InvalidArguments, new { required = "line or grant" });
        var board = new BoardState(context.State);
        var standing = board.Standing ?? new StandingState(new JsonObject());
        var proposals = standing.Proposals;
        if (proposals.Count >= RelayLimits.MaxStandingProposals) return Error(CapacityExceeded);
        if (standing.ProposalRecords.Count(pending => pending.SessionId == context.SessionId) >= RelayLimits.MaxStandingProposalsPerSession)
            return Error(CapacityExceeded, new { maxPerSession = RelayLimits.MaxStandingProposalsPerSession });
        var id = "p-" + Guid.NewGuid().ToString("N")[..12];
        var proposal = StandingProposal.BySession(id, context.Caller.PrincipalId, context.SessionId, context.Now);
        if (args.ContainsKey("line"))
        {
            if (ReadLine(args["line"], idRequired: false, out var lineId, out var text) is { } field) return Invalid("line." + field);
            proposal.Line = StandingLine.Proposed(lineId, text);
        }
        else
        {
            if (ReadGrant(args["grant"], idRequired: false, context.Repository, context.Now, out var grant) is { } field) return Invalid("grant." + field);
            proposal.Grant = grant;
        }
        proposals[id] = proposal.Json;
        board.SetStanding(standing);
        AttentionSources.OnProposal(context, id, proposal.Line is { } proposed ? "line: " + proposed.Text : "grant: " + proposal.Grant!.Scope + " " + string.Join(",", Strings(proposal.Grant.Targets)));
        return Ok(new { proposalId = id, pending = true }, standing.Revision);
    }

    /// <summary>
    /// The hub proposes a feedback rule's standing line (<see cref="HubStandingProposal"/>). It reaches the board only through the internal
    /// operation, which the state machine refuses to any public caller, so no session can propose as the hub or skip its own cap. It has no
    /// session, so it never counts against a session's cap; the board's cap still applies. It proposes nothing while that line exists or is
    /// pending, or within <see cref="RelayLimits.TeamRuleDays"/> days of its last proposal of that line, so a rejected line is not proposed
    /// again at once. Like every proposal it changes nothing until an administrator approves it.
    /// </summary>
    public static RelayReply ProposeFromHub(AggregateContext context)
    {
        if (!HubStandingProposal.Decode(context.Args).TryGet(out var proposed, out var malformed)) return Malformed(malformed);
        if (LineProblem(proposed.LineId, idRequired: true, proposed.Text) is { } field) return Invalid("line." + field);
        var (lineId, text) = (proposed.LineId, proposed.Text);
        var board = new BoardState(context.State);
        var standing = board.Standing ?? new StandingState(new JsonObject());
        var revision = standing.Revision;
        if (standing.HasLine(lineId)) return Ok(new { pending = false, reason = "exists" }, revision);
        if (standing.ProposalRecords.Any(pending => pending.Line is { } line && line.Id() == lineId)) return Ok(new { pending = false, reason = "pending" }, revision);
        if (standing.HubProposedAt(lineId) > context.Now.AddDays(-RelayLimits.TeamRuleDays)) return Ok(new { pending = false, reason = "recent" }, revision);
        if (standing.ProposalCount >= RelayLimits.MaxStandingProposals) return Error(CapacityExceeded);
        var id = "p-" + Guid.NewGuid().ToString("N")[..12];
        standing.Proposals[id] = StandingProposal.ByHub(id, SessionRoles.HubProposer, context.Now, StandingLine.Proposed(lineId, text)).Json;
        standing.HubProposed(lineId, context.Now);
        board.SetStanding(standing);
        AttentionSources.OnProposal(context, id, "line: " + text);
        return Ok(new { proposalId = id, pending = true }, revision);
    }

    /// <summary>
    /// <c>PayloadTooLarge</c> (with <c>maxBytes</c> and <c>bytes</c>) when standing could not be read whole: its delivered reply, measured on the
    /// wire as the hub reads it, is over <see cref="RelayLimits.MaxStandingBytes"/>, or its <c>task_get</c> view would not fit even with
    /// history and proposals left out. Null when both fit.
    /// </summary>
    private static RelayReply? Oversized(StandingState standing, DateTimeOffset now)
    {
        var delivered = DeliveredStanding(standing, now);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Ok(delivered, Number(delivered, "revision"))).Length;
        if (bytes > RelayLimits.MaxStandingBytes) return Error(PayloadTooLarge, new { maxBytes = RelayLimits.MaxStandingBytes, bytes });
        var core = new StandingState(standing.Json.DeepClone().AsObject());
        core.DropHistoryAndProposals();
        var view = ViewOf(core, now);
        var viewBytes = JsonSerializer.SerializeToUtf8Bytes(view).Length;
        return viewBytes > RelayLimits.AggregateReplyBytes ? Error(PayloadTooLarge, new { maxBytes = RelayLimits.AggregateReplyBytes, bytes = viewBytes }) : null;
    }

    /// <summary>The standing a chat receives: lines and active grants without provenance, and settings when any are set.</summary>
    public static JsonObject Delivered(JsonObject board, DateTimeOffset now) => DeliveredStanding(new BoardState(board).Standing ?? new StandingState(new JsonObject()), now);

    private static JsonObject DeliveredStanding(StandingState standing, DateTimeOffset now)
    {
        var lines = standing.LineRecords.Select(line => (JsonNode)new JsonObject { ["id"] = line.Id(), ["text"] = line.Text });
        var grants = standing.GrantRecords.Where(grant => grant.ExpiresAt > now)
            .Select(grant => (JsonNode)new JsonObject { ["id"] = grant.Id(), ["scope"] = grant.Scope, ["targets"] = grant.Targets?.DeepClone(), ["expiresAt"] = grant.ExpiresAtText });
        var delivered = new JsonObject { ["revision"] = standing.Revision, ["lines"] = new JsonArray(lines.ToArray()), ["grants"] = new JsonArray(grants.ToArray()) };
        if (standing.SettingsNode is JsonObject { Count: > 0 } settings) delivered["settings"] = settings.DeepClone();
        return delivered;
    }

    /// <summary>The hub's internal read of <see cref="Delivered"/> (<see cref="StandingRead"/>); its version is the standing revision.</summary>
    public static RelayReply Read(AggregateContext context)
    {
        if (!StandingRead.Decode(context.Args).TryGet(out _, out var malformed)) return Malformed(malformed);
        var delivered = Delivered(context.State, context.Now);
        return Ok(delivered, Number(delivered, "revision"));
    }

    /// <summary>The full record for <c>task_get</c> with <c>section: "standing"</c>: provenance, expired grants, proposals and history, newest first.</summary>
    public static RelayReply View(JsonObject board, DateTimeOffset now) => ViewOf(new BoardState(board).Standing ?? new StandingState(new JsonObject()), now);

    private static RelayReply ViewOf(StandingState standing, DateTimeOffset now)
    {
        var grants = new JsonArray(standing.GrantRecords.Select(grant =>
        {
            var copy = grant.Json.DeepClone().AsObject();
            if (grant.ExpiresAt <= now) copy["expired"] = true;
            return (JsonNode)copy;
        }).ToArray());
        var view = new JsonObject
        {
            ["revision"] = standing.Revision,
            ["lines"] = new JsonArray(standing.LineNodes.Select(line => line?.DeepClone()).ToArray()),
            ["grants"] = grants,
            ["settings"] = standing.SettingsNode?.DeepClone() ?? new JsonObject(),
            ["proposals"] = new JsonArray(standing.ProposalRecords.Select(proposal => (JsonNode)Proposal(standing, proposal)).ToArray()),
            ["history"] = new JsonArray((standing.HistoryIfAny ?? new()).Reverse().Select(x => x?.DeepClone()).ToArray())
        };
        var revision = standing.Revision;
        // Lines and grants are bounded by MaxStandingBytes; the history and then proposals give way when the whole view would not fit.
        foreach (var section in new[] { "history", "proposals" })
        {
            var items = view[section]!.AsArray();
            while (items.Count > 0 && !FitsWire(Ok(view, revision), RelayLimits.AggregateReplyBytes)) { items.RemoveAt(items.Count - 1); view[section + "Truncated"] = true; }
        }
        return Ok(view, revision);
    }

    /// <summary>A proposal as the view shows it; <c>replaces</c> marks one whose ID would edit an existing line or grant when approved.</summary>
    private static JsonObject Proposal(StandingState standing, StandingProposal proposal)
    {
        var copy = proposal.Json.DeepClone().AsObject();
        // Approving writes the line or grant under the ID it names, or under the proposal's when it names none.
        var replaces = proposal.Line is { } line ? standing.HasLine(line.Id(proposal.Id)) : standing.HasGrant((proposal.Grant ?? new StandingGrant(new JsonObject())).Id(proposal.Id));
        if (replaces) copy["replaces"] = true;
        return copy;
    }

    /// <summary>
    /// Why a workflow's <c>authorityRef</c> cannot authorize it: <c>unknown</c>, <c>expired</c> or <c>scope</c>. Null when it names no
    /// grant, or names one that exists, has not expired, and covers the board's repository or every resource in <c>packet.resources</c>.
    /// An action-class grant has nothing on the task to match, so it never satisfies readiness.
    /// </summary>
    public static string? GrantProblem(BoardState board, WorkflowRecord workflow, DateTimeOffset now)
    {
        var reference = workflow.Packet?.AuthorityRef ?? "";
        if (!reference.StartsWith(GrantPrefix, StringComparison.Ordinal)) return null;
        var id = reference[GrantPrefix.Length..];
        if (board.Standing?.Grant(id) is not { } grant) return "unknown";
        if (grant.ExpiresAt <= now) return "expired";
        var targets = grant.TargetSet;
        var resources = workflow.Resources.ToArray();
        return grant.Scope switch
        {
            "repository" when targets.Contains(board.RepositoryId) => null,
            "resource" when resources.Length > 0 && resources.All(targets.Contains) => null,
            _ => "scope"
        };
    }

    /// <summary>Returns the invalid field, or null with the line's ID (empty when optional and absent) and text.</summary>
    private static string? ReadLine(JsonNode? node, bool idRequired, out string id, out string text)
    {
        id = ""; text = "";
        if (node is not JsonObject line) return "line";
        if ((idRequired || line[StandingLine.IdKey] is not null) && !Identifier(line[StandingLine.IdKey], out id)) return "id";
        if (line[StandingLine.TextKey] is not JsonValue value || !value.TryGetValue<string>(out var content) || LineProblem(id, idRequired: false, content) is not null) return "text";
        text = content;
        return null;
    }

    /// <summary>The invalid field of a line given as an ID and text (<c>id</c> or <c>text</c>), or null.</summary>
    private static string? LineProblem(string id, bool idRequired, string text)
    {
        if ((idRequired || id.Length > 0) && !Identifier(id, out _)) return "id";
        return string.IsNullOrWhiteSpace(text) || text.Length > RelayLimits.MaxStandingLineLength ? "text" : null;
    }

    /// <summary>Returns the invalid field, or null with the normalized grant: scope, distinct targets, and a future UTC expiry.</summary>
    private static string? ReadGrant(JsonNode? node, bool idRequired, string repository, DateTimeOffset now, out StandingGrant grant)
    {
        grant = new StandingGrant(new JsonObject());
        if (node is not JsonObject source) return "grant";
        var id = "";
        if ((idRequired || source[StandingGrant.IdKey] is not null) && !Identifier(source[StandingGrant.IdKey], out id)) return "id";
        var scope = Text(source, StandingGrant.ScopeKey);
        if (!Scopes.Contains(scope)) return "scope";
        string[] targets;
        if (source[StandingGrant.TargetsKey] is null && scope == "repository") targets = [repository];
        else if (source[StandingGrant.TargetsKey] is not JsonArray items || items.Count is 0 or > RelayLimits.MaxGrantTargets) return "targets";
        else
        {
            var values = items.Select(item => item is JsonValue value && value.TryGetValue<string>(out var target) && !string.IsNullOrWhiteSpace(target) && target.Length <= RelayLimits.MaxIdentifierLength ? target : null).ToArray();
            if (values.Any(value => value is null)) return "targets";
            targets = values.OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        }
        // A board's grant can only speak for the board's own repository.
        if (scope == "repository" && targets.Any(target => target != repository)) return "targets";
        if (!DateTimeOffset.TryParse(Text(source, StandingGrant.ExpiresAtKey), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expires) || expires <= now) return "expiresAt";
        grant = StandingGrant.Requested(id, scope, targets, expires);
        return null;
    }

    private static bool Identifier(JsonNode? node, out string id) => Identifier(node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "", out id);

    private static bool Identifier(string text, out string id)
    {
        id = text;
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
