using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain.Messages;
using CoCern.Domain.State;
using static CoCern.Domain.Replies;

namespace CoCern.Domain;

/// <summary>
/// The administrative action log: who did what to what, and how it ended, kept in the control document so it survives a restart and needs no running
/// chat to read. Bounded, with the number dropped reported beside it. Only the hub's own composition writes it; the actor is always the authenticated
/// caller, so no client argument can name someone else. Selection decisions keep Paul's words in their own history, which this entry refers to.
/// </summary>
internal static class AdminActionLog
{
    /// <summary><see cref="Operations.AdminAction"/>: one entry. A retried call reports the same request and outcome, and keeps one entry.</summary>
    public static RelayReply Record(AggregateContext context)
    {
        if (!AdminActionReport.Decode(context.Args).TryGet(out var report, out var malformed)) return Malformed(malformed);
        var state = new ControlState(context.State);
        var log = state.AdminActions;
        var actor = context.Caller.PrincipalId;
        if (report.ActionRequestId is { Length: > 0 } request && Entries(log).Any(entry => entry.RequestId == request && entry.ActorPrincipalId == actor && entry.Tool == report.Tool && entry.Code == report.Code))
            return Ok(new { recorded = false });
        var seq = state.AdminActionSeq + 1;
        state.AdminActionSeq = seq;
        log.Add(JsonSerializer.SerializeToNode(new AdminActionEntry(seq, context.Now, actor, report.ActingSessionId, report.ManagerGeneration, report.Tool, report.Command, report.Target,
            context.Repository, report.ActionRequestId, report.Code, report.Succeeded, report.DecisionRef)));
        while (log.Count > RelayLimits.AdminActionEntries) log.RemoveAt(0);
        return Ok(new { recorded = true });
    }

    /// <summary>
    /// A page older than <paramref name="before"/>, newest first, shortened until <paramref name="fits"/> accepts it, with the cursor following the cut.
    /// </summary>
    public static AdminActionPage Page(ControlState state, long? before, Func<AdminActionPage, bool> fits)
    {
        var log = state.AdminActions;
        var candidates = Entries(log).Where(entry => before is null || entry.Seq < before).OrderByDescending(entry => entry.Seq).ToArray();
        var dropped = Math.Max(0, state.AdminActionSeq - log.Count);
        var count = Math.Min(candidates.Length, RelayLimits.AdminActionPageEntries);
        while (true)
        {
            var items = candidates[..count];
            var page = new AdminActionPage(items, count < candidates.Length && count > 0 ? items[^1].Seq : null, dropped);
            if (count == 0 || fits(page)) return page;
            count--;
        }
    }

    private static IEnumerable<AdminActionEntry> Entries(JsonArray log) => log.Select(entry => entry!.Deserialize<AdminActionEntry>()!);
}
