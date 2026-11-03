using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain.Messages;
using static CoCern.Domain.JsonFields;

namespace CoCern.Domain;

/// <summary>
/// Moves finished tasks off a board into the archive, so a board never fills up for good. A task is archivable when its workflow is
/// <c>completed</c> (the only finished state the domain has; a task without a workflow is never finished), nobody holds a live claim on
/// it, and it has no unresolved action or pending restoration. The board keeps a bounded memory of archived IDs in <c>archivedTasks</c>
/// (each ID with the board revision it left at and when): the last <see cref="RelayLimits.MaxArchivedIds"/> IDs or
/// <see cref="RelayLimits.ArchivedIdDays"/> days, whichever is fewer, plus every ID a live task's <c>dependsOn</c> still names.
/// <c>archiveFloor</c> is the newest revision it forgot, so a delta older than that restarts. Older IDs stay resolvable through the
/// archive table, which the hub checks.
/// </summary>
internal static class TaskArchiving
{
    public const string ArchivedKey = "archivedTasks";
    public const string FloorKey = "archiveFloor";
    private const int CloseBatch = 64;
    private const int MaxClosedSessions = 64;

    public static bool IsArchived(JsonObject board, string taskId) => board[ArchivedKey] is JsonObject archived && archived.ContainsKey(taskId);

    /// <summary>Whether a task ID on the board that owns <paramref name="tasks"/> is a remembered archived one.</summary>
    public static bool IsArchivedOnBoardOf(JsonObject tasks, string taskId) => tasks.Parent is JsonObject board && IsArchived(board, taskId);

    /// <summary>The board revision a remembered archived ID left at; 0 for one the hub resolved from the archive (never a delta removal).</summary>
    public static long Revision(JsonNode? stub) =>
        (stub is JsonArray { Count: > 0 } pair ? pair[0] : stub) is JsonValue value && value.TryGetValue<long>(out var revision) ? revision : 0;

