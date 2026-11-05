using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using static CoCern.Domain.JsonFields;

namespace CoCern.Domain.State;

/// <summary>
/// A board's stored document, read and written through named, typed members only: its tasks (<see cref="BoardTask"/>) and the board
/// revision every task change advances, its shared context notes (<see cref="ContextNote"/>), the bounded memory of the tasks it archived,
/// its standing preferences (<see cref="StandingState"/>) and its standing orders (<see cref="OrdersState"/>). Every key it stores is
/// declared in <see cref="Shape"/>. A client's own values (a task's facts, an action's binding and report, a note's content) are stored as
/// given, and so are the keys of a workflow beyond the ones the board reads: a workflow is validated, not rebuilt.
/// </summary>
/// <remarks>
/// Members that name a collection (<see cref="TaskMap"/>, <see cref="BoardTask.Actions"/>, <see cref="BoardTask.Messages"/>) create it
/// when it is absent, as a write needs; members that list or look up records never do, so a read leaves the document as it found it.
/// </remarks>
public sealed class BoardState(JsonObject json)
{
    public const string TasksKey = "tasks";
    /// <summary>The board revision: every committed task change advances it, so readers can fetch deltas.</summary>
    public const string TaskRevisionKey = "taskRevision";
    public const string ContextKey = "context";
    /// <summary>The archived task IDs the board still remembers, each with the board revision it left at and when it left.</summary>
    public const string ArchivedTasksKey = "archivedTasks";
    /// <summary>The newest board revision whose archived IDs the board forgot: a delta from before it starts over.</summary>
    public const string ArchiveFloorKey = "archiveFloor";
    public const string StandingKey = "standing";
    public const string OrdersKey = "orders";

    /// <summary>Every key a board stores, and the shape of each record under it.</summary>
    public static StateShape Shape { get; } = CommonState.Shape.With(
        (TasksKey, StateShape.Map(BoardTask.Shape)), (TaskRevisionKey, null), (ContextKey, StateShape.Map(ContextNote.Shape)), (ArchivedTasksKey, StateShape.Map(null)),
        (ArchiveFloorKey, null), (StandingKey, StandingState.Shape), (OrdersKey, OrdersState.Shape));

    public JsonObject Json => json;

    public string RepositoryId => Text(json, CommonState.RepositoryIdKey);

    /// <summary>The tasks by ID, created empty when the board has none.</summary>
    public JsonObject TaskMap => ObjectAt(json, TasksKey);

    /// <summary>The board's tasks; an entry that is not an object is skipped.</summary>
    public IEnumerable<BoardTask> Tasks => (json[TasksKey] as JsonObject ?? []).Select(entry => entry.Value).OfType<JsonObject>().Select(task => new BoardTask(task));

    public BoardTask? Task(string taskId) => (json[TasksKey] as JsonObject)?[taskId] is JsonObject task ? new(task) : null;

    /// <summary>A new task under <paramref name="taskId"/> (see <see cref="BoardTask.Create"/>).</summary>
    public BoardTask AddTask(string taskId)
    {
        var task = BoardTask.Create(taskId);
        TaskMap[taskId] = task.Json;
        return task;
    }

    public void RemoveTask(string taskId) => TaskMap.Remove(taskId);

    public long TaskRevision
    {
        get => Number(json, TaskRevisionKey);
        set => json[TaskRevisionKey] = value;
    }

    /// <summary>Advances the board revision and stamps <paramref name="task"/> with it, so delta readers and offers see the change.</summary>
    public void MarkChanged(BoardTask task)
    {
        TaskRevision += 1;
        task.ChangeRevision = TaskRevision;
    }

    /// <summary>The shared context notes by ID, created empty when the board has none.</summary>
    public JsonObject Notes => ObjectAt(json, ContextKey);

    /// <summary>Whether <paramref name="taskId"/> is an archived task the board still remembers.</summary>
    public bool IsArchived(string taskId) => json[ArchivedTasksKey] is JsonObject archived && archived.ContainsKey(taskId);

    /// <summary>The remembered archived IDs, each with its stub (see <c>TaskArchiving</c>), created empty when there are none.</summary>
    public JsonObject ArchivedTasks => ObjectAt(json, ArchivedTasksKey);

    /// <summary>The remembered archived IDs, or null when the board remembers none.</summary>
    public JsonObject? ArchivedTasksIfAny => json[ArchivedTasksKey] as JsonObject;

    public long ArchiveFloor
    {
        get => Number(json, ArchiveFloorKey);
        set => json[ArchiveFloorKey] = value;
    }

    /// <summary>The board's standing preferences, or null when it has none.</summary>
    public StandingState? Standing => json[StandingKey] is JsonObject standing ? new(standing) : null;

    /// <summary>Stores <paramref name="standing"/> as the board's standing preferences.</summary>
    public void SetStanding(StandingState standing) => json[StandingKey] = standing.Json;

    /// <summary>Whether the board refuses <c>ready</c> without <c>packet.resources</c> (<see cref="StandingSettings.RequireResources"/>); off unless set.</summary>
    public bool RequiresResources => Standing?.SettingsIfAny?.RequireResources == true;

    /// <summary>The board's standing orders, or null when it has none.</summary>
    public OrdersState? Orders => json[OrdersKey] is JsonObject orders ? new(orders) : null;

    /// <summary>Stores <paramref name="orders"/> as the board's standing orders.</summary>
    public void SetOrders(OrdersState orders) => json[OrdersKey] = orders.Json;
}

/// <summary>
/// One task: its version and change revision, its claim (owner, lease, generation, the request that made it, the resources it holds), its
/// optional workflow, its action records and the copies of superseded attempts, its reports and conversation, the client's facts, and when
/// it was completed, last updated and archived.
/// </summary>
public sealed class BoardTask(JsonObject json)
{
    public const string TaskIdKey = "taskId";
    /// <summary>The task's optimistic version: every write a client sees advances it.</summary>
    public const string VersionKey = "version";
    /// <summary>The board revision of the task's last change.</summary>
    public const string ChangeRevisionKey = "changeRevision";
    public const string OwnerKey = "owner";
    public const string LeaseUntilKey = "leaseUntil";
    public const string ClaimGenerationKey = "claimGeneration";
    public const string ClaimRequestIdKey = "claimRequestId";
    public const string ResourceIdsKey = "resourceIds";
    public const string WorkflowKey = "workflow";
    public const string ActionsKey = "actions";
    public const string AttemptHistoryKey = "attemptHistory";
    public const string ReportsKey = "reports";
    public const string MessagesKey = "messages";
    /// <summary>The client's facts, by its own keys (see <see cref="NotesFact"/> and <see cref="TitleFact"/>).</summary>
    public const string FactsKey = "facts";
    public const string CompletedAtKey = "completedAt";
    public const string UpdatedAtKey = "updatedAt";
    public const string UpdatedByKey = "updatedBy";
    /// <summary>When the hub saw this task take a <c>human_decision</c> blocker; <see cref="UpdatedAtKey"/> resets on any evidence update, so it cannot say.</summary>
    public const string HumanSinceKey = "humanSince";
    /// <summary>The same for a <c>coordinator_hold</c>, so a hold's age never shortens a later human blocker's time on the list.</summary>
    public const string HoldSinceKey = "holdSince";
    /// <summary>When the task left the board; only the archived copy holds it.</summary>
    public const string ArchivedAtKey = "archivedAt";

