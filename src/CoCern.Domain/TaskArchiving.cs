using System.Text.Json.Nodes;
using CoCern.Contracts;
using static CoCern.Domain.JsonFields;

namespace CoCern.Domain;

/// <summary>
/// Moves finished tasks off a board into the archive, so a board never fills up for good. A task is archivable when its workflow is
/// <c>completed</c> (the only finished state the domain has; a task without a workflow is never finished), nobody holds a live claim on
/// it, and it has no unresolved action or pending restoration. The board keeps only <c>archivedTasks</c>: each archived ID with the
/// board revision it left at, so dependencies on it still count as completed, writes to it are refused and deltas report its removal.
/// </summary>
internal static class TaskArchiving
{
    public const string ArchivedKey = "archivedTasks";

    public static bool IsArchived(JsonObject board, string taskId) => board[ArchivedKey] is JsonObject archived && archived.ContainsKey(taskId);

    /// <summary>Whether a task ID on the board that owns <paramref name="tasks"/> was archived.</summary>
    public static bool IsArchivedOnBoardOf(JsonObject tasks, string taskId) => tasks.Parent is JsonObject board && IsArchived(board, taskId);

    /// <summary>When the task completed: its <c>completedAt</c>, else its last update (boards from before archiving), else the earliest time.</summary>
    public static DateTimeOffset CompletedAt(JsonObject task) =>
        Date(task, "completedAt") is var completed && completed > DateTimeOffset.MinValue ? completed : Date(task, "updatedAt");

    public static bool IsFinished(JsonObject task) => task["workflow"] is JsonObject workflow && Text(workflow, "state") == "completed";

    public static bool Archivable(JsonObject task, DateTimeOffset now) =>
        IsFinished(task) && !TaskActions.HasUnresolved(task) && (Text(task, "owner").Length == 0 || Date(task, "leaseUntil") <= now);

    /// <summary>
    /// On a board write with at least <see cref="RelayLimits.ArchiveHighWater"/> tasks: archives tasks finished longer than the setting,
    /// then the oldest finished ones down to <see cref="RelayLimits.ArchiveLowWater"/>. With <paramref name="sweep"/> (the due sweep), it
    /// also archives the aged ones on a board below that mark. The task the operation names is never archived by it.
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
            if (excess > 0) chosen.AddRange(candidates.Where(entry => !taken.Contains(entry.Id)).Take(excess));
        }
        if (chosen.Count == 0) return;
        // One board revision for the whole batch: a delta reader past it sees each archived task as a removal.
        var revision = Number(state, "taskRevision") + 1;
        state["taskRevision"] = revision;
        var archived = ObjectAt(state, ArchivedKey);
        var repository = Text(state, "repositoryId");
        foreach (var (id, task, completed) in chosen)
        {
            tasks.Remove(id);
            task["archivedAt"] = context.Now.ToString("O");
            archived[id] = revision;
            context.Archived.Add(new(repository, id, completed, context.Now, task.ToJsonString()));
        }
    }

    /// <summary>
    /// When the next sweep is due: the earliest time an idle finished task passes the setting (a claimed one waits for its lease too).
    /// Tasks with an unresolved action have no time; the write that resolves it schedules them. Null with nothing to wait for.
    /// </summary>
    public static DateTimeOffset? NextSweep(JsonObject state, int archiveAfterDays)
    {
        if (archiveAfterDays <= 0 || state["tasks"] is not JsonObject tasks) return null;
        DateTimeOffset? next = null;
        foreach (var entry in tasks)
        {
            if (entry.Value is not JsonObject task || !IsFinished(task) || TaskActions.HasUnresolved(task)) continue;
            var due = CompletedAt(task).AddDays(archiveAfterDays);
            if (Text(task, "owner").Length > 0 && Date(task, "leaseUntil") > due) due = Date(task, "leaseUntil");
            if (next is null || due < next) next = due;
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
