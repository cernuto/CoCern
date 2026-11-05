using System.Collections.Frozen;

namespace CoCern.Contracts;

/// <summary>How a client should treat a failed reply. REST maps each kind to one HTTP status.</summary>
public enum RelayErrorKind
{
    Invalid,
    Denied,
    Missing,
    Conflict,
    TooLarge
}

/// <summary>Stable domain reply codes. Clients switch on these, so values never change once published.</summary>
public static class RelayErrorCodes
{
    // Request shape and capacity
    public const string UnknownTool = "UnknownTool";
    public const string InvalidArguments = "InvalidArguments";
    public const string RequestIdRequired = "RequestIdRequired";
    public const string PayloadTooLarge = "PayloadTooLarge";
    public const string CapacityExceeded = "CapacityExceeded";
    public const string TaskCapacityExceeded = "TaskCapacityExceeded";
    public const string UnknownAggregate = "UnknownAggregate";
    public const string UnknownCommand = "UnknownCommand";

    // Authorization
    public const string RepositoryDenied = "RepositoryDenied";
    public const string SessionDenied = "SessionDenied";
    public const string AdministratorRequired = "AdministratorRequired";
    /// <summary>The chat presenting this administrative call was the recorded manager and has been replaced or revoked. The caller's token is not the question.</summary>
    public const string ManagerRevoked = "ManagerRevoked";
    /// <summary>A chat tried to record its own appointment. Paul names the manager from the dashboard or through another chat.</summary>
    public const string SelfAppointment = "SelfAppointment";
    /// <summary>The caller is an administrator, but this tool answers only a connection from the hub's own PC.</summary>
    public const string HubPcRequired = "HubPcRequired";
    public const string InternalOnly = "InternalOnly";
    public const string ActionDenied = "ActionDenied";
    public const string MachineRevoked = "MachineRevoked";
    /// <summary>The hub dropped this machine ID for inactivity and never reissues it; enroll again under a new ID.</summary>
    public const string MachineExpired = "MachineExpired";
    public const string MachineRequired = "MachineRequired";
    public const string HttpsRequired = "HttpsRequired";
    /// <summary>Enrollment needs the hub's join code, and the request carried none or a wrong one.</summary>
    public const string JoinCodeRequired = "JoinCodeRequired";
    public const string RateLimited = "RateLimited";

    // Records
    public const string NotFound = "NotFound";
    /// <summary>The task was finished and moved to the archive: <c>task_get</c> still reads it; claims, updates and actions are refused.</summary>
    public const string TaskArchived = "TaskArchived";
    public const string SessionUnknown = "SessionUnknown";
    public const string ResourceUnknown = "ResourceUnknown";
    public const string ActionUnknown = "ActionUnknown";

    // Optimistic concurrency, claims and coordination state
    public const string VersionConflict = "VersionConflict";
    public const string RequestConflict = "RequestConflict";
    /// <summary>A retry of a request whose stored reply was evicted early to make room; it did not run again. Read the current state.</summary>
    public const string RequestExpired = "RequestExpired";
    public const string StaleClaim = "StaleClaim";
    public const string Paused = "Paused";
    public const string Busy = "Busy";
    public const string ClaimRequired = "ClaimRequired";
    public const string OwnerConflict = "OwnerConflict";
    public const string ResourceSetConflict = "ResourceSetConflict";
    public const string AliasConflict = "AliasConflict";
    public const string DeltaRestartRequired = "DeltaRestartRequired";

    // External actions
    public const string ActionConflict = "ActionConflict";
    public const string ActionUncertain = "ActionUncertain";
    public const string ActionPending = "ActionPending";
    public const string ActionChanged = "ActionChanged";
    public const string AttemptConflict = "AttemptConflict";
    public const string AttemptRequired = "AttemptRequired";
    public const string TriggerConsumed = "TriggerConsumed";
    public const string UnresolvedAction = "UnresolvedAction";
    public const string RestorationRequired = "RestorationRequired";
    public const string RestorationResolved = "RestorationResolved";
    public const string InvalidOutcome = "InvalidOutcome";
    public const string InvalidRestoration = "InvalidRestoration";