    /// <summary>The fact <c>task_update</c>'s <c>notes</c> are stored under.</summary>
    public const string NotesFact = "notes";

    /// <summary>The fact search takes a task's title from, when the client set one.</summary>
    public const string TitleFact = "title";

    public static StateShape Shape { get; } = StateShape.Object(
        (TaskIdKey, null), (VersionKey, null), (ChangeRevisionKey, null), (OwnerKey, null), (LeaseUntilKey, null), (ClaimGenerationKey, null), (ClaimRequestIdKey, null),
        (ResourceIdsKey, null), (WorkflowKey, WorkflowRecord.Shape), (ActionsKey, StateShape.Map(TaskAction.Shape)), (AttemptHistoryKey, TaskAction.Shape),
        (ReportsKey, TaskReport.Shape), (MessagesKey, TaskMessage.Shape), (FactsKey, StateShape.Any), (CompletedAtKey, null), (UpdatedAtKey, null), (UpdatedByKey, null),
        (HumanSinceKey, null), (HoldSinceKey, null), (ArchivedAtKey, null));

    /// <summary>A new task: version 0, with no actions, conversation, reports or facts yet.</summary>
    public static BoardTask Create(string taskId) => new(new JsonObject
    {
        [TaskIdKey] = taskId, [VersionKey] = 0, [ActionsKey] = new JsonObject(), [MessagesKey] = new JsonArray(), [ReportsKey] = new JsonArray(), [FactsKey] = new JsonObject()
    });

    public JsonObject Json => json;

    public string TaskId => Text(json, TaskIdKey);

    public long Version
    {
        get => Number(json, VersionKey);
        set => json[VersionKey] = value;
    }

    public long ChangeRevision
    {
        get => Number(json, ChangeRevisionKey);
        set => json[ChangeRevisionKey] = value;
    }

    /// <summary>Whether the task carries a change revision; one from before change markers does not.</summary>
    public bool HasChangeRevision => json.ContainsKey(ChangeRevisionKey);

    /// <summary>The session that holds or last held the claim, or empty when it was released. Whether it still holds it is <see cref="LeaseUntil"/>'s.</summary>
    public string Owner
    {
        get => Text(json, OwnerKey);
        set => json[OwnerKey] = value;
    }

    public DateTimeOffset LeaseUntil
    {
        get => Date(json, LeaseUntilKey);
        set => json[LeaseUntilKey] = value.ToString("O");
    }

    /// <summary>The lease's end as stored, for replies.</summary>
    public string LeaseUntilText => Text(json, LeaseUntilKey);

    /// <summary>Counts the claims taken; a renewal or release names the generation it is for.</summary>
    public long ClaimGeneration
    {
        get => Number(json, ClaimGenerationKey);
        set => json[ClaimGenerationKey] = value;
    }

    /// <summary>The <c>requestId</c> of the <c>task_claim</c> that took the claim, so only its own rollback can undo it.</summary>
    public string ClaimRequestId
    {
        get => Text(json, ClaimRequestIdKey);
        set => json[ClaimRequestIdKey] = value;
    }

    /// <summary>The canonical resource IDs the claim holds, as stored.</summary>
    public JsonNode? ResourceIds
    {
        get => json[ResourceIdsKey];
        set => json[ResourceIdsKey] = value;
    }

    /// <summary>The claim's resources; a stored value that is not a list of strings is invalid (<see cref="JsonFields.Strings"/>).</summary>
    public IEnumerable<string> ClaimedResources => Strings(json[ResourceIdsKey]);

    /// <summary>Ends the claim: the owner goes and the lease ends now. The caller bumps the version.</summary>
    public void ReleaseClaim(DateTimeOffset now)
    {
        json.Remove(OwnerKey);
        LeaseUntil = now;
    }

    public WorkflowRecord? Workflow => json[WorkflowKey] is JsonObject workflow ? new(workflow) : null;

    /// <summary>Stores a copy of <paramref name="workflow"/>, already validated, as the task's workflow.</summary>
    public void SetWorkflow(JsonObject workflow) => json[WorkflowKey] = workflow.DeepClone();

    /// <summary>The action records by action ID, created empty when the task has none.</summary>
    public JsonObject Actions => ObjectAt(json, ActionsKey);

    /// <summary>The record of action <paramref name="actionId"/>, or null.</summary>
    public TaskAction? Action(string actionId) => Actions[actionId] is JsonObject action ? new(action) : null;

    /// <summary>Every action record; an entry that is not an object is skipped.</summary>
    public IEnumerable<TaskAction> ActionRecords => (json[ActionsKey] as JsonObject ?? []).Select(entry => entry.Value).OfType<JsonObject>().Select(action => new TaskAction(action));

    /// <summary>Copies of the action records of superseded attempts, oldest first; created empty when the task has none.</summary>
    public JsonArray AttemptHistory => ArrayAt(json, AttemptHistoryKey);

    public JsonArray? AttemptHistoryIfAny => json[AttemptHistoryKey] as JsonArray;

    /// <summary>The action reports (<see cref="TaskReport"/>), oldest first; created empty when the task has none.</summary>
    public JsonArray Reports => ArrayAt(json, ReportsKey);

    public JsonArray? ReportsIfAny => json[ReportsKey] as JsonArray;

    /// <summary>The task's conversation (<see cref="TaskMessage"/>), oldest first; created empty when the task has none.</summary>
    public JsonArray Messages => ArrayAt(json, MessagesKey);

    public JsonArray? MessagesIfAny => json[MessagesKey] as JsonArray;

    /// <summary>
    /// One of the paged list sections <c>task_get</c> reads by name (<see cref="MessagesKey"/>, <see cref="ReportsKey"/>,
    /// <see cref="AttemptHistoryKey"/>), created empty when absent.
    /// </summary>
    public JsonArray Section(string key) => key switch
    {
        MessagesKey => Messages,
        ReportsKey => Reports,
        AttemptHistoryKey => AttemptHistory,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Not a task list section.")
    };

    /// <summary>The client's facts, created empty when the task has none.</summary>
    public JsonObject Facts => ObjectAt(json, FactsKey);

    public JsonObject? FactsIfAny => json[FactsKey] as JsonObject;

    /// <summary>When the workflow was last completed; archiving counts from it, and reopening clears it.</summary>
    public DateTimeOffset CompletedAt
    {
        get => Date(json, CompletedAtKey);
        set => json[CompletedAtKey] = value.ToString("O");
    }

    public void ClearCompletedAt() => json.Remove(CompletedAtKey);

    public DateTimeOffset UpdatedAt
    {
        get => Date(json, UpdatedAtKey);
        set => json[UpdatedAtKey] = value.ToString("O");
    }

    /// <summary>The last update's time as stored, for replies.</summary>
    public string UpdatedAtText => Text(json, UpdatedAtKey);

    /// <summary>The principal of the last update.</summary>
    public string UpdatedBy
    {
        get => Text(json, UpdatedByKey);
        set => json[UpdatedByKey] = value;
    }

    public DateTimeOffset ArchivedAt
    {
        get => Date(json, ArchivedAtKey);
        set => json[ArchivedAtKey] = value.ToString("O");
    }

