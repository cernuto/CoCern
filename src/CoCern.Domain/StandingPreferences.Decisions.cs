using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain.State;
using static CoCern.Contracts.RelayErrorCodes;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Domain;

internal static partial class StandingPreferences
{
    /// <summary>The caller's owned, open session is verified by CallGuard before this board mutation.</summary>
    public static RelayReply RecordDecision(AggregateContext context)
    {
        var args = context.Args;
        if (Text(args, "decision") == "approveGoal") return ApproveGoal(context);
        if (args.Any(field => field.Key is not ("repositoryId" or "boardId" or "sessionId" or "sessionToken" or "requestId" or "hubIncarnation" or "proposalId" or "decision" or "humanWords")))
            return Error(InvalidArguments);
        if (!Identifier(args["proposalId"], out var id)) return Invalid("proposalId");
        if (args["humanWords"] is not JsonValue quote || !quote.TryGetValue<string>(out var words)
            || string.IsNullOrWhiteSpace(words) || words.Length > RelayLimits.MaxDecisionHumanWordsLength) return Invalid("humanWords");
        var outcome = Text(args, "decision");
        if (outcome is not ("approve" or "dismiss")) return Invalid("decision");
        var request = Text(args, "requestId");
        if (request.Length == 0) return Error(RequestIdRequired);
        if (context.SessionId.Length == 0) return Error(SessionDenied);
        var board = new BoardState(context.State);
        var current = board.Standing;
        if (current?.Proposal(id) is not { } proposal) return Error(NotFound, new { proposalId = id });
        if (proposal.Json["goalTaskId"] is not null && !IsCurrentGoalOwner(context.State, proposal, context.ActingParticipant, context.SessionId)) return Error(SessionDenied);
        if (proposal.PresentedBy != context.SessionId || proposal.PresentedPrincipal != context.Caller.PrincipalId
            || proposal.PresentedEffect is null || proposal.PresentedRevision > current.Revision) return Error(SessionDenied);
        // Pending proposals do not advance the effective revision, so always compare the terms too. An error commits no stale marker.
        var effect = PresentationEffect(current, proposal, Text(args, "boardId", "default"));
        if (effect is null || !JsonNode.DeepEquals(proposal.PresentedEffect, effect))
            return Error(RelayErrorCodes.VersionConflict, new { reason = "The proposal changed; review it again" }, current.Revision);

        var auditRef = context.SessionId + ":" + request;
        var evidence = new JsonObject
        {
            [StandingDecision.AuditRefKey] = auditRef, [StandingDecision.PrincipalIdKey] = context.Caller.PrincipalId,
            [StandingDecision.SessionIdKey] = context.SessionId, [StandingDecision.AtKey] = context.Now.ToString("O"),
            [StandingDecision.HumanWordsKey] = words, [StandingDecision.OutcomeKey] = outcome,
            [StandingDecision.ProposalIdKey] = id, [StandingDecision.EffectKey] = effect.DeepClone()
        };
        var next = new StandingState(current.Json.DeepClone().AsObject());
        var changes = new List<string>();
        if (outcome == "approve")
        {
            if (ApproveProposal(next, next.Proposal(id)!, context, changes, evidence) is { } refusal) return refusal;
        }
        else
        {
            next.Proposals.Remove(id);
            changes.Add("-proposal:" + id);
        }
        var effectiveChange = changes.Any(change => change.StartsWith("+", StringComparison.Ordinal) || change.StartsWith("~", StringComparison.Ordinal));
        if (effectiveChange) next.Revision++;
        var entry = StandingChange.Create(next.Revision, context.Caller.PrincipalId, context.Now, changes);
        entry[StandingDecision.Key] = evidence.DeepClone();
        next.History.Add(entry);
        while (next.History.Count > RelayLimits.StandingHistoryEntries) next.History.RemoveAt(0);
        if (next.Lines.Count > RelayLimits.MaxStandingLines || next.Grants.Count > RelayLimits.MaxStandingGrants) return Error(CapacityExceeded);
        if (Oversized(next, context.Now) is { } tooLarge) return tooLarge;
        // The latest quotation must be readable whole, including for dismissal (which has no active record to carry it).
        var visible = JsonNode.Parse(ViewOf(next, context.Now).DataJson)!.AsObject();
        if (visible["history"] is not JsonArray history || !history.OfType<JsonObject>().Any(change => Text(change[StandingDecision.Key] as JsonObject ?? new(), StandingDecision.AuditRefKey) == auditRef))
            return Error(PayloadTooLarge);
        board.SetStanding(next);
        AttentionSources.OnProposalDecided(context, id);
        return Ok(new { proposalId = id, outcome, auditRef, revision = next.Revision }, next.Revision);
    }

    // Shared, validated per-ID apply path, below Put's unchanged administrator gate. Neither helper accepts arbitrary caller fields.
    private static RelayReply? ApproveProposal(StandingState next, StandingProposal proposal, AggregateContext context, List<string> changes, JsonObject? evidence = null)
    {
        if (proposal.Line is { } line && proposal.Grant is null)
        {
            if (ReadLine(line.Json, idRequired: false, out _, out var text) is { } invalid) return Invalid("line." + invalid);
            var id = line.Id(proposal.Id);
            if (next.HasGrant(id)) return Invalid("approve", id);
            SetApprovedLine(next, id, text, proposal.ProposedBy, context, changes, evidence);
        }
        else if (proposal.Grant is { } grant && proposal.Line is null)
        {
            var id = grant.Id(proposal.Id);
            if (next.HasLine(id)) return Invalid("approve", id);
            if (grant.ExpiresAt <= context.Now) return Error(GrantInvalid, new { grantId = id, reason = "expired" });
            if (ReadGrant(grant.Json, idRequired: false, context.Repository, context.Now, out var validated) is { } invalid) return Invalid("grant." + invalid);
            SetApprovedGrant(next, id, validated, proposal.ProposedBy, context, changes, evidence);
        }
        else return Invalid("proposal");
        next.Proposals.Remove(proposal.Id);
        changes.Add("approve:" + proposal.Id);
        return null;
    }

    private static void SetApprovedLine(StandingState next, string id, string text, string author, AggregateContext context, List<string> changes, JsonObject? evidence = null)
    {
        if (next.Line(id) is { } existing && existing.Text == text)
        {
            if (evidence is not null) existing.Json[StandingDecision.Key] = evidence.DeepClone();
            return;
        }
        changes.Add((next.HasLine(id) ? "~line:" : "+line:") + id);
        var record = StandingLine.Create(id, text, author, context.Caller.PrincipalId, context.Now).Json;
        if (evidence is not null) record[StandingDecision.Key] = evidence.DeepClone();
        next.Lines[id] = record;
    }

    private static void SetApprovedGrant(StandingState next, string id, StandingGrant grant, string? proposer, AggregateContext context, List<string> changes, JsonObject? evidence = null)
    {
        if (next.Grant(id) is { } existing && SameGrant(existing, grant))
        {
            if (evidence is not null) existing.Json[StandingDecision.Key] = evidence.DeepClone();
            return;
        }
        changes.Add((next.HasGrant(id) ? "~grant:" : "+grant:") + id);
        var record = StandingGrant.Create(id, grant, context.Caller.PrincipalId, context.Now, proposer).Json;
        if (evidence is not null) record[StandingDecision.Key] = evidence.DeepClone();
        next.Grants[id] = record;
    }
}
