using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PkRelay.Contracts;
using static PkRelay.Contracts.RelayErrorCodes;
using static PkRelay.Domain.JsonFields;
using static PkRelay.Domain.Replies;

namespace PkRelay.Domain;

/// <summary>
/// The single entry point for changing an aggregate. It applies the cross-cutting rules every operation shares
/// (payload bound, repository scope, internal-only operations, request idempotency, reply bounds, capacity) and
/// delegates the operation itself to the aggregate's handler. The envelope changes only when the reply is Ok.
/// </summary>
public static class CoordinationStateMachine
{
    private const int MaxBindingBytes = 8_192;

    public static RelayReply Apply(StateEnvelope<CoordinationDocument> envelope, string kind, string key, RelayCaller caller, string operation, JsonObject arguments, DateTimeOffset now)
    {
        if (ByteSize(arguments) > RelayLimits.MaxPayloadBytes) return Error(PayloadTooLarge);
        var repository = Text(arguments, "repositoryId");
        if (!caller.MayAccess(repository)) return Error(RepositoryDenied);
        if (Operations.IsInternal(operation) && !caller.IsInternal) return Error(InternalOnly);
        var state = JsonNode.Parse(envelope.Value.Json)!.AsObject();
        if (operation is RelayTools.SessionOpen or RelayTools.SessionBind && ByteSize(arguments["binding"]) > MaxBindingBytes) return Error(PayloadTooLarge);
        if (kind != AggregateKinds.Control)
        {
            // Every non-control aggregate belongs to the first repository that touched it.
            if (Text(state, "repositoryId") is { Length: > 0 } existing && existing != repository) return Error(RepositoryDenied);
            state["repositoryId"] = repository;
        }

        var requestId = Text(arguments, "requestId");
        var requests = ObjectAt(state, "requests");
        var requestKey = caller.PrincipalId + ":" + operation + ":" + requestId;
        var fingerprint = Fingerprint(arguments);
        if (requestId.Length > 0 && requests[requestKey] is JsonObject prior) return Replay(kind, state, operation, arguments, prior, fingerprint, now);

        var context = new AggregateContext(state, caller, operation, arguments, now) { Key = key };
        RelayReply result;
        try
        {
            result = kind switch
            {
                AggregateKinds.Control => ControlAggregate.Handle(context),
                AggregateKinds.Session => SessionAggregate.Handle(context),
                AggregateKinds.Board => TaskBoardAggregate.Handle(context),
                AggregateKinds.Lock => LockAggregate.Handle(context),
                AggregateKinds.Anomaly => AnomalyMonitor.Handle(context),
                _ => Error(UnknownAggregate)
            };
        }
        catch (ArgumentException exception) { return Error(InvalidArguments, new { error = exception.Message }); }
        catch (InvalidOperationException) { return Error(InvalidArguments); }
        catch (FormatException) { return Error(InvalidArguments); }
        if (!result.Ok) return result;

        if (kind == AggregateKinds.Board)
        {
            if (TaskBoardAggregate.ExceedsTaskCapacity(state)) return Error(TaskCapacityExceeded);
            result = TaskBoardAggregate.FitTaskRead(state, operation, arguments, result);
        }
        // Leave room for application composition (session attachments and convenience outcomes).
        result = BoundWire(result, RelayLimits.AggregateReplyBytes);
        // Reject before committing if essential authorization or fencing metadata alone cannot fit.
        if (!FitsWire(result, RelayLimits.AggregateReplyBytes)) return Error(PayloadTooLarge);
        if (envelope.Effects.Count + context.Effects.Count > RelayLimits.MaxRecords) return Error(CapacityExceeded);
        if (requestId.Length > 0 && IsMutation(operation))
        {
            if (requests.Count >= RelayLimits.MaxRecords) return Error(CapacityExceeded);
            requests[requestKey] = new JsonObject { ["fingerprint"] = fingerprint, ["code"] = result.Code, ["json"] = result.DataJson, ["version"] = result.Version };
        }
        envelope.Value = new() { Json = state.ToJsonString() };
        // Nothing changes the state object after this point, so the commit's search indexing can read it instead of parsing again.
        envelope.Value.KeepParsed(state);
        envelope.Effects.AddRange(context.Effects);
        return result;
    }

    /// <summary>A retried request returns its original reply, unless its grant has since gone stale.</summary>
    private static RelayReply Replay(string kind, JsonObject state, string operation, JsonObject arguments, JsonObject prior, string fingerprint, DateTimeOffset now)
    {
        if (Text(prior, "fingerprint") != fingerprint) return Error(RequestConflict);
        var tasks = ObjectAt(state, "tasks");
        var taskId = Text(arguments, "taskId");
        var session = Text(arguments, "sessionId");
        if (operation == RelayTools.ActionBegin && tasks[taskId] is JsonObject actionTask && ObjectAt(actionTask, "actions")[Text(arguments, "actionId")] is JsonObject currentAction)
            return Ok(new { action = currentAction, replayAllowed = false, existing = true }, Number(actionTask, "version"));
        if (operation is RelayTools.ClaimAcquire or RelayTools.ClaimRenew or RelayTools.TaskClaim)
        {
            var grant = JsonNode.Parse(Text(prior, "json"))!.AsObject();
            if (Date(grant, "leaseUntil") <= now) return Error(StaleClaim);
            if (kind == AggregateKinds.Lock && (Text(state, "holder") != session || Number(state, "generation") != Number(grant, "generation"))) return Error(StaleClaim);
            if (kind == AggregateKinds.Board && tasks[taskId] is JsonObject task)
            {
                if (Text(task, "owner") != session || Number(task, "claimGeneration") != Number(grant, "claimGeneration")) return Error(StaleClaim);
                if (operation == RelayTools.TaskClaim && !TaskWorkflow.AllowsExecution(task, session)) return TaskWorkflow.NotReady(task);
            }
        }
        return new(true, Text(prior, "code"), Text(prior, "json"), Number(prior, "version"));
    }