    /// <summary>
    /// Stamps when the task took a blocker that waits on a person, from <paramref name="workflow"/>, its new workflow: the time stays while
    /// any blocker of that kind stays, and goes with the last one. The hub's alone: a client's <c>patch</c> writes facts, never these.
    /// </summary>
    public void StampWaiting(WorkflowRecord workflow, DateTimeOffset now)
    {
        void Stamp(string key, string kind)
        {
            if (workflow.State != WorkflowStates.Blocked || !workflow.Blockers.Any(blocker => blocker.Kind == kind)) json.Remove(key);
            else if (json[key] is null) json[key] = now.ToString("O");
        }
        Stamp(HumanSinceKey, BlockerKinds.HumanDecision);
        Stamp(HoldSinceKey, BlockerKinds.CoordinatorHold);
    }
}

/// <summary>
/// One action attempt's record: the action, its input fingerprint and trigger, the attempt, its status and restoration, who wrote it
/// (session, principal, and the binding the session had), when it started and when the board last told its session it answers for it,
/// the resources it blocks, and the report that settled it. A recovery (<see cref="ActionRecovery"/>), its writer's override of one
/// (<see cref="ActionOverride"/>) and its session's close (<see cref="ActionOwnerClosed"/>) are recorded on it.
/// </summary>
public sealed class TaskAction(JsonObject json)
{
    public const string ActionIdKey = "actionId";
    public const string InputFingerprintKey = "inputFingerprint";
    public const string TriggerIdKey = "triggerId";
    public const string AttemptKey = "attempt";
    public const string StatusKey = "status";
    public const string SessionIdKey = "sessionId";
    public const string PrincipalIdKey = "principalId";
    public const string BindingVersionKey = "bindingVersion";
    /// <summary>The writer session's client binding when the action began, as the client gave it.</summary>
    public const string BindingKey = "binding";
    public const string StartedAtKey = "startedAt";
    public const string RestorationKey = "restoration";
    public const string ResourceIdsKey = "resourceIds";
    public const string RequiresRestorationKey = "requiresRestoration";
    /// <summary>When the board last told the session that answers for the action that it does.</summary>
    public const string TrackedAtKey = "trackedAt";
    public const string OwnerClosedKey = "ownerClosed";
    /// <summary>The settling report's body, as the client gave it.</summary>
    public const string ReportKey = "report";
    public const string ReportedAtKey = "reportedAt";
    public const string RecoveryKey = "recovery";
    public const string OverriddenKey = "overridden";

    public static StateShape Shape { get; } = StateShape.Object(
        (ActionIdKey, null), (InputFingerprintKey, null), (TriggerIdKey, null), (AttemptKey, null), (StatusKey, null), (SessionIdKey, null), (PrincipalIdKey, null),
        (BindingVersionKey, null), (BindingKey, StateShape.Any), (StartedAtKey, null), (RestorationKey, null), (ResourceIdsKey, null), (RequiresRestorationKey, null),
        (TrackedAtKey, null), (OwnerClosedKey, ActionOwnerClosed.Shape), (ReportKey, StateShape.Any), (ReportedAtKey, null), (RecoveryKey, ActionRecovery.Shape),
        (OverriddenKey, ActionOverride.Shape));

    /// <summary>A new attempt's record, tracked from <paramref name="startedAt"/>.</summary>
    public static TaskAction Begun(string actionId, string inputFingerprint, string triggerId, long attempt, string status, string sessionId, string principalId, long bindingVersion,
        JsonNode? binding, DateTimeOffset startedAt, string restoration, JsonNode? resourceIds, bool requiresRestoration)
    {
        var action = new JsonObject
        {
            [ActionIdKey] = actionId, [InputFingerprintKey] = inputFingerprint, [TriggerIdKey] = triggerId, [AttemptKey] = attempt, [StatusKey] = status, [SessionIdKey] = sessionId,
            [PrincipalIdKey] = principalId, [BindingVersionKey] = bindingVersion, [BindingKey] = binding, [StartedAtKey] = startedAt.ToString("O"), [RestorationKey] = restoration,
            [ResourceIdsKey] = resourceIds
        };
        action[RequiresRestorationKey] = requiresRestoration;
        action[TrackedAtKey] = startedAt.ToString("O");
        return new(action);
    }

    public JsonObject Json => json;

    public string ActionId => Text(json, ActionIdKey);
    public string InputFingerprint => Text(json, InputFingerprintKey);
    public string TriggerId => Text(json, TriggerIdKey);
    public long Attempt => Number(json, AttemptKey);

    public string Status
    {
        get => Text(json, StatusKey);
        set => json[StatusKey] = value;
    }

    /// <summary>The session that wrote the action.</summary>
    public string SessionId => Text(json, SessionIdKey);

    public string PrincipalId => Text(json, PrincipalIdKey);

    public string Restoration
    {
        get => Text(json, RestorationKey);
        set => json[RestorationKey] = value;
    }

    /// <summary>Whether the action declared that it must be restored; a stored value that is not a boolean is invalid (<see cref="JsonFields.Flag"/>).</summary>
    public bool RequiresRestoration => Flag(json, RequiresRestorationKey);

    /// <summary>The resources the action blocks; a stored value that is not a list of strings is invalid.</summary>
    public IEnumerable<string> Resources => Strings(json[ResourceIdsKey]);

    public DateTimeOffset StartedAt => Date(json, StartedAtKey);

    public DateTimeOffset TrackedAt
    {
        get => Date(json, TrackedAtKey);
        set => json[TrackedAtKey] = value.ToString("O");
    }

    /// <summary>Whether the session that answered for the action was seen to close while it was unresolved.</summary>
    public bool OwnerClosed => json[OwnerClosedKey] is not null;

    /// <summary>The session seen to close while it answered for the action; none unless <see cref="OwnerClosed"/>.</summary>
    public string? OwnerClosedSessionId => json[OwnerClosedKey] is JsonObject closed ? Text(closed, ActionOwnerClosed.SessionIdKey) : null;

    public void MarkOwnerClosed(string sessionId, DateTimeOffset at) =>
        json[OwnerClosedKey] = new JsonObject { [ActionOwnerClosed.SessionIdKey] = sessionId, [ActionOwnerClosed.AtKey] = at.ToString("O") };

    /// <summary>Records the report that settled or updated the action: its body and when.</summary>
    public void Reported(JsonNode? report, DateTimeOffset at)
    {
        json[ReportKey] = report;
        json[ReportedAtKey] = at.ToString("O");
    }

    /// <summary>The recovery the action is under, or null; setting null removes it.</summary>
    public ActionRecovery? Recovery
    {
        get => json[RecoveryKey] is JsonObject recovery ? new(recovery) : null;
        set
        {
            if (value is null) json.Remove(RecoveryKey);
            else json[RecoveryKey] = value.Json;
        }
    }

    /// <summary>A copy of the stored recovery, whatever it holds, for the record of an override.</summary>
    public JsonNode? RecoveryCopy => json[RecoveryKey]?.DeepClone();

    /// <summary>The abandonment its writer's real outcome replaced, or null.</summary>
    public ActionOverride? Overridden
    {
        get => json[OverriddenKey] is JsonObject overridden ? new(overridden) : null;
        set => json[OverriddenKey] = value?.Json;
    }
}

/// <summary>Who took an action over from its gone writer, when, and the status it had; <see cref="OverrideKey"/> marks one taken back from another session.</summary>
public sealed class ActionRecovery(JsonObject json)
{
    public const string PrincipalIdKey = "principalId";
    public const string SessionIdKey = "sessionId";
    public const string AdministratorKey = "administrator";
    public const string AtKey = "at";
    public const string PriorStatusKey = "priorStatus";
    public const string OverrideKey = "override";

