using System.Collections.Frozen;

namespace PkRelay.Contracts;

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
    public const string InternalOnly = "InternalOnly";
    public const string ActionDenied = "ActionDenied";
    public const string MachineRevoked = "MachineRevoked";
    public const string MachineRequired = "MachineRequired";
    public const string HttpsRequired = "HttpsRequired";
    public const string RateLimited = "RateLimited";

    // Records
    public const string NotFound = "NotFound";
    public const string SessionUnknown = "SessionUnknown";
    public const string ResourceUnknown = "ResourceUnknown";
    public const string ActionUnknown = "ActionUnknown";

    // Optimistic concurrency, claims and coordination state
    public const string VersionConflict = "VersionConflict";
    public const string RequestConflict = "RequestConflict";
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

    // Infrastructure
    public const string StorageFailure = "StorageFailure";
    public const string DeliveryPending = "DeliveryPending";
    public const string DeliveryUnavailable = "DeliveryUnavailable";
    public const string Unavailable = "Unavailable";
    public const string InternalError = "InternalError";

    private static readonly FrozenSet<string> Conflicts = new[]
    {
        VersionConflict, RequestConflict, StaleClaim, Paused, Busy, ActionConflict, UnresolvedAction,
        DeltaRestartRequired, WorkflowRecheckRequired, WorkflowIncomplete, WorkflowNotReady
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> Denials = new[] { RepositoryDenied, SessionDenied, AdministratorRequired, MachineRevoked, MachineRequired }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> Missing = new[] { UnknownTool, NotFound }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Recognized conflicts are the failures a client reconciles by rereading state; they keep their code, data and version end to end.</summary>
    public static RelayErrorKind Classify(string code) =>
        code == PayloadTooLarge ? RelayErrorKind.TooLarge
        : Conflicts.Contains(code) ? RelayErrorKind.Conflict
        : Denials.Contains(code) ? RelayErrorKind.Denied
        : Missing.Contains(code) ? RelayErrorKind.Missing
        : RelayErrorKind.Invalid;
}