    private static string Fingerprint(JsonObject arguments)
    {
        var significant = arguments.DeepClone().AsObject();
        significant.Remove("hubIncarnation");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(significant.ToJsonString())));
    }

    /// <summary>Mutations are deduplicated by requestId and committed; reads never are.</summary>
    public static bool IsMutation(string operation) =>
        operation is not (Operations.Verify or Operations.Resolve or Operations.ResolveAll or Operations.NextWork or Operations.ClaimState or Operations.SessionOwners or Operations.Standing)
        && (!RelayToolCatalog.TryGet(operation, out var tool) || tool.IsMutation);

    /// <summary>Whether a successful operation must be persisted. A legacy board's first read durably seeds current change markers without changing task versions.</summary>
    public static bool RequiresCommit(string kind, string operation, string? priorTaskRevision, StateEnvelope<CoordinationDocument> envelope) =>
        IsMutation(operation) || kind == AggregateKinds.Board && priorTaskRevision != TaskRevision(envelope);

    public static string? TaskRevision(StateEnvelope<CoordinationDocument> envelope) => JsonNode.Parse(envelope.Value.Json)?["taskRevision"]?.ToJsonString();

    /// <summary>Stable identity of one action attempt, shared by the board and the resource locks it blocks.</summary>
    public static string ActionKey(JsonObject args, string actionId, long attempt) => string.Join("/", new[]
    {
        Text(args, "repositoryId"), Text(args, "boardId", "default"), Text(args, "taskId"), actionId, attempt.ToString(CultureInfo.InvariantCulture)
    }.Select(Uri.EscapeDataString));

    /// <summary>
    /// Rebuilds the aggregate's timers from its own state: lease expiry from its own and its tasks' leases, a lock's lease check, and the
    /// anomaly monitor's next check.
    /// </summary>
    public static void ScheduleLeases(StateEnvelope<CoordinationDocument> envelope)
    {
        var state = JsonNode.Parse(envelope.Value.Json)!.AsObject();
        envelope.DueWork.RemoveAll(x => x.Kind is "lease" or DueWorkKinds.LeaseWarning or DueWorkKinds.AnomalyCheck);
        if (DateTimeOffset.TryParse(Text(state, "nextCheckAt"), out var check)) envelope.DueWork.Add(new(DueWorkKinds.AnomalyCheck, DueWorkKinds.AnomalyCheck, check));
        if (DateTimeOffset.TryParse(Text(state, "leaseUntil"), out var lease)) envelope.DueWork.Add(new("lease", "lease", lease));
        // A lock whose holder has an action in flight checks its lease shortly before it ends (claim_expiring).
        if (AttentionSources.LeaseWarningAt(state) is { } warning) envelope.DueWork.Add(new(DueWorkKinds.LeaseWarning, DueWorkKinds.LeaseWarning, warning));
        if (state["tasks"] is JsonObject tasks)
            foreach (var entry in tasks)
                if (entry.Value is JsonObject task && DateTimeOffset.TryParse(Text(task, "leaseUntil"), out var taskLease)) envelope.DueWork.Add(new("lease:" + entry.Key, "lease", taskLease));
    }

    /// <summary>Records who caused new outbox effects so delivery runs with the originating caller's authority.</summary>
    public static void StampEffects(List<DurableEffect> effects, int firstNew, RelayCaller caller)
    {
        for (var index = firstNew; index < effects.Count; index++)
        {
            var effect = effects[index];
            var payload = JsonNode.Parse(effect.PayloadJson)!.AsObject();
            payload["__principalId"] = caller.PrincipalId;
            payload["__administrator"] = caller.IsAdministrator;
            payload["__repositories"] = JsonSerializer.SerializeToNode(caller.Repositories);
            effects[index] = effect with { PayloadJson = payload.ToJsonString() };
        }
    }

    /// <summary>Removes the originating caller recorded by <see cref="StampEffects"/> and returns it as an internal caller.</summary>
    public static RelayCaller TakeEffectCaller(JsonObject payload)
    {
        var caller = new RelayCaller(Text(payload, "__principalId"), payload["__administrator"]?.GetValue<bool>() ?? false, payload["__repositories"]?.Deserialize<string[]>() ?? [], true);
        payload.Remove("__principalId"); payload.Remove("__administrator"); payload.Remove("__repositories");
        return caller;
    }
}