    public static StateShape Shape { get; } = StateShape.Object((PrincipalIdKey, null), (SessionIdKey, null), (AdministratorKey, null), (AtKey, null), (PriorStatusKey, null), (OverrideKey, null));

    public static ActionRecovery Create(string principalId, string sessionId, bool administrator, DateTimeOffset at, string priorStatus, bool takenBack = false)
    {
        var recovery = new JsonObject { [PrincipalIdKey] = principalId, [SessionIdKey] = sessionId, [AdministratorKey] = administrator, [AtKey] = at.ToString("O"), [PriorStatusKey] = priorStatus };
        if (takenBack) recovery[OverrideKey] = true;
        return new(recovery);
    }

    public JsonObject Json => json;
    public string PrincipalId => Text(json, PrincipalIdKey);
    /// <summary>The session that answers for the action now.</summary>
    public string SessionId => Text(json, SessionIdKey);
}

/// <summary>An abandonment its writer replaced with the real outcome: the status it replaced, the recovery that set it, and who replaced it when.</summary>
public sealed class ActionOverride(JsonObject json)
{
    public const string StatusKey = "status";
    public const string RecoveryKey = "recovery";
    public const string PrincipalIdKey = "principalId";
    public const string SessionIdKey = "sessionId";
    public const string AtKey = "at";

    public static StateShape Shape { get; } = StateShape.Object((StatusKey, null), (RecoveryKey, ActionRecovery.Shape), (PrincipalIdKey, null), (SessionIdKey, null), (AtKey, null));

    public static ActionOverride Create(string status, JsonNode? recovery, string principalId, string sessionId, DateTimeOffset at) =>
        new(new JsonObject { [StatusKey] = status, [RecoveryKey] = recovery, [PrincipalIdKey] = principalId, [SessionIdKey] = sessionId, [AtKey] = at.ToString("O") });

    public JsonObject Json => json;
}

/// <summary>The close of the session that answered for an unresolved action: which session, and when the board heard of it.</summary>
public static class ActionOwnerClosed
{
    public const string SessionIdKey = "sessionId";
    public const string AtKey = "at";

    public static StateShape Shape { get; } = StateShape.Object((SessionIdKey, null), (AtKey, null));
}

/// <summary>
/// One action report as the task keeps it: the action and attempt, the outcome and restoration it gave, its body, when and by whom.
/// <see cref="RecoveredKey"/> marks a recovery, <see cref="OverrodeKey"/> a writer's outcome that replaced an abandonment, and
/// <see cref="HistoricalKey"/> a late report on a superseded attempt.
/// </summary>
public sealed class TaskReport(JsonObject json)
{
    public const string ActionIdKey = "actionId";
    public const string AttemptKey = "attempt";
    public const string OutcomeKey = "outcome";
    public const string RestorationKey = "restoration";
    /// <summary>The report's body, as the client gave it.</summary>
    public const string ReportKey = "report";
    public const string AtKey = "at";
    public const string AuthorKey = "author";
    public const string SessionIdKey = "sessionId";
    public const string RecoveredKey = "recovered";
    public const string OverrodeKey = "overrode";
    public const string HistoricalKey = "historical";

    public static StateShape Shape { get; } = StateShape.Object(
        (ActionIdKey, null), (AttemptKey, null), (OutcomeKey, null), (RestorationKey, null), (ReportKey, StateShape.Any), (AtKey, null), (AuthorKey, null), (SessionIdKey, null),
        (RecoveredKey, null), (OverrodeKey, null), (HistoricalKey, null));

    public static TaskReport Create(string actionId, long attempt, string outcome, string restoration, JsonNode? report, DateTimeOffset at, string author, string sessionId) =>
        new(new JsonObject
        {
            [ActionIdKey] = actionId, [AttemptKey] = attempt, [OutcomeKey] = outcome, [RestorationKey] = restoration, [ReportKey] = report, [AtKey] = at.ToString("O"),
            [AuthorKey] = author, [SessionIdKey] = sessionId
        });

    public JsonObject Json => json;

    public string ActionId => Text(json, ActionIdKey);
    public string Outcome => Text(json, OutcomeKey);
    public JsonNode? Body => json[ReportKey];
    public DateTimeOffset At => Date(json, AtKey);

    public bool Recovered
    {
        get => IsTrue(json, RecoveredKey);
        set => json[RecoveredKey] = value;
    }

    /// <summary>The status a writer's real outcome replaced, or empty.</summary>
    public string Overrode
    {
        get => Text(json, OverrodeKey);
        set => json[OverrodeKey] = value;
    }

    public bool Historical
    {
        get => IsTrue(json, HistoricalKey);
        set => json[HistoricalKey] = value;
    }
}

/// <summary>One entry of a task's conversation: its ID, its text and kind, what it replies to, who wrote it when, and the task version it came with.</summary>
public sealed class TaskMessage(JsonObject json)
{
    public const string MessageIdKey = "messageId";
    public const string TaskIdKey = "taskId";
    public const string TextKey = "text";
    public const string KindKey = "kind";
    /// <summary>What the message replies to, as the client gave it.</summary>
    public const string ReplyToKey = "replyTo";
    public const string AuthorKey = "author";
    public const string SessionIdKey = "sessionId";
    public const string AtKey = "at";
    public const string TaskVersionKey = "taskVersion";

    public static StateShape Shape { get; } = StateShape.Object(
        (MessageIdKey, null), (TaskIdKey, null), (TextKey, null), (KindKey, null), (ReplyToKey, StateShape.Any), (AuthorKey, null), (SessionIdKey, null), (AtKey, null),
        (TaskVersionKey, null));

    public static TaskMessage Create(string messageId, string taskId, string text, string kind, JsonNode? replyTo, string author, string sessionId, DateTimeOffset at, long taskVersion) =>
        new(new JsonObject
        {
            [MessageIdKey] = messageId, [TaskIdKey] = taskId, [TextKey] = text, [KindKey] = kind, [ReplyToKey] = replyTo, [AuthorKey] = author, [SessionIdKey] = sessionId,
            [AtKey] = at.ToString("O"), [TaskVersionKey] = taskVersion
        });

    public JsonObject Json => json;

    public string MessageId => JsonFields.Text(json, MessageIdKey);
    public string Text => JsonFields.Text(json, TextKey);
    public DateTimeOffset At => Date(json, AtKey);
}

/// <summary>
/// A task's structured workflow, stored as its writer gave it once validated: its state, packet, acceptance criteria and blockers, the
/// tasks it depends on, the session it targets, its priority, and the evidence of its last recheck.
/// </summary>
public sealed class WorkflowRecord(JsonObject json)
{
    public const string StateKey = "state";
    public const string PacketKey = "packet";
    public const string CriteriaKey = "criteria";
    public const string BlockersKey = "blockers";
    public const string DependsOnKey = "dependsOn";
    public const string TargetSessionIdKey = "targetSessionId";
    public const string PriorityKey = "priority";
    public const string RecheckEvidenceRefKey = "recheckEvidenceRef";

    public static StateShape Shape { get; } = StateShape.Object(
        (StateKey, null), (PacketKey, WorkflowPacket.Shape), (CriteriaKey, WorkflowCriterion.Shape), (BlockersKey, WorkflowBlocker.Shape), (DependsOnKey, null),
        (TargetSessionIdKey, null), (PriorityKey, null), (RecheckEvidenceRefKey, null));

