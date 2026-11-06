using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain;
using CoCern.Domain.Messages;
using static CoCern.Contracts.RelayErrorCodes;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Core;

/// <summary>Resolves public goal member selectors to private current participants before the atomic board approval.</summary>
internal sealed class GoalTools(Aggregates aggregates, PeerTools peers)
{
    internal async Task<RelayReply> ApproveAsync(RelayCall call)
    {
        if (call.NativeParticipant is not { } actor) return Error(SessionDenied);
        var caller = call.Caller with { ActingParticipant = actor, GoalParticipants = null };
        var replay = await aggregates.Board(caller, CoordinationStateMachine.GoalDecisionReplayProbe, call.Args);
        if (!replay.Ok) return replay;
        var saved = Aggregates.Parse(replay);
        if (IsTrue(saved, "found"))
            return new(true, Text(saved, "code"), saved["receipt"]?.ToJsonString() ?? "{}", Number(saved, "version"));

        if (call.Args["participants"] is JsonValue mode && mode.TryGetValue<string>(out var membership) && membership == "repository")
            return await aggregates.Board(caller, RelayTools.RecordDecision, call.Args);
        if (call.Args["participants"] is not JsonArray selectors || selectors.Count is 0 or > 32) return Error(InvalidArguments);
        var members = new List<VerifiedNativeParticipant>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var selector in selectors)
        {
            if (selector is not JsonObject selected || selected.Count != 1 || selected.Any(field => field.Key is not ("name" or "ref")))
                return Error(InvalidArguments);
            var name = Text(selected, "name");
            var reference = Text(selected, "ref");
            if ((name.Length == 0) == (reference.Length == 0) || name.Length > RelayLimits.MaxIdentifierLength || reference.Length > RelayLimits.MaxIdentifierLength)
                return Error(InvalidArguments);
            var (resolved, refusal) = await peers.ResolveAsync(caller, actor, name, reference);
            if (refusal is not null) return refusal;
            if (resolved is not { Outcome: PeerOutcome.Resolved, Truncated: false, Participant: { } member } || member.RepositoryId != actor.RepositoryId)
                return Error(InvalidArguments, new { reason = "Every goal member must resolve to one current verified chat in this repository." });
            if (!ids.Add(member.ParticipantId)) return Error(InvalidArguments, new { reason = "Goal participants must be distinct." });
            members.Add(member);
        }
        if (!ids.Contains(actor.ParticipantId)) return Error(InvalidArguments, new { reason = "Named participants must include the approving chat." });
        return await aggregates.Board(caller with { GoalParticipants = members.ToArray() }, RelayTools.RecordDecision, call.Args);
    }
}
