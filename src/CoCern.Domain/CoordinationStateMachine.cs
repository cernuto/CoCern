using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using static CoCern.Contracts.RelayErrorCodes;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Domain;

/// <summary>
/// The single entry point for changing an aggregate. It applies the cross-cutting rules every operation shares
/// (payload bound, repository scope, internal-only operations, request idempotency, reply bounds, capacity) and
/// delegates the operation itself to the aggregate's handler. The envelope changes only when the reply is Ok.
/// </summary>
public static class CoordinationStateMachine
{
    private const int MaxBindingBytes = 8_192;

    /// <param name="ordersEnabled">Whether the hub runs standing orders; the grain passes its setting, so the domain stays free of configuration.</param>
    /// <param name="archiveAfterDays">Days after completion before a finished task is archived (<c>Relay:Board:ArchiveAfterDays</c>); 0 archives only when a board fills.</param>
    public static RelayReply Apply(StateEnvelope<CoordinationDocument> envelope, string kind, string key, RelayCaller caller, string operation, JsonObject arguments, DateTimeOffset now, bool ordersEnabled = false,
        int archiveAfterDays = RelayLimits.DefaultArchiveAfterDays)
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
            if (Text(state, "repositoryId") is { Length: > 0 } existing && existing != repository)
                // A message for a session in another repository can never be delivered, and a task cannot wait on such a session; dropping
                // either keeps the sender's outbox moving.
                return operation switch
                {
                    Operations.Deliver => Ok(new { delivered = false, repositoryDenied = true }),
                    Operations.SessionTargetWatch => Ok(new { watching = false, repositoryDenied = true }),
                    _ => Error(RepositoryDenied)
                };
            state["repositoryId"] = repository;
        }

        var requestId = Text(arguments, "requestId");
        var requests = ObjectAt(state, ReadModels.RequestsKey);
        var requestKey = RequestKey(caller.PrincipalId, Text(arguments, "sessionId"), operation, requestId);
        var fingerprint = Fingerprint(operation, arguments);
        if (requestId.Length > 0 && requests[requestKey] is JsonObject prior) return Replay(kind, state, operation, arguments, prior, fingerprint, now);
        // Its stored reply was evicted to make room before its window ended: refuse rather than run the write twice.
        if (requestId.Length > 0 && IsMutation(operation) && Retention.IsBuried(state, requestKey, now)) return Error(RequestExpired, new { readCurrentState = true });

        var context = new AggregateContext(state, caller, operation, arguments, now) { Key = key, OrdersEnabled = ordersEnabled, ArchiveAfterDays = archiveAfterDays };
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
            // Bounded by age, then by count: a full map drops expired replays, then its oldest, and remembers those as tombstones.
            if (requests.Count >= RelayLimits.MaxRecords) Retention.Bury(state, Retention.MakeRoom(requests, now), now);
            requests[requestKey] = new JsonObject { ["fingerprint"] = fingerprint, ["code"] = result.Code, ["json"] = result.DataJson, ["version"] = result.Version, ["at"] = Retention.Stamp(now) };
        }
        envelope.Value = new() { Json = state.ToJsonString() };
        // Nothing changes the state object after this point, so the commit's search indexing can read it instead of parsing again.
        envelope.Value.KeepParsed(state);
        // New effects deliver with this call's authority, and the aggregate's timers follow its new state.
        StampEffects(context.Effects, 0, caller);
        envelope.Effects.AddRange(context.Effects);
        ScheduleLeases(envelope, state, kind == AggregateKinds.Board ? archiveAfterDays : 0, now);
        // Tasks this operation archived leave the board in this commit; storage writes them to the archive in the same transaction.
        envelope.QueueArchive(context.Archived);
        return result;
    }

    /// <summary>A retried request returns its original reply, unless its grant has since gone stale or its task may no longer run (the claim gate).</summary>
    private static RelayReply Replay(string kind, JsonObject state, string operation, JsonObject arguments, JsonObject prior, string fingerprint, DateTimeOffset now)
    {
        if (Text(prior, "fingerprint") != fingerprint && Text(prior, "fingerprint") != LegacyFingerprint(arguments)) return Error(RequestConflict);
        var stored = JsonNode.Parse(Text(prior, "json")) as JsonObject ?? new JsonObject();
        // A grant is replayed only while it is still live; whether it is still the same grant is each aggregate's own rule.
        if (operation is RelayTools.ClaimAcquire or RelayTools.ClaimRenew or RelayTools.TaskClaim && Date(stored, "leaseUntil") <= now) return Error(StaleClaim);
        var refusal = kind switch
        {
            AggregateKinds.Board => TaskBoardAggregate.ReplayRefusal(state, operation, arguments, stored, now),
            AggregateKinds.Lock => LockAggregate.ReplayRefusal(state, operation, arguments, stored),
            _ => null
        };
        return refusal ?? new(true, Text(prior, "code"), Text(prior, "json"), Number(prior, "version"));
    }

    /// <summary>
    /// The request's identity for replay: only the client's public arguments, never <c>hubIncarnation</c>, a <c>__</c> field or one of
    /// <see cref="ServerArguments"/> (a retry may get different values for those).
    /// </summary>
    private static string Fingerprint(string operation, JsonObject arguments)
    {
        var significant = arguments.DeepClone().AsObject();
        significant.Remove("hubIncarnation");
        foreach (var key in significant.Select(entry => entry.Key).Where(key => key.StartsWith("__", StringComparison.Ordinal)).ToArray()) significant.Remove(key);
        // The hub replaces a claim's resourceIds with their canonical IDs, which can change between retries (an alias re-registered): the
        // client's own list is what identifies the request.
        if (significant[ServerArguments.RequestedResources] is { } requested) significant["resourceIds"] = requested.DeepClone();
        ServerArguments.StripClientValues(operation, significant);
        return Hash(significant);
    }

    /// <summary>
    /// The fingerprint replays stored before server arguments were left out; a retry that matches it still replays. Fields the hub adds
    /// only since then are left out, since those older requests never carried them.
    /// </summary>
    private static string LegacyFingerprint(JsonObject arguments)
    {
        var significant = arguments.DeepClone().AsObject();
        significant.Remove("hubIncarnation");
        foreach (var key in new[] { ServerArguments.RequestedResources, Operations.ResolveDeclaredField, Operations.LockRefusalField }) significant.Remove(key);
        return Hash(significant);
    }

    private static string Hash(JsonObject significant) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(significant.ToJsonString())));

    /// <summary>
    /// Where a request's stored reply lives: per principal, session and operation. Every chat on one PC shares a principal and agents reuse
    /// simple request IDs, so without the session one chat's retry could replay another chat's reply.
    /// </summary>
    public static string RequestKey(string principalId, string sessionId, string operation, string requestId) =>
        principalId + (sessionId.Length > 0 ? "/" + sessionId : "") + ":" + operation + ":" + requestId;

    /// <summary>
    /// Drops a stored reply whose effect was undone (a task claim rolled back because a resource was busy), so a retry with the same
    /// requestId runs again instead of replaying a claim that no longer exists.
    /// </summary>
    public static void ForgetRequest(JsonObject state, string principalId, string sessionId, string operation, string requestId)
    {
        if (requestId.Length > 0 && state[ReadModels.RequestsKey] is JsonObject requests) requests.Remove(RequestKey(principalId, sessionId, operation, requestId));
    }

    /// <summary>Mutations are deduplicated by requestId and committed; reads never are.</summary>
    public static bool IsMutation(string operation) =>
        !Operations.IsRead(operation) && (!RelayToolCatalog.TryGet(operation, out var tool) || tool.IsMutation);

    /// <summary>Whether a successful operation must be persisted. A legacy board's first read durably seeds current change markers without changing task versions.</summary>
    public static bool RequiresCommit(string kind, string operation, string? priorTaskRevision, StateEnvelope<CoordinationDocument> envelope) =>
        IsMutation(operation) || kind == AggregateKinds.Board && priorTaskRevision != TaskRevision(envelope);

    public static string? TaskRevision(StateEnvelope<CoordinationDocument> envelope) => JsonNode.Parse(envelope.Value.Json)?["taskRevision"]?.ToJsonString();

    /// <summary>The board, task and action an <see cref="ActionKey"/> names; an older key without them reads as the default board.</summary>
    public static (string BoardId, string TaskId, string ActionId) ParseActionKey(string actionKey)
    {
        var parts = actionKey.Split('/');
        return parts.Length >= 4 ? (Uri.UnescapeDataString(parts[1]), Uri.UnescapeDataString(parts[2]), Uri.UnescapeDataString(parts[3]))
            : parts.Length >= 3 ? (Uri.UnescapeDataString(parts[1]), Uri.UnescapeDataString(parts[2]), "") : (AggregateKinds.DefaultBoard, "", actionKey);
    }

    /// <summary>Stable identity of one action attempt, shared by the board and the resource locks it blocks.</summary>
    public static string ActionKey(JsonObject args, string actionId, long attempt) =>
        ActionKey(Text(args, "repositoryId"), Text(args, "boardId", "default"), Text(args, "taskId"), actionId, attempt);

    /// <inheritdoc cref="ActionKey(JsonObject, string, long)"/>
    public static string ActionKey(string repositoryId, string boardId, string taskId, string actionId, long attempt) =>
        string.Join("/", new[] { repositoryId, boardId, taskId, actionId, attempt.ToString(CultureInfo.InvariantCulture) }.Select(Uri.EscapeDataString));

    /// <summary>
    /// Rebuilds the aggregate's timers from its own state: lease expiry from its own and its tasks' leases, a lock's lease check, and the
    /// anomaly monitor's next check, and a board's next archive sweep (with <paramref name="archiveAfterDays"/> above 0).
    /// </summary>
    public static void ScheduleLeases(StateEnvelope<CoordinationDocument> envelope, int archiveAfterDays = 0, DateTimeOffset? now = null) =>
        ScheduleLeases(envelope, JsonNode.Parse(envelope.Value.Json)!.AsObject(), archiveAfterDays, now ?? DateTimeOffset.MinValue);

    /// <remarks>
    /// Only leases still running get a timer. A lease's end needs no work of its own (lapses are read lazily); an ended one kept its
    /// <c>leaseUntil</c>, and rescheduling it on every write cost one full commit per previously claimed task.
    /// </remarks>
    private static void ScheduleLeases(StateEnvelope<CoordinationDocument> envelope, JsonObject state, int archiveAfterDays, DateTimeOffset now)
    {
        var backoff = envelope.DueWork.FirstOrDefault(x => x.Id == DueWorkKinds.TaskArchiveRetry)?.DueAt;
        envelope.DueWork.RemoveAll(x => x.Kind is DueWorkKinds.Lease or DueWorkKinds.LeaseWarning or DueWorkKinds.AnomalyCheck or DueWorkKinds.TaskArchive);
        // A board's next archive sweep, when a finished task will pass Relay:Board:ArchiveAfterDays; a refused sweep's backoff still holds.
        if (TaskArchiving.NextSweep(state, archiveAfterDays) is { } sweep)
            envelope.DueWork.Add(backoff > sweep ? new(DueWorkKinds.TaskArchiveRetry, DueWorkKinds.TaskArchive, backoff.Value) : new(DueWorkKinds.TaskArchive, DueWorkKinds.TaskArchive, sweep));
        if (DateTimeOffset.TryParse(Text(state, State.AnomalyState.NextCheckAtKey), out var check)) envelope.DueWork.Add(new(DueWorkKinds.AnomalyCheck, DueWorkKinds.AnomalyCheck, check));
        if (DateTimeOffset.TryParse(Text(state, "leaseUntil"), out var lease) && lease > now) envelope.DueWork.Add(new(DueWorkKinds.Lease, DueWorkKinds.Lease, lease));
        // A lock whose holder has an action in flight checks its lease shortly before it ends (claim_expiring).
        if (AttentionSources.LeaseWarningAt(state) is { } warning) envelope.DueWork.Add(new(DueWorkKinds.LeaseWarning, DueWorkKinds.LeaseWarning, warning));
        if (state["tasks"] is JsonObject tasks)
            foreach (var entry in tasks)
                if (entry.Value is JsonObject task && DateTimeOffset.TryParse(Text(task, "leaseUntil"), out var taskLease) && taskLease > now) envelope.DueWork.Add(new(DueWorkKinds.Lease + ":" + entry.Key, DueWorkKinds.Lease, taskLease));
    }

    /// <summary>Records who caused new outbox effects so delivery runs with the originating caller's authority.</summary>
    public static void StampEffects(List<DurableEffect> effects, int firstNew, RelayCaller caller)
    {
        for (var index = firstNew; index < effects.Count; index++)
        {
            var effect = effects[index];
            var payload = JsonNode.Parse(effect.PayloadJson)!.AsObject();
            // An effect emitted with its own caller keeps it.
            if (payload.ContainsKey(CallerPrincipal)) continue;
            effects[index] = effect with { PayloadJson = WithCaller(payload, caller).ToJsonString() };
        }
    }

    /// <summary>Removes the originating caller recorded by <see cref="StampEffects"/> and returns it as an internal caller.</summary>
    public static RelayCaller TakeEffectCaller(JsonObject payload)
    {
        var caller = new RelayCaller(Text(payload, CallerPrincipal), payload[CallerAdministrator]?.GetValue<bool>() ?? false, payload[CallerRepositories]?.Deserialize<string[]>() ?? [], true);
        payload.Remove(CallerPrincipal); payload.Remove(CallerAdministrator); payload.Remove(CallerRepositories);
        return caller;
    }

    // An effect's caller rides in its payload under these keys (stripped from client arguments, see Fingerprint's "__" rule).
    private const string CallerPrincipal = "__principalId";
    private const string CallerAdministrator = "__administrator";
    private const string CallerRepositories = "__repositories";

    /// <summary>Records <paramref name="caller"/> in an effect's payload, so its delivery runs with that authority.</summary>
    internal static JsonObject WithCaller(JsonObject payload, RelayCaller caller)
    {
        payload[CallerPrincipal] = caller.PrincipalId;
        payload[CallerAdministrator] = caller.IsAdministrator;
        payload[CallerRepositories] = JsonSerializer.SerializeToNode(caller.Repositories);
        return payload;
    }
}