    public JsonObject Json => json;

    public string State => Text(json, StateKey);

    public WorkflowPacket? Packet => json[PacketKey] is JsonObject packet ? new(packet) : null;

    /// <summary>The blockers as stored, or null when they are not a list.</summary>
    public JsonArray? BlockerList => json[BlockersKey] as JsonArray;

    public IEnumerable<WorkflowBlocker> Blockers => (BlockerList ?? []).OfType<JsonObject>().Select(blocker => new WorkflowBlocker(blocker));

    /// <summary>The acceptance criteria as stored, or null when they are not a list.</summary>
    public JsonArray? CriterionList => json[CriteriaKey] as JsonArray;

    /// <summary>The session the task is meant for, or empty for any session.</summary>
    public string TargetSessionId => Text(json, TargetSessionIdKey);

    /// <summary>Frees the task for any session: its target closed.</summary>
    public void ClearTarget() => json.Remove(TargetSessionIdKey);

    public long Priority => Number(json, PriorityKey);

    /// <summary>
    /// The tasks it depends on, or none when <see cref="DependsOnKey"/> is not a list of strings. Boards always accepted unknown workflow
    /// fields, so a stored legacy value of another shape is skipped here rather than failing every later write.
    /// </summary>
    public IEnumerable<string> DependsOn => StringList(json[DependsOnKey]);

    /// <summary>The resources the packet declares, or none (see <see cref="DependsOn"/>).</summary>
    public IEnumerable<string> Resources => Packet?.Resources ?? [];

    /// <summary>Whether the packet declares its resources at all, as a list.</summary>
    public bool DeclaresResources => Packet?.Json[WorkflowPacket.ResourcesKey] is JsonArray;

    internal static IEnumerable<string> StringList(JsonNode? node) =>
        node is JsonArray items && items.All(item => item is JsonValue value && value.GetValueKind() == JsonValueKind.String) ? items.Select(item => item!.GetValue<string>()) : [];
}

/// <summary>A workflow's packet: its goal and target, the authority it runs under, how to restore, when to stop, and the resources it declares.</summary>
public sealed class WorkflowPacket(JsonObject json)
{
    public const string GoalKey = "goal";
    public const string TargetKey = "target";
    /// <summary>The authority the work runs under: prose, or a standing grant named <c>grant:&lt;id&gt;</c>.</summary>
    public const string AuthorityRefKey = "authorityRef";
    public const string RestorationKey = "restoration";
    public const string StopConditionKey = "stopCondition";
    public const string ResourcesKey = "resources";

    public static StateShape Shape { get; } = StateShape.Object((GoalKey, null), (TargetKey, null), (AuthorityRefKey, null), (RestorationKey, null), (StopConditionKey, null), (ResourcesKey, null));

    public JsonObject Json => json;

    public string Goal => Text(json, GoalKey);
    public string Target => Text(json, TargetKey);
    public string AuthorityRef => Text(json, AuthorityRefKey);

    /// <summary>The declared resources, or none when they are not a list of strings.</summary>
    public IEnumerable<string> Resources => WorkflowRecord.StringList(json[ResourcesKey]);
}

/// <summary>One acceptance criterion: its ID and description, its result, and the evidence a pass needs.</summary>
public sealed class WorkflowCriterion(JsonObject json)
{
    public const string IdKey = "id";
    public const string DescriptionKey = "description";
    public const string ResultKey = "result";
    public const string EvidenceRefKey = "evidenceRef";

    public static StateShape Shape { get; } = StateShape.Object((IdKey, null), (DescriptionKey, null), (ResultKey, null), (EvidenceRefKey, null));

    public string Id => Text(json, IdKey);
    public string Result => Text(json, ResultKey);
}

/// <summary>One blocker: its ID and kind, what blocks, and the condition to recheck it on.</summary>
public sealed class WorkflowBlocker(JsonObject json)
{
    public const string IdKey = "id";
    public const string KindKey = "kind";
    public const string SummaryKey = "summary";
    public const string RecheckConditionKey = "recheckCondition";

    public static StateShape Shape { get; } = StateShape.Object((IdKey, null), (KindKey, null), (SummaryKey, null), (RecheckConditionKey, null));

    public string Id => Text(json, IdKey);
    public string Kind => Text(json, KindKey);
    public string Summary => Text(json, SummaryKey);
    public string RecheckCondition => Text(json, RecheckConditionKey);
}

/// <summary>A shared context note: its ID and version, its content and provenance as the client gave them, and who wrote it when.</summary>
public sealed class ContextNote(JsonObject json)
{
    public const string ContextIdKey = "contextId";
    public const string VersionKey = "version";
    public const string ContentKey = "content";
    public const string ProvenanceKey = "provenance";
    public const string AuthorKey = "author";
    public const string UpdatedAtKey = "updatedAt";

    public static StateShape Shape { get; } = StateShape.Object(
        (ContextIdKey, null), (VersionKey, null), (ContentKey, StateShape.Any), (ProvenanceKey, StateShape.Any), (AuthorKey, null), (UpdatedAtKey, null));

    public static ContextNote Create(string contextId, long version, JsonNode? content, JsonNode? provenance, string author, DateTimeOffset updatedAt) => new(new JsonObject
    {
        [ContextIdKey] = contextId, [VersionKey] = version, [ContentKey] = content, [ProvenanceKey] = provenance, [AuthorKey] = author, [UpdatedAtKey] = updatedAt.ToString("O")
    });

    public JsonObject Json => json;

    public long Version => Number(json, VersionKey);
    public JsonNode? Content => json[ContentKey];
    public DateTimeOffset UpdatedAt => Date(json, UpdatedAtKey);
}

/// <summary>
/// A board's standing preferences: convention lines and authority grants under one <see cref="Revision"/>, the board's settings, pending
/// proposals, a bounded change history, and when the hub last proposed each of its own lines.
/// </summary>
public sealed class StandingState(JsonObject json)
{
    public const string RevisionKey = "revision";
    public const string LinesKey = "lines";
    public const string GrantsKey = "grants";
    public const string SettingsKey = "settings";
    public const string ProposalsKey = "proposals";
    public const string HistoryKey = "history";
    /// <summary>When the hub last proposed each of its lines, by line ID, so a rejected line is not proposed again at once.</summary>
    public const string HubProposalsKey = "hubProposals";

    public static StateShape Shape { get; } = StateShape.Object(
        (RevisionKey, null), (LinesKey, StateShape.Map(StandingLine.Shape)), (GrantsKey, StateShape.Map(StandingGrant.Shape)), (SettingsKey, StandingSettings.Shape),
        (ProposalsKey, StateShape.Map(StandingProposal.Shape)), (HistoryKey, StandingChange.Shape), (HubProposalsKey, StateShape.Map(null)));

    public JsonObject Json => json;

    public long Revision
    {
        get => Number(json, RevisionKey);
        set => json[RevisionKey] = value;
    }

    /// <summary>The lines by ID, created empty when there are none.</summary>
    public JsonObject Lines => ObjectAt(json, LinesKey);

    /// <summary>The grants by ID, created empty when there are none.</summary>
    public JsonObject Grants => ObjectAt(json, GrantsKey);

    /// <summary>The pending proposals by ID, created empty when there are none.</summary>
    public JsonObject Proposals => ObjectAt(json, ProposalsKey);

    /// <summary>The change history, oldest first, created empty when there is none.</summary>
    public JsonArray History => ArrayAt(json, HistoryKey);