    // Structured workflow
    public const string InvalidWorkflow = "InvalidWorkflow";
    public const string WorkflowRecheckRequired = "WorkflowRecheckRequired";
    public const string WorkflowIncomplete = "WorkflowIncomplete";
    public const string WorkflowNotReady = "WorkflowNotReady";
    public const string DependencyCycle = "DependencyCycle";
    public const string DependencyUnknown = "DependencyUnknown";
    /// <summary><c>task_claim</c> or <c>action_begin</c> on a task whose <c>dependsOn</c> tasks are not all completed; <c>dependsOn</c> lists the unfinished ones.</summary>
    public const string DependenciesIncomplete = "DependenciesIncomplete";
    public const string GrantInvalid = "GrantInvalid";
    /// <summary>
    /// <c>task_claim</c> without every registered resource its <c>packet.resources</c> declares in <c>resourceIds</c>, or <c>action_begin</c>
    /// on a task whose claim does not cover them; <c>missing</c> lists them (and <c>unregistered</c> the declared names no resource answers
    /// to, which a board with <c>requireResources</c> refuses).
    /// </summary>
    public const string ResourcesRequired = "ResourcesRequired";
    /// <summary>
    /// Internal: the board asks the hub to resolve the task's declared <c>packet.resources</c> (<c>declared</c>) and call again. Only a call
    /// that set <c>resolveDeclared</c> gets it, so the hub reads nothing extra for a task that declares none. Never reaches a client.
    /// </summary>
    public const string DeclaredResourcesNeeded = "DeclaredResourcesNeeded";

    // Attention
    /// <summary>An item that blocks another session cannot be postponed again (at most 2 snoozes and 30 minutes).</summary>
    public const string SnoozeLimit = "SnoozeLimit";

    // Named peers
    /// <summary>More than one chat here has that name; <c>choices</c> lists them. Nothing breaks the tie: ask again with a choice's <c>ref</c>.</summary>
    public const string PeerAmbiguous = "PeerAmbiguous";

    // Infrastructure
    public const string StorageFailure = "StorageFailure";
    public const string DeliveryPending = "DeliveryPending";
    public const string DeliveryUnavailable = "DeliveryUnavailable";
    public const string Unavailable = "Unavailable";
    /// <summary>The hub keeps no search index (search is turned off, or the store has none).</summary>
    public const string SearchUnavailable = "SearchUnavailable";
    /// <summary>Analytics is turned off, or no snapshot has been taken yet.</summary>
    public const string AnalyticsUnavailable = "AnalyticsUnavailable";
    /// <summary>Ad hoc SQL is turned off on this hub, or the SQL is not exactly one SELECT.</summary>
    public const string AnalyticsRefused = "AnalyticsRefused";
    /// <summary>The query ran past the hub's per-query timeout; ask a narrower question.</summary>
    public const string AnalyticsTimeout = "AnalyticsTimeout";
    /// <summary>DuckDB rejected the query, for example an unknown column or a file read; the reply says why.</summary>
    public const string AnalyticsFailed = "AnalyticsFailed";
    /// <summary>Standing orders are turned off on this hub (<c>Relay:Orders:Enabled</c>).</summary>
    public const string OrdersUnavailable = "OrdersUnavailable";
    /// <summary>The doc cache is turned off on this hub (<c>Relay:DocCache:Enabled</c>), or the repository has no synced docs.</summary>
    public const string DocCacheUnavailable = "DocCacheUnavailable";
    /// <summary>The doc cache is on, but no repositories are configured for it (<c>Relay:DocCache:Repositories</c>).</summary>
    public const string NoRepositoriesConfigured = "NoRepositoriesConfigured";
    public const string InternalError = "InternalError";
    /// <summary>
    /// Internal: an outbox effect's payload lacked a field its message requires, or had one of the wrong type; <c>fields</c> names them.
    /// The hub logs it once and retries or drops it by the message's delivery class. Never reaches a client.
    /// </summary>
    public const string MalformedEffect = "MalformedEffect";

    private static readonly FrozenSet<string> Conflicts = new[]
    {
        VersionConflict, RequestConflict, RequestExpired, StaleClaim, Paused, Busy, ActionConflict, UnresolvedAction,
        DeltaRestartRequired, WorkflowRecheckRequired, WorkflowIncomplete, WorkflowNotReady, DependenciesIncomplete, TaskArchived, SnoozeLimit, PeerAmbiguous
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> Denials = new[] { RepositoryDenied, SessionDenied, AdministratorRequired, ManagerRevoked, SelfAppointment, HubPcRequired, MachineRevoked, MachineRequired, JoinCodeRequired }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> Missing = new[] { UnknownTool, NotFound }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Recognized conflicts are the failures a client reconciles by rereading state; they keep their code, data and version end to end.</summary>
    public static RelayErrorKind Classify(string code) =>
        code == PayloadTooLarge ? RelayErrorKind.TooLarge
        : Conflicts.Contains(code) ? RelayErrorKind.Conflict
        : Denials.Contains(code) ? RelayErrorKind.Denied
        : Missing.Contains(code) ? RelayErrorKind.Missing
        : RelayErrorKind.Invalid;
}
