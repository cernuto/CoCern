using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain.Messages;
using CoCern.Domain.State;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Domain;

/// <summary>
/// The administrative action log: who did what to what, and how it ended, kept in the control document so it survives a restart and needs no running
/// chat to read. Only the hub's own composition writes it; the actor is always the authenticated caller, so no client argument can name someone else.
/// Selection decisions keep Paul's words in their own history, which an entry refers to.
/// <para>The control document is hub-wide, but this log reads one repository at a time. Numbers, cursors, the dropped count and eviction are all per
/// repository, so a page says nothing about any other, not even how busy it is. <see cref="RelayCaller.MayAccess"/> admits every administrator to
/// every repository, so it can't draw this line: <see cref="MayRead"/> honours the repositories an administrator's credential lists.</para>
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
        var repository = context.Repository;
        if (report.ActionRequestId is { Length: > 0 } request && Entries(log, repository).Any(entry => entry.RequestId == request && entry.ActorPrincipalId == actor && entry.Tool == report.Tool && entry.Code == report.Code))
            return Ok(new { recorded = false });
        var seqs = state.AdminActionSeqs;
        var seq = Number(seqs, repository) + 1;
        seqs[repository] = seq;
        log.Add(JsonSerializer.SerializeToNode(new AdminActionEntry(seq, context.Now, actor, report.ActingSessionId, report.ManagerGeneration, report.Tool, report.Command, report.Target,
            repository, report.ActionRequestId, report.Code, report.Succeeded, report.DecisionRef)));
        if (log.Count(entry => RepositoryOf(entry) == repository) > RelayLimits.AdminActionEntries) log.Remove(log.First(entry => RepositoryOf(entry) == repository));
        while (log.Count > RelayLimits.AdminActionTotalEntries) log.RemoveAt(0);
        if (seqs.Count > RelayLimits.AdminActionSequences)
        {
            var retained = log.Select(RepositoryOf).ToHashSet(StringComparer.Ordinal);
            foreach (var idle in seqs.Select(pair => pair.Key).Where(key => !retained.Contains(key)).ToArray()) seqs.Remove(idle);
        }
        return Ok(new { recorded = true });
    }

    /// <summary>
    /// A page of <paramref name="repository"/>'s entries older than <paramref name="before"/>, newest first, shortened until <paramref name="fits"/> accepts it,
    /// with the cursor following the cut. A caller with no claim on the repository gets the same page as a repository with no history.
    /// </summary>
    public static AdminActionPage Page(ControlState state, RelayCaller caller, string repository, long? before, Func<AdminActionPage, bool> fits)
    {
        if (!MayRead(caller, repository)) return new([], null, 0);
        var retained = Entries(state.AdminActions, repository).ToArray();
        var dropped = Math.Max(0, Number(state.AdminActionSeqs, repository) - retained.Length);
        var candidates = retained.Where(entry => before is null || entry.Seq < before).OrderByDescending(entry => entry.Seq).ToArray();
        var count = Math.Min(candidates.Length, RelayLimits.AdminActionPageEntries);
        while (true)
        {
            var items = candidates[..count];
            var page = new AdminActionPage(items, count < candidates.Length && count > 0 ? items[^1].Seq : null, dropped);
            if (count == 0 || fits(page)) return page;
            count--;
        }
    }

    /// <summary>A credential that lists repositories reads only those; one that lists none is the hub's own and reads whichever it names.</summary>
    private static bool MayRead(RelayCaller caller, string repository) =>
        caller.MayAccess(repository) && (caller.Repositories.Length == 0 || caller.Repositories.Contains(repository, StringComparer.Ordinal));

    private static IEnumerable<AdminActionEntry> Entries(JsonArray log, string repository) =>
        log.Where(entry => RepositoryOf(entry) == repository).Select(entry => entry!.Deserialize<AdminActionEntry>()!);

    private static string RepositoryOf(JsonNode? entry) => entry is JsonObject record ? Text(record, "repositoryId") : "";
}