    public JsonArray? HistoryIfAny => json[HistoryKey] as JsonArray;

    /// <summary>The board's settings, created empty when there are none.</summary>
    public StandingSettings Settings => new(ObjectAt(json, SettingsKey));

    public StandingSettings? SettingsIfAny => json[SettingsKey] is JsonObject settings ? new(settings) : null;

    /// <summary>The stored settings object as it is, for a reply.</summary>
    public JsonNode? SettingsNode => json[SettingsKey];

    public IEnumerable<StandingLine> LineRecords => Records(LinesKey).Select(line => new StandingLine(line));
    public IEnumerable<StandingGrant> GrantRecords => Records(GrantsKey).Select(grant => new StandingGrant(grant));
    public IEnumerable<StandingProposal> ProposalRecords => Records(ProposalsKey).Select(proposal => new StandingProposal(proposal));

    /// <summary>The line entries as stored, objects or not, for a reply that copies them.</summary>
    public IEnumerable<JsonNode?> LineNodes => (json[LinesKey] as JsonObject ?? []).Select(entry => entry.Value);

    public StandingLine? Line(string id) => (json[LinesKey] as JsonObject)?[id] is JsonObject line ? new(line) : null;
    public StandingGrant? Grant(string id) => (json[GrantsKey] as JsonObject)?[id] is JsonObject grant ? new(grant) : null;
    public StandingProposal? Proposal(string id) => (json[ProposalsKey] as JsonObject)?[id] is JsonObject proposal ? new(proposal) : null;

    /// <summary>How many proposals are pending.</summary>
    public int ProposalCount => (json[ProposalsKey] as JsonObject)?.Count ?? 0;

    public bool HasLine(string id) => (json[LinesKey] as JsonObject)?.ContainsKey(id) == true;
    public bool HasGrant(string id) => (json[GrantsKey] as JsonObject)?.ContainsKey(id) == true;

    /// <summary>When the hub last proposed line <paramref name="lineId"/>; the earliest time when it never did.</summary>
    public DateTimeOffset HubProposedAt(string lineId) => json[HubProposalsKey] is JsonObject proposed ? Date(proposed, lineId) : DateTimeOffset.MinValue;

    public void HubProposed(string lineId, DateTimeOffset at) => ObjectAt(json, HubProposalsKey)[lineId] = at.ToString("O");

    /// <summary>Leaves out the history and the proposals: what is left is what a <c>task_get</c> view must fit whole.</summary>
    public void DropHistoryAndProposals()
    {
        json.Remove(HistoryKey);
        json.Remove(ProposalsKey);
    }

    private IEnumerable<JsonObject> Records(string key) => (json[key] as JsonObject ?? []).Select(entry => entry.Value).OfType<JsonObject>();
}

/// <summary>A convention line: its ID and text, who wrote it, and the administrator who approved it when.</summary>
public sealed class StandingLine(JsonObject json)
{
    public const string IdKey = "id";
    public const string TextKey = "text";
    public const string AuthorKey = "author";
    public const string ApprovedByKey = "approvedBy";
    public const string ApprovedAtKey = "approvedAt";

    public static StateShape Shape { get; } = StateShape.Object((IdKey, null), (TextKey, null), (AuthorKey, null), (ApprovedByKey, null), (ApprovedAtKey, null));

    /// <summary>The line a proposal carries: its text, and the ID it would be written under when it names one.</summary>
    public static StateShape ProposedShape { get; } = StateShape.Object((IdKey, null), (TextKey, null));

    public static StandingLine Create(string id, string text, string author, string approvedBy, DateTimeOffset approvedAt) =>
        new(new JsonObject { [IdKey] = id, [TextKey] = text, [AuthorKey] = author, [ApprovedByKey] = approvedBy, [ApprovedAtKey] = approvedAt.ToString("O") });

    /// <summary>A proposal's line: the ID it names, if any, and its text.</summary>
    public static StandingLine Proposed(string id, string text) => new(id.Length > 0 ? new JsonObject { [IdKey] = id, [TextKey] = text } : new JsonObject { [TextKey] = text });

    public JsonObject Json => json;

    /// <summary>The line's ID; for a proposal's line that names none, <paramref name="fallback"/>.</summary>
    public string Id(string fallback = "") => JsonFields.Text(json, IdKey, fallback);

    public string Text => JsonFields.Text(json, TextKey);
}

/// <summary>
/// An authority grant: its ID, scope (repository, resource or action) and targets, when it expires, who granted it when, and who proposed
/// it when it came from a proposal.
/// </summary>
public sealed class StandingGrant(JsonObject json)
{
    public const string IdKey = "id";
    public const string ScopeKey = "scope";
    public const string TargetsKey = "targets";
    public const string ExpiresAtKey = "expiresAt";
    public const string GrantorKey = "grantor";
    public const string GrantedAtKey = "grantedAt";
    public const string ProposedByKey = "proposedBy";

    public static StateShape Shape { get; } = StateShape.Object(
        (IdKey, null), (ScopeKey, null), (TargetsKey, null), (ExpiresAtKey, null), (GrantorKey, null), (GrantedAtKey, null), (ProposedByKey, null));

    /// <summary>The grant a proposal carries, normalized: its scope, targets and expiry, and the ID it would be written under when it names one.</summary>
    public static StateShape ProposedShape { get; } = StateShape.Object((IdKey, null), (ScopeKey, null), (TargetsKey, null), (ExpiresAtKey, null));

    /// <summary>A normalized requested grant: distinct targets and a UTC expiry, under <paramref name="id"/> when it names one.</summary>
    public static StandingGrant Requested(string id, string scope, IEnumerable<string> targets, DateTimeOffset expiresAt)
    {
        var grant = new JsonObject();
        if (id.Length > 0) grant[IdKey] = id;
        grant[ScopeKey] = scope;
        grant[TargetsKey] = new JsonArray(targets.Select(target => (JsonNode)target).ToArray());
        grant[ExpiresAtKey] = expiresAt.ToUniversalTime().ToString("O");
        return new(grant);
    }

    /// <summary>The stored grant <paramref name="requested"/> becomes under <paramref name="id"/>, granted by <paramref name="grantor"/>.</summary>
    public static StandingGrant Create(string id, StandingGrant requested, string grantor, DateTimeOffset grantedAt, string? proposer)
    {
        var record = new JsonObject
        {
            [IdKey] = id, [ScopeKey] = requested.Scope, [TargetsKey] = requested.Targets?.DeepClone(), [ExpiresAtKey] = requested.ExpiresAtText, [GrantorKey] = grantor,
            [GrantedAtKey] = grantedAt.ToString("O")
        };
        if (proposer is not null) record[ProposedByKey] = proposer;
        return new(record);
    }

    public JsonObject Json => json;

    /// <summary>The grant's ID; for a proposal's grant that names none, <paramref name="fallback"/>.</summary>
    public string Id(string fallback = "") => Text(json, IdKey, fallback);

    public string Scope => Text(json, ScopeKey);
    public JsonNode? Targets => json[TargetsKey];

    /// <summary>The targets that are strings; anything else in the list is left out.</summary>
    public HashSet<string> TargetSet =>
        (json[TargetsKey] as JsonArray ?? []).Select(item => item is JsonValue value && value.TryGetValue<string>(out var text) ? text : null).OfType<string>().ToHashSet(StringComparer.Ordinal);