    private static DateTimeOffset ArchivedAt(JsonNode? stub) =>
        stub is JsonArray { Count: > 1 } pair && pair[1] is JsonValue value && value.TryGetValue<long>(out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : DateTimeOffset.MinValue;

    private static JsonArray Stub(long revision, DateTimeOffset at) => new(revision, at.ToUnixTimeSeconds());

    /// <summary>When the task completed: its <c>completedAt</c>, else its last update (boards from before archiving), else the earliest time.</summary>
    public static DateTimeOffset CompletedAt(JsonObject task) =>
        Date(task, "completedAt") is var completed && completed > DateTimeOffset.MinValue ? completed : Date(task, "updatedAt");

    public static bool IsFinished(JsonObject task) => task["workflow"] is JsonObject workflow && Text(workflow, "state") == "completed";

    public static bool Archivable(JsonObject task, DateTimeOffset now) =>
        IsFinished(task) && !TaskActions.HasUnresolved(task) && (Text(task, "owner").Length == 0 || Date(task, "leaseUntil") <= now);

    /// <summary>
    /// On a board write with at least <see cref="RelayLimits.ArchiveHighWater"/> tasks: archives tasks finished longer than the setting,
    /// then the oldest finished ones (at least <see cref="RelayLimits.MinArchiveAgeHours"/> old) down to <see cref="RelayLimits.ArchiveLowWater"/>.
    /// With <paramref name="sweep"/> (the due sweep), it also archives the aged ones on a board below that mark and forgets old IDs.
    /// The task the operation names is never archived by it. Open attention items of each archived task are closed.
    /// </summary>
    public static void Run(AggregateContext context, bool sweep)
    {
        var state = context.State;
        var tasks = ObjectAt(state, "tasks");
        var full = tasks.Count >= RelayLimits.ArchiveHighWater;
        if (!sweep && !full) return;
        var except = Text(context.Args, "taskId");
        var candidates = tasks.Where(entry => entry.Key != except && entry.Value is JsonObject task && Archivable(task, context.Now))
            .Select(entry => (Id: entry.Key, Task: entry.Value!.AsObject(), Completed: CompletedAt(entry.Value!.AsObject())))
            .OrderBy(entry => entry.Completed).ThenBy(entry => entry.Id, StringComparer.Ordinal).ToList();
        var chosen = context.ArchiveAfterDays > 0 ? candidates.Where(entry => entry.Completed <= context.Now.AddDays(-context.ArchiveAfterDays)).ToList() : [];
        if (full)
        {
            var taken = chosen.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
            var excess = tasks.Count - chosen.Count - RelayLimits.ArchiveLowWater;
            var oldEnough = context.Now.AddHours(-RelayLimits.MinArchiveAgeHours);
            if (excess > 0) chosen.AddRange(candidates.Where(entry => !taken.Contains(entry.Id) && entry.Completed <= oldEnough).Take(excess));
        }
        if (chosen.Count > 0)
        {
            // One board revision for the whole batch: a delta reader past it sees each archived task as a removal.
            var revision = Number(state, "taskRevision") + 1;
            state["taskRevision"] = revision;
            var archived = ObjectAt(state, ArchivedKey);
            var repository = Text(state, "repositoryId");
            foreach (var (id, task, completed) in chosen)
            {
                tasks.Remove(id);
                task["archivedAt"] = context.Now.ToString("O");
                archived[id] = Stub(revision, context.Now);
                context.Archived.Add(new(repository, id, completed, context.Now, task.ToJsonString()));
            }
            CloseAttention(context, chosen.Select(entry => entry.Task).ToList(), revision);
        }
        if (sweep || chosen.Count > 0) Forget(state, context.Now);
    }

    /// <summary>
    /// Closes every open attention item of the archived tasks, with the archive revision, at the managers and at each task's owner and
    /// target session: review_ready, failed, next_work and the rest. Batched, so a large archive run adds only a few outbox effects.
    /// </summary>
    private static void CloseAttention(AggregateContext context, List<JsonObject> archived, long revision)
    {
        AttentionSelection Close(IEnumerable<string> ids) => new() { TaskIds = ids.ToArray(), Revision = revision };
        var ids = archived.Select(task => Text(task, "taskId")).ToList();
        foreach (var batch in ids.Chunk(CloseBatch)) AttentionSources.ToManagers(context, Close(batch));
        var bySession = archived
            .SelectMany(task => new[] { Text(task, "owner"), task["workflow"] is JsonObject workflow ? Text(workflow, "targetSessionId") : "" }.Where(session => session.Length > 0).Distinct(StringComparer.Ordinal).Select(session => (Session: session, Id: Text(task, "taskId"))))
            .GroupBy(entry => entry.Session, StringComparer.Ordinal).Take(MaxClosedSessions);
        foreach (var group in bySession)
            foreach (var batch in group.Select(entry => entry.Id).Chunk(CloseBatch)) AttentionSources.Emit(context, group.Key, Close(batch));
    }

    /// <summary>The IDs live tasks' <c>dependsOn</c> name: archived ones among them are always remembered.</summary>
    private static HashSet<string> Referenced(JsonObject state) =>
        (state["tasks"] as JsonObject ?? new JsonObject()).Select(entry => entry.Value).OfType<JsonObject>()
            .SelectMany(task => task["workflow"] is JsonObject workflow ? TaskWorkflow.Dependencies(workflow) : []).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Bounds what deltas can report: of the IDs with a removal revision, those older than <see cref="RelayLimits.ArchivedIdDays"/>, then
    /// the oldest past <see cref="RelayLimits.MaxArchivedIds"/>, leave delta memory, and <c>archiveFloor</c> rises to the newest revision
    /// among them. One a live task depends on stays remembered with revision 0 (counted as completed, never a removal); the rest are
    /// dropped, as are revision-0 IDs no live task depends on any more. So removals never exceed <see cref="RelayLimits.MaxArchivedIds"/>.
    /// </summary>
    private static void Forget(JsonObject state, DateTimeOffset now)
    {
        if (state[ArchivedKey] is not JsonObject archived || archived.Count == 0) return;
        var referenced = Referenced(state);
        var cutoff = now.AddDays(-RelayLimits.ArchivedIdDays);
        var entries = archived.Select(entry => (entry.Key, Revision: Revision(entry.Value), At: ArchivedAt(entry.Value))).ToList();
        var withRemoval = entries.Where(entry => entry.Revision > 0).ToList();
        var leave = withRemoval.Where(entry => entry.At <= cutoff).ToList();
        var kept = withRemoval.Count - leave.Count;
        if (kept > RelayLimits.MaxArchivedIds)
            leave.AddRange(withRemoval.Where(entry => entry.At > cutoff).OrderBy(entry => entry.Revision).ThenBy(entry => entry.Key, StringComparer.Ordinal).Take(kept - RelayLimits.MaxArchivedIds));
        var stale = entries.Where(entry => entry.Revision == 0 && !referenced.Contains(entry.Key)).ToList();
        if (leave.Count == 0 && stale.Count == 0) return;
        var leaving = leave.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        var dropping = stale.Select(entry => entry.Key).Concat(leave.Where(entry => !referenced.Contains(entry.Key)).Select(entry => entry.Key)).ToHashSet(StringComparer.Ordinal);
        var remaining = entries.Where(entry => !dropping.Contains(entry.Key))
            .Select(entry => (entry.Key, Value: (JsonNode)(leaving.Contains(entry.Key) ? Stub(0, entry.At) : archived[entry.Key]!.DeepClone()))).ToList();
        archived.Clear();
        foreach (var (key, value) in remaining) archived[key] = value;
        if (leave.Count > 0) state[FloorKey] = Math.Max(Number(state, FloorKey), leave.Max(entry => entry.Revision));
    }

    /// <summary>
    /// Remembers archived IDs the hub found in the archive table for a workflow's <c>dependsOn</c> (revision 0, so no delta lists them),
    /// so the dependency counts as completed here and stays remembered while a live task names it.
    /// </summary>
    public static void Remember(JsonObject state, IEnumerable<string> archivedIds, DateTimeOffset now)
    {
        var tasks = state["tasks"] as JsonObject;
        foreach (var id in archivedIds)
            if (tasks?[id] is not JsonObject && !IsArchived(state, id)) ObjectAt(state, ArchivedKey)[id] = Stub(0, now);
    }

    /// <summary>
    /// When the next sweep is due: the earliest time an idle finished task passes the setting (a claimed one waits for its lease too),
    /// or a remembered archived ID no live task depends on ages out. Tasks with an unresolved action have no time; the write that
    /// resolves it schedules them. Null with nothing to wait for.
    /// </summary>
    public static DateTimeOffset? NextSweep(JsonObject state, int archiveAfterDays)
    {
        DateTimeOffset? next = null;
        void Consider(DateTimeOffset due) { if (next is null || due < next) next = due; }
        if (archiveAfterDays > 0 && state["tasks"] is JsonObject tasks)
            foreach (var entry in tasks)
            {
                if (entry.Value is not JsonObject task || !IsFinished(task) || TaskActions.HasUnresolved(task)) continue;
                var due = CompletedAt(task).AddDays(archiveAfterDays);
                if (Text(task, "owner").Length > 0 && Date(task, "leaseUntil") > due) due = Date(task, "leaseUntil");
                Consider(due);
            }
        if (state[ArchivedKey] is JsonObject { Count: > 0 } archived)
        {
            // An ID with a removal revision leaves delta memory after ArchivedIdDays; a revision-0 one goes once nothing depends on it.
            var referenced = Referenced(state);
            foreach (var entry in archived)
                if (Revision(entry.Value) > 0) Consider(ArchivedAt(entry.Value).AddDays(RelayLimits.ArchivedIdDays));
                else if (!referenced.Contains(entry.Key)) Consider(ArchivedAt(entry.Value));
        }
        return next;
    }
}

/// <summary>Reads of archived tasks, which the hub loads from storage: the same views <c>task_get</c> gives, marked <c>archived: true</c>.</summary>
public static class ArchivedTasks
{
    public static RelayReply Read(JsonObject args, string json)
    {
        if (JsonNode.Parse(json) is not JsonObject task) return Replies.Error(RelayErrorCodes.NotFound);
        var reply = TaskBoardAggregate.ReadArchived(args, task);
        if (!reply.Ok || JsonNode.Parse(reply.DataJson) is not JsonObject data) return reply;
        data["archived"] = true;
        return Replies.BoundWire(reply with { DataJson = data.ToJsonString() }, RelayLimits.AggregateReplyBytes);
    }
}