    public DateTimeOffset ExpiresAt => Date(json, ExpiresAtKey);
    public string ExpiresAtText => Text(json, ExpiresAtKey);
}

/// <summary>
/// A pending proposal of a line or a grant: its ID, who proposed it from which session when, and the line or grant itself. The hub's own
/// proposals are marked <see cref="HubKey"/> and have no session.
/// </summary>
public sealed class StandingProposal(JsonObject json)
{
    public const string IdKey = "id";
    public const string ProposedByKey = "proposedBy";
    public const string SessionIdKey = "sessionId";
    public const string ProposedAtKey = "proposedAt";
    public const string HubKey = "hub";
    public const string LineKey = "line";
    public const string GrantKey = "grant";
    public const string PresentedByKey = "presentedBy";
    public const string PresentedPrincipalKey = "presentedPrincipal";
    public const string PresentedAtKey = "presentedAt";
    public const string PresentedRequestKey = "presentedRequest";
    public const string PresentedRevisionKey = "presentedRevision";
    public const string PresentedEffectKey = "presentedEffect";

    public static StateShape Shape { get; } = StateShape.Object(
        (IdKey, null), (ProposedByKey, null), (SessionIdKey, null), (ProposedAtKey, null), (HubKey, null), (LineKey, StandingLine.ProposedShape), (GrantKey, StandingGrant.ProposedShape),
        (PresentedByKey, null), (PresentedPrincipalKey, null), (PresentedAtKey, null), (PresentedRequestKey, null), (PresentedRevisionKey, null), (PresentedEffectKey, null));

    /// <summary>A session's proposal; its line or grant is added next.</summary>
    public static StandingProposal BySession(string id, string proposedBy, string sessionId, DateTimeOffset proposedAt) =>
        new(new JsonObject { [IdKey] = id, [ProposedByKey] = proposedBy, [SessionIdKey] = sessionId, [ProposedAtKey] = proposedAt.ToString("O") });

    /// <summary>The hub's proposal of its own line.</summary>
    public static StandingProposal ByHub(string id, string proposedBy, DateTimeOffset proposedAt, StandingLine line) =>
        new(new JsonObject { [IdKey] = id, [ProposedByKey] = proposedBy, [HubKey] = true, [ProposedAtKey] = proposedAt.ToString("O"), [LineKey] = line.Json });

    public JsonObject Json => json;

    public string Id => Text(json, IdKey);
    public string ProposedBy => Text(json, ProposedByKey);
    public string SessionId => Text(json, SessionIdKey);

    public string PresentedBy => Text(json, PresentedByKey);
    public string PresentedPrincipal => Text(json, PresentedPrincipalKey);
    public string PresentedRequest => Text(json, PresentedRequestKey);
    public long PresentedRevision => Number(json, PresentedRevisionKey);
    public JsonObject? PresentedEffect => json[PresentedEffectKey] as JsonObject;

    /// <summary>A reservation only; it never changes a line, grant, decision history or Standing revision.</summary>
    public void MarkPresented(string principal, string session, string request, DateTimeOffset at, long revision, JsonObject effect)
    {
        json[PresentedByKey] = session; json[PresentedPrincipalKey] = principal; json[PresentedAtKey] = at.ToString("O");
        json[PresentedRequestKey] = request; json[PresentedRevisionKey] = revision; json[PresentedEffectKey] = effect.DeepClone();
    }

    /// <summary>The proposed line, or null for a grant.</summary>
    public StandingLine? Line
    {
        get => json[LineKey] is JsonObject line ? new(line) : null;
        set => json[LineKey] = value?.Json;
    }

    /// <summary>The proposed grant, or null for a line.</summary>
    public StandingGrant? Grant
    {
        get => json[GrantKey] is JsonObject grant ? new(grant) : null;
        set => json[GrantKey] = value?.Json;
    }
}

/// <summary>One standing change: the revision it made, who made it when, and its changes (at most <see cref="RelayLimits.StandingHistoryChanges"/>, with their total when there were more).</summary>
public static class StandingChange
{
    public const string RevisionKey = "revision";
    public const string ByKey = "by";
    public const string AtKey = "at";
    public const string ChangesKey = "changes";
    public const string ChangesTotalKey = "changesTotal";

    public static StateShape Shape { get; } = StateShape.Object((RevisionKey, null), (ByKey, null), (AtKey, null), (ChangesKey, null), (ChangesTotalKey, null));

    public static JsonObject Create(long revision, string by, DateTimeOffset at, IReadOnlyList<string> changes)
    {
        var entry = new JsonObject
        {
            [RevisionKey] = revision, [ByKey] = by, [AtKey] = at.ToString("O"), [ChangesKey] = new JsonArray(changes.Take(RelayLimits.StandingHistoryChanges).Select(change => (JsonNode)change).ToArray())
        };
        if (changes.Count > RelayLimits.StandingHistoryChanges) entry[ChangesTotalKey] = changes.Count;
        return entry;
    }
}

/// <summary>A board's settings. The only one is <see cref="RequireResources"/>.</summary>
public sealed class StandingSettings(JsonObject json)
{
    /// <summary>Refuse <c>ready</c> without <c>packet.resources</c>.</summary>
    public const string RequireResourcesKey = "requireResources";

    public static StateShape Shape { get; } = StateShape.Object((RequireResourcesKey, null));

    /// <summary>The setting, or null when it is unset (or not a boolean).</summary>
    public bool? RequireResources
    {
        get => json[RequireResourcesKey] is JsonValue value && value.TryGetValue<bool>(out var required) ? required : null;
        set => json[RequireResourcesKey] = value;
    }
}

/// <summary>
/// A board's standing orders: the orders by ID, a bounded log of what they did, the revision every order effect and change carries, and
/// the write revision that moves only when an administrator puts or removes one.
/// </summary>
public sealed class OrdersState(JsonObject json)
{
    public const string ItemsKey = "items";
    public const string LogKey = "log";
    /// <summary>One counter for every order effect and change, so raises, closes and releases are applied once and in order.</summary>
    public const string RevisionKey = "revision";
    public const string WriteRevisionKey = "writeRevision";

    public static StateShape Shape { get; } = StateShape.Object((ItemsKey, StateShape.Map(OrderRecord.Shape)), (LogKey, OrderLogEntry.Shape), (RevisionKey, null), (WriteRevisionKey, null));

    public JsonObject Json => json;

    /// <summary>The orders by ID, created empty when there are none.</summary>
    public JsonObject Items => ObjectAt(json, ItemsKey);

    /// <summary>The orders; an entry that is not an object is skipped. With <paramref name="create"/>, an absent map is created first.</summary>
    public IEnumerable<OrderRecord> Orders(bool create = false) =>
        ((create ? Items : json[ItemsKey] as JsonObject) ?? []).Select(entry => entry.Value).OfType<JsonObject>().Select(order => new OrderRecord(order));

    /// <summary>Order <paramref name="orderId"/>, or null.</summary>
    public OrderRecord? Order(string orderId) => Items[orderId] is JsonObject order ? new(order) : null;

    public bool HasOrder(string orderId) => (json[ItemsKey] as JsonObject)?.ContainsKey(orderId) == true;

    public long WriteRevision => Number(json, WriteRevisionKey);

    /// <summary>Advances the write revision and returns it.</summary>
    public long NextWrite()
    {
        var revision = WriteRevision + 1;
        json[WriteRevisionKey] = revision;
        return revision;
    }

    /// <summary>Advances the order revision and returns it.</summary>
    public long NextRevision()
    {
        var revision = Number(json, RevisionKey) + 1;
        json[RevisionKey] = revision;
        return revision;
    }

    /// <summary>Appends to the log, keeping its last <see cref="RelayLimits.OrderLogEntries"/> entries.</summary>
    public void Log(DateTimeOffset at, string orderId, string what)
    {
        var log = ArrayAt(json, LogKey);
        log.Add(new JsonObject { [OrderLogEntry.AtKey] = at.ToString("O"), [OrderLogEntry.OrderIdKey] = orderId, [OrderLogEntry.WhatKey] = what });
        while (log.Count > RelayLimits.OrderLogEntries) log.RemoveAt(0);
    }
}

/// <summary>
/// One standing order: its trigger and actions, who approved it when, when it expires, its runs and failed runs, the trigger revision and
/// order revision of its last run, its last error, and whether it was disabled.
/// </summary>
public sealed class OrderRecord(JsonObject json)
{
    public const string IdKey = "id";
    public const string TriggerKey = "trigger";
    public const string ActionsKey = "actions";
    public const string ApprovedByKey = "approvedBy";
    public const string ApprovedAtKey = "approvedAt";
    public const string ExpiresAtKey = "expiresAt";
    public const string RunsKey = "runs";
    public const string FailuresKey = "failures";
    /// <summary>The trigger revision of the last run (a task's change revision or a lock's generation), so a redelivered trigger runs once.</summary>
    public const string LastRunKey = "lastRun";
    /// <summary>The order revision of the last run, which its effects and failure reports carry.</summary>
    public const string RunRevisionKey = "runRevision";
    public const string LastRunAtKey = "lastRunAt";
    /// <summary>The run whose failure was last counted, so a second failure of one run is not counted again.</summary>
    public const string FailedRunKey = "failedRun";
    public const string LastErrorKey = "lastError";
    public const string LastErrorAtKey = "lastErrorAt";
    public const string DisabledKey = "disabled";

    public static StateShape Shape { get; } = StateShape.Object(
        (IdKey, null), (TriggerKey, OrderTrigger.Shape), (ActionsKey, OrderStep.Shape), (ApprovedByKey, null), (ApprovedAtKey, null), (ExpiresAtKey, null), (RunsKey, null),
        (FailuresKey, null), (LastRunKey, null), (RunRevisionKey, null), (LastRunAtKey, null), (FailedRunKey, null), (LastErrorKey, null), (LastErrorAtKey, null), (DisabledKey, null));

    public static OrderRecord Create(string id, OrderTrigger trigger, JsonArray steps, string approvedBy, DateTimeOffset approvedAt, DateTimeOffset expiresAt) => new(new JsonObject
    {
        [IdKey] = id, [TriggerKey] = trigger.Json, [ActionsKey] = steps, [ApprovedByKey] = approvedBy, [ApprovedAtKey] = approvedAt.ToString("O"),
        [ExpiresAtKey] = expiresAt.ToUniversalTime().ToString("O"), [RunsKey] = 0, [FailuresKey] = 0
    });

    public JsonObject Json => json;

    public string Id => Text(json, IdKey);

    public OrderTrigger? Trigger => json[TriggerKey] is JsonObject trigger ? new(trigger) : null;

    /// <summary>The order's actions; an entry that is not an object is skipped.</summary>
    public IEnumerable<OrderStep> Steps => (json[ActionsKey] as JsonArray ?? []).OfType<JsonObject>().Select(step => new OrderStep(step));

    public DateTimeOffset ExpiresAt => Date(json, ExpiresAtKey);
    public string ExpiresAtText => Text(json, ExpiresAtKey);

    public long Runs
    {
        get => Number(json, RunsKey);
        set => json[RunsKey] = value;
    }

    public long Failures
    {
        get => Number(json, FailuresKey);
        set => json[FailuresKey] = value;
    }

    public string LastRun
    {
        get => Text(json, LastRunKey);
        set => json[LastRunKey] = value;
    }

    public long RunRevision
    {
        get => Number(json, RunRevisionKey);
        set => json[RunRevisionKey] = value;
    }

    public DateTimeOffset LastRunAt
    {
        get => Date(json, LastRunAtKey);
        set => json[LastRunAtKey] = value.ToString("O");
    }

    public long FailedRun
    {
        get => Number(json, FailedRunKey);
        set => json[FailedRunKey] = value;
    }

    public string LastError
    {
        get => Text(json, LastErrorKey);
        set => json[LastErrorKey] = value;
    }

    public DateTimeOffset LastErrorAt
    {
        get => Date(json, LastErrorAtKey);
        set => json[LastErrorAtKey] = value.ToString("O");
    }

    /// <summary>Whether the order was disabled after too many failed runs; only a boolean true counts.</summary>
    public bool Disabled
    {
        get => IsTrue(json, DisabledKey);
        set => json[DisabledKey] = value;
    }
}

/// <summary>What an order runs on: <see cref="OnKey"/> names the trigger, and a task trigger names its task, a claim trigger its resource.</summary>
public sealed class OrderTrigger(JsonObject json)
{
    public const string OnKey = "on";
    public const string TaskIdKey = "taskId";
    public const string ResourceIdKey = "resourceId";

    public static StateShape Shape { get; } = StateShape.Object((OnKey, null), (TaskIdKey, null), (ResourceIdKey, null));

    /// <summary>The key that names a trigger's target: its resource for <c>claim_released</c>, its task for the rest.</summary>
    public static string TargetKey(string on) => on == OrderTriggers.ClaimReleased ? ResourceIdKey : TaskIdKey;

    public static OrderTrigger Create(string on, string target) => new(new JsonObject { [OnKey] = on, [TargetKey(on)] = target });

    public JsonObject Json => json;

    public string On => Text(json, OnKey);
    public string TaskId => Text(json, TaskIdKey);
    public string ResourceId => Text(json, ResourceIdKey);

    /// <summary>The task or resource the trigger watches.</summary>
    public string Target => Text(json, TargetKey(On));
}

/// <summary>One of an order's actions: what it does (<see cref="DoKey"/>), and the task, session and note it names.</summary>
public sealed class OrderStep(JsonObject json)
{
    public const string DoKey = "do";
    public const string TaskIdKey = "taskId";
    public const string SessionIdKey = "sessionId";
    public const string NoteKey = "note";

    public static StateShape Shape { get; } = StateShape.Object((DoKey, null), (TaskIdKey, null), (SessionIdKey, null), (NoteKey, null));

    public JsonObject Json => json;

    public string Do => Text(json, DoKey);

    /// <summary>The task the action names, or null when it names none.</summary>
    public string? TaskId => json[TaskIdKey] is JsonValue value && value.TryGetValue<string>(out var taskId) ? taskId : null;

    public string SessionId => Text(json, SessionIdKey);

    /// <summary>The note a notice carries, or null when it has none.</summary>
    public string? Note => json[NoteKey] is JsonValue value && value.TryGetValue<string>(out var note) ? note : null;
}

/// <summary>One line of the order log: when, which order, and what happened.</summary>
public static class OrderLogEntry
{
    public const string AtKey = "at";
    public const string OrderIdKey = "orderId";
    public const string WhatKey = "what";

    public static StateShape Shape { get; } = StateShape.Object((AtKey, null), (OrderIdKey, null), (WhatKey, null));
}
