using System.Text.Json.Nodes;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using CoCern.Contracts;
using CoCern.Domain.State;

namespace CoCern.Domain;

/// <summary>
/// Bounded owner-lifecycle subscriptions in existing session state and outbox.
/// Shared source hooks/router are deliberately not registered here. The final
/// producer must call close/stranded only from verified closure/stranding paths.
/// </summary>
public static class EventWaitSources
{
    public const string SourceWatchOperation = "__event_wait_source_watch";
    public const string SourceUnwatchOperation = "__event_wait_source_unwatch";
    public const string SourceAckOperation = "__event_wait_source_ack";
    public const string SourceSignalOperation = "__event_wait_source_signal";
    public const string SourceBindOperation = "__event_wait_source_bind";
    public const string ClaimDueOperation = "__event_wait_claim_due";
    public const string SubscriptionsKey = "eventWaitSubscriptions";

    internal static RelayReply HandleSource(AggregateContext context, string sourceKind)
    {
        if (!context.Caller.IsInternal) return Error("InternalOnly");
        EventWaitSourceWatch? watch;
        try { watch = context.Args.Deserialize<EventWaitSourceWatch>(EventWaitState.Json); }
        catch (JsonException) { return Error("MalformedEffect"); }
        if (!ValidSource(watch) || watch!.SourceKind != sourceKind || watch.SourceKey != context.Key) return Error("InvalidArguments");
        if (watch.RepositoryId != context.Repository) return Error("RepositoryDenied");
        if (watch.ExpiresAt - context.Now > TimeSpan.FromHours(1)) return Error("InvalidArguments");
        if (watch.ExpiresAt <= context.Now) return Ok(new { watching = false, expired = true });
        var burial = "event-source\u001f" + context.Repository + "\u001f" + context.Key + "\u001f" + SourceKey(watch);
        if (EventWaitRetirement.Contains(context.State, burial, context.Now)) return Ok(new { watching = false, retired = true });
        var watches = SourceMap(context, sourceKind);
        var key = SourceKey(watch);
        var cancel = context.Operation == SourceUnwatchOperation;
        if (context.Operation is not (SourceWatchOperation or SourceUnwatchOperation)) return Error("InvalidArguments");
        foreach (var old in watches.Where(pair => ReadSource(pair.Value!.AsObject()).ExpiresAt <= context.Now).Select(pair => pair.Key).ToArray()) watches.Remove(old);
        var entry = watches[key] as JsonObject;
        if (entry is not null && ReadSource(entry) != watch) return Error("RequestConflict");
        if (entry is null)
        {
            if (watches.Count >= 128) RetireSourceSlots(context, watches, "event-source", item => SourceKey(ReadSource(item)));
            if (watches.Count >= 128) return Error("CapacityExceeded");
            entry = new JsonObject { ["watch"] = JsonSerializer.SerializeToNode(watch, EventWaitState.Json), ["status"] = cancel ? "canceled" : "active" };
            watches[key] = entry;
        }
        if (cancel) { entry["status"] = "canceled"; return Ok(new { watching = false }); }
        if (entry["ack"] is JsonObject replay)
        {
            context.Emit(AggregateKinds.Session, watch.OwnerSessionId, SourceAckOperation, replay.DeepClone().AsObject());
            return Ok(new { watching = entry["status"]!.GetValue<string>() == "active" });
        }
        long baseline;
        bool already;
        bool armed = entry["status"]!.GetValue<string>() == "active" && watch.ExpiresAt > context.Now;
        string? outcome = null;
        if (sourceKind == AggregateKinds.Board)
        {
            var task = new BoardState(context.State).Task(watch.TaskId!);
            baseline = task?.ChangeRevision ?? 0;
            already = task?.Workflow?.State == "completed";
            if (task is null) { armed = false; outcome = "target_gone"; }
        }
        else
        {
            var state = new LockState(context.State);
            baseline = state.Generation;
            var exact = watch.Revision == baseline.ToString(System.Globalization.CultureInfo.InvariantCulture);
            already = !exact || state.Holder.Length == 0 || !state.Live(context.Now) && state.BlockerCount == 0;
            armed &= exact && !already;
        }
        if (!armed && entry["status"]!.GetValue<string>() == "active") entry["status"] = "inactive";
        var ack = new JsonObject { ["repositoryId"] = context.Repository, ["watch"] = JsonSerializer.SerializeToNode(watch, EventWaitState.Json),
            ["baselineRevision"] = baseline, ["alreadySatisfied"] = already, ["armed"] = armed, ["outcome"] = outcome };
        entry["ack"] = ack.DeepClone();
        context.Emit(AggregateKinds.Session, watch.OwnerSessionId, SourceAckOperation, ack);
        return Ok(new { watching = armed });
    }

    internal static void TaskCompleted(AggregateContext context, BoardTask task)
    {
        if (context.State["eventWaitSubscriptions"] is null) return;
        foreach (var entry in SourceMap(context, AggregateKinds.Board).Select(pair => pair.Value!.AsObject()))
        {
            var watch = ReadSource(entry);
            if (watch.TaskId != task.TaskId || !SourceActive(entry, watch, context.Now) || task.Workflow?.State != "completed"
                || task.ChangeRevision <= entry["ack"]!["baselineRevision"]!.GetValue<long>()) continue;
            SignalSource(context, entry, watch, task.ChangeRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    internal static void ClaimReleased(AggregateContext context)
    {
        if (context.State["eventWaitSubscriptions"] is null) return;
        var generation = new LockState(context.State).Generation.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var entry in SourceMap(context, AggregateKinds.Lock).Select(pair => pair.Value!.AsObject()))
        {
            var watch = ReadSource(entry);
            if (watch.Revision == generation && SourceActive(entry, watch, context.Now)) SignalSource(context, entry, watch, generation);
        }
    }

    internal static RelayReply ClaimExpired(AggregateContext context)
    {
        if (!context.Caller.IsInternal) return Error("InternalOnly");
        OnClaimExpiry(context);
        return Ok(new { checkedClaim = true });
    }

    internal static void OnClaimExpiry(AggregateContext context)
    {
        var state = new LockState(context.State);
        // A lease lapse is available only without an unresolved action. Never
        // release or recover anything on a watcher's behalf.
        if (state.Holder.Length > 0 && !state.Live(context.Now) && state.BlockerCount == 0) ClaimReleased(context);
    }

    private static void RetireSourceSlots(AggregateContext context, JsonObject map, string prefix, Func<JsonObject, string> identity)
    {
        var retired = map.Where(pair => pair.Value is JsonObject item && item["status"]?.GetValue<string>() is "notified" or "canceled" or "inactive")
            .Take(Math.Max(0, map.Count - 96)).ToArray();
        var tombstones = new List<(string Key, DateTimeOffset At)>();
        foreach (var pair in retired)
        {
            tombstones.Add((prefix + "\u001f" + context.Repository + "\u001f" + context.Key + "\u001f" + identity(pair.Value!.AsObject()), context.Now));
        }
        if (!EventWaitRetirement.TryRemember(context.State, tombstones, context.Now)) return;
        foreach (var pair in retired) map.Remove(pair.Key);
    }

    private static bool SourceActive(JsonObject entry, EventWaitSourceWatch watch, DateTimeOffset now) =>
        entry["status"]?.GetValue<string>() == "active" && watch.ExpiresAt > now && entry["ack"]?["armed"]?.GetValue<bool>() == true;

    private static void SignalSource(AggregateContext context, JsonObject entry, EventWaitSourceWatch watch, string revision)
    {
        context.Emit(AggregateKinds.Session, watch.OwnerSessionId, SourceSignalOperation, new JsonObject
        { ["repositoryId"] = context.Repository, ["watch"] = JsonSerializer.SerializeToNode(watch, EventWaitState.Json), ["revision"] = revision });
        entry["status"] = "notified";
    }

    private static JsonObject SourceMap(AggregateContext context, string kind)
    {
        var map = context.State["eventWaitSubscriptions"] switch
        {
            null => (JsonObject)(context.State["eventWaitSubscriptions"] = new JsonObject()),
            JsonObject existing => existing,
            _ => throw new InvalidOperationException("Invalid source subscriptions")
        };
        if (map.Count > 128) throw new InvalidOperationException("Oversized source subscriptions");
        foreach (var pair in map)
        {
            if (pair.Value is not JsonObject entry) throw new InvalidOperationException("Invalid subscription");
            var watch = ReadSource(entry);
            if (!ValidSource(watch) || watch.RepositoryId != context.Repository || watch.SourceKind != kind || watch.SourceKey != context.Key
                || pair.Key != SourceKey(watch) || entry["status"]?.GetValue<string>() is not ("active" or "notified" or "inactive" or "canceled"))
                throw new InvalidOperationException("Invalid subscription binding");
            if (entry["ack"] is JsonObject ack)
            {
                try
                {
                    if (ack["watch"]?.Deserialize<EventWaitSourceWatch>(EventWaitState.Json) != watch || ack["repositoryId"]?.GetValue<string>() != context.Repository
                        || ack["baselineRevision"]?.GetValue<long>() is not >= 0 || ack["armed"] is null || ack["alreadySatisfied"] is null
                        || ack["outcome"]?.GetValue<string>() is not (null or "target_gone")) throw new InvalidOperationException("Invalid captured source acknowledgment");
                    var armed = ack["armed"]!.GetValue<bool>();
                    _ = ack["alreadySatisfied"]!.GetValue<bool>();
                    if (entry["status"]!.GetValue<string>() is "active" or "notified" && !armed) throw new InvalidOperationException("Invalid armed subscription");
                }
                catch (JsonException error) { throw new InvalidOperationException("Invalid source acknowledgment", error); }
            }
            else if (entry["status"]!.GetValue<string>() != "canceled") throw new InvalidOperationException("Missing captured source acknowledgment");
        }
        return map;
    }

    private static EventWaitSourceWatch ReadSource(JsonObject entry)
    {
        try { return entry["watch"]?.Deserialize<EventWaitSourceWatch>(EventWaitState.Json) ?? throw new InvalidOperationException("Missing subscription"); }
        catch (JsonException error) { throw new InvalidOperationException("Invalid subscription", error); }
    }
    private static string SourceKey(EventWaitSourceWatch watch) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { watch.OwnerSessionId, watch.WaitId, watch.WatchId }))));
    private static bool ValidSource(EventWaitSourceWatch? watch)
    {
        if (watch is null || !new[] { watch.RepositoryId, watch.OwnerSessionId, watch.WaitId, watch.WatchId, watch.SourceKey }
            .All(value => value is { Length: > 0 and <= 256 } && !value.Any(char.IsControl) && !value.Any(char.IsSurrogate))) return false;
        return watch.Kind switch
        {
            "task_completed" => watch.SourceKind == AggregateKinds.Board && watch.TaskId is { Length: > 0 and <= 128 }
                && !watch.TaskId.Any(char.IsControl) && !watch.TaskId.Any(char.IsSurrogate) && watch.Revision is null && watch.QualifyingData == "completed",
            "claim_released" => watch.SourceKind == AggregateKinds.Lock && watch.TaskId is null && watch.QualifyingData is null
                && long.TryParse(watch.Revision, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var generation)
                && generation > 0 && generation.ToString(System.Globalization.CultureInfo.InvariantCulture) == watch.Revision,
            _ => false
        };
    }
    public const string WatchOperation = "__event_wait_owner_watch";
    public const string UnwatchOperation = "__event_wait_owner_unwatch";
    public const string LifecycleOperation = "__event_wait_lifecycle";
    private const string ClosureOutcomeKey = "eventWaitOwnerClosureOutcome";

    public static RelayReply Apply(StateEnvelope<CoordinationDocument> envelope, RelayCaller caller,
        string sessionKey, string operation, JsonObject args, DateTimeOffset now,
        EventWaitOwnerWatch? watch = null, EventWaitLifecycle? lifecycle = null)
    {
        var state = JsonNode.Parse(envelope.Value.Json)!.AsObject();
        var context = new AggregateContext(state, caller, operation, args, now) { Key = sessionKey };
        if (!caller.MayAccess(context.Repository) || state["repositoryId"]?.GetValue<string>() != context.Repository) return Error("RepositoryDenied");
        RelayReply result;
        try
        {
            result = operation switch
            {
                "bind" when watch is not null => Bind(context, watch),
                "watch" when watch is not null => Watch(context, watch, cancel: false),
                "unwatch" when watch is not null => Watch(context, watch, cancel: true),
                "close" => Closed(context, "target_gone"),
                "stranded" => Closed(context, "stranded"),
                "lifecycle" when lifecycle is not null => Deliver(context, lifecycle),
                _ => Error("InvalidArguments")
            };
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or JsonException)
        { return Error("InvalidArguments"); }
        if (!result.Ok) return result;
        if (envelope.Effects.Count + context.Effects.Count > RelayLimits.MaxRecords) return Error("CapacityExceeded");
        envelope.Value = new() { Json = state.ToJsonString() };
        CoordinationStateMachine.StampEffects(context.Effects, 0, caller);
        envelope.Effects.AddRange(context.Effects);
        return result;
    }

    // These messages use the normal primary-session transaction and outbox. They
    // remain private: the state machine rejects external __ operations first.
    internal static RelayReply Handle(AggregateContext context)
    {
        if (!context.Caller.IsInternal) return Error("InternalOnly");
        try
        {
            return context.Operation switch
            {
                SourceBindOperation => BindSource(context, context.Args.Deserialize<EventWaitSourceWatch>(EventWaitState.Json) ?? throw new JsonException()),
                SourceAckOperation or SourceSignalOperation => AcceptSource(context),
                WatchOperation => Watch(context, context.Args.Deserialize<EventWaitOwnerWatch>(EventWaitState.Json)
                    ?? throw new JsonException(), cancel: false),
                UnwatchOperation => Watch(context, context.Args.Deserialize<EventWaitOwnerWatch>(EventWaitState.Json)
                    ?? throw new JsonException(), cancel: true),
                LifecycleOperation => Deliver(context, context.Args.Deserialize<EventWaitLifecycle>(EventWaitState.Json)
                    ?? throw new JsonException()),
                _ => Error("InvalidArguments")
            };
        }
        catch (JsonException) { return Error("MalformedEffect"); }
    }

    internal static RelayReply OnClosed(AggregateContext context, bool stranded) =>
        Closed(context, stranded ? "stranded" : "target_gone");

    internal static void CloseWaiter(AggregateContext context)
    {
        if (new EventWaitState(context.State).Entries is not { } entries) return;
        foreach (var entry in entries.Select(x => x.Value).OfType<JsonObject>())
            if (entry["terminal"] is null)
                EndWatch(context, entry, EventWaitState.Request(entry));
    }

    private static RelayReply BindSource(AggregateContext context, EventWaitSourceWatch watch)
    {
        if (EventWaitAggregate.Authorize(context) is { } denied) return denied;
        if (!ValidSource(watch) || watch.OwnerSessionId != context.Key || watch.RepositoryId != context.Repository) return Error("InvalidArguments");
        if (new EventWaitState(context.State).Entries?[watch.WaitId] is not JsonObject entry) return Error("WaitUnknown");
        var request = EventWaitState.Request(entry);
        var target = watch.SourceKind == AggregateKinds.Board ? watch.SourceKey + "/" + watch.TaskId : watch.SourceKey;
        if (request.Kind != watch.Kind || request.Target != target || request.ExpiresAt != watch.ExpiresAt
            || request.QualifyingData != watch.QualifyingData || watch.Kind == "claim_released" && request.Revision != watch.Revision) return Error("SubscriptionMismatch");
        if (entry["terminal"] is not null) return Ok(new { watching = false });
        if (request.ExpiresAt <= context.Now)
        {
            EventWaitAggregate.Terminal(context, entry, request, "expired", request.Revision);
            return Ok(new { watching = false });
        }
        if (entry["sourceWatch"] is not null)
            return entry["sourceWatch"]!.Deserialize<EventWaitSourceWatch>(EventWaitState.Json) == watch ? Ok(new { watching = true }) : Error("RequestConflict");
        entry["sourceWatch"] = JsonSerializer.SerializeToNode(watch, EventWaitState.Json);
        context.Emit(watch.SourceKind, watch.SourceKey, SourceWatchOperation, JsonSerializer.SerializeToNode(watch, EventWaitState.Json)!.AsObject());
        return Ok(new { watching = true, pending = true });
    }

    private static RelayReply AcceptSource(AggregateContext context)
    {
        var watch = context.Args["watch"]?.Deserialize<EventWaitSourceWatch>(EventWaitState.Json);
        if (!ValidSource(watch) || watch!.RepositoryId != context.Repository || watch.OwnerSessionId != context.Key) return Error("InvalidArguments");
        if (new SessionState(context.State).Closed) return Ok(new { handled = false, closed = true });
        if (new EventWaitState(context.State).Entries?[watch.WaitId] is not JsonObject entry) return Ok(new { handled = false, unknown = true });
        if (entry["sourceWatch"]?.Deserialize<EventWaitSourceWatch>(EventWaitState.Json) != watch) return Error("SubscriptionMismatch");
        if (entry["terminal"] is not null) return Ok(new { handled = true });
        var request = EventWaitState.Request(entry);
        if (request.ExpiresAt <= context.Now)
        {
            EventWaitAggregate.Terminal(context, entry, request, "expired", request.Revision);
            return Ok(new { handled = true });
        }
        if (context.Operation == SourceAckOperation)
        {
            var baseline = context.Args["baselineRevision"]?.GetValue<long>() ?? -1;
            if (baseline < 0 || context.Args["armed"] is null || context.Args["alreadySatisfied"] is null) return Error("InvalidArguments");
            var armed = context.Args["armed"]!.GetValue<bool>();
            if (armed && watch.Kind == "claim_released" && watch.Revision != baseline.ToString(System.Globalization.CultureInfo.InvariantCulture)) return Error("SubscriptionMismatch");
            var outcome = context.Args["outcome"]?.GetValue<string>();
            if (outcome is not (null or "target_gone")) return Error("InvalidArguments");
            var ack = new JsonObject { ["baselineRevision"] = baseline, ["armed"] = armed,
                ["alreadySatisfied"] = context.Args["alreadySatisfied"]!.GetValue<bool>(), ["outcome"] = outcome };
            if (entry["sourceAck"] is not null && !JsonNode.DeepEquals(entry["sourceAck"], ack)) return Error("SubscriptionMismatch");
            entry["sourceAck"] = ack;
            if (outcome == "target_gone") EventWaitAggregate.Terminal(context, entry, request, outcome, request.Revision);
            else if (entry["pendingSourceRevision"] is JsonValue pending) CompleteSource(context, entry, request, watch, pending.GetValue<string>());
            entry.Remove("pendingSourceRevision");
        }
        else
        {
            var revision = context.Args["revision"]?.GetValue<string>() ?? "";
            if (!CanonicalRevision(revision, out _)) return Error("InvalidArguments");
            if (entry["sourceAck"] is null)
            {
                if (entry["pendingSourceRevision"] is JsonValue prior && prior.GetValue<string>() != revision) return Error("RequestConflict");
                entry["pendingSourceRevision"] = revision;
            }
            else CompleteSource(context, entry, request, watch, revision);
        }
        return Ok(new { handled = true });
    }

    private static void CompleteSource(AggregateContext context, JsonObject entry, EventWaitRequest request, EventWaitSourceWatch watch, string revision)
    {
        if (entry["sourceAck"] is not JsonObject ack || ack["armed"]?.GetValue<bool>() != true || !CanonicalRevision(revision, out var actual)) return;
        var baseline = ack["baselineRevision"]!.GetValue<long>();
        if (watch.Kind == "task_completed" ? actual <= baseline : revision != watch.Revision || actual != baseline) return;
        EventWaitAggregate.Terminal(context, entry, request, "satisfied", revision);
    }

    private static bool CanonicalRevision(string revision, out long value) =>
        long.TryParse(revision, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value)
        && value > 0 && value.ToString(System.Globalization.CultureInfo.InvariantCulture) == revision;

    private static void EndSourceWatch(AggregateContext context, JsonObject entry, EventWaitRequest request)
    {
        if (entry["sourceWatch"] is null) return;
        var watch = entry["sourceWatch"]!.Deserialize<EventWaitSourceWatch>(EventWaitState.Json);
        if (!ValidSource(watch) || watch!.RepositoryId != context.Repository || watch.OwnerSessionId != context.Key
            || watch.WaitId != request.WaitId || watch.ExpiresAt != request.ExpiresAt) throw new InvalidOperationException("Invalid source binding");
        context.Emit(watch.SourceKind, watch.SourceKey, SourceUnwatchOperation, JsonSerializer.SerializeToNode(watch, EventWaitState.Json)!.AsObject());
    }

    private static RelayReply Bind(AggregateContext context, EventWaitOwnerWatch watch)
    {
        if (EventWaitAggregate.Authorize(context) is { } denied) return denied;
        if (!Valid(watch) || watch.RepositoryId != context.Repository || watch.OwnerSessionId != context.Key) return Error("InvalidArguments");
        if (new EventWaitState(context.State).Entries?[watch.WaitId] is not JsonObject entry) return Error("WaitUnknown");
        var request = EventWaitState.Request(entry);
        if (request.ExpiresAt != watch.ExpiresAt) return Error("RequestConflict");
        if (entry["terminal"] is not null) return Ok(new { watching = false });
        if (request.ExpiresAt <= context.Now)
        {
            EventWaitAggregate.Terminal(context, entry, request, "expired", request.Revision);
            return Ok(new { watching = false });
        }
        if (entry["ownerWatch"] is not null)
            return entry["ownerWatch"]!.Deserialize<EventWaitOwnerWatch>(EventWaitState.Json) == watch ? Ok(new { watching = true }) : Error("RequestConflict");
        entry["ownerWatch"] = JsonSerializer.SerializeToNode(watch, EventWaitState.Json);
        context.Emit(AggregateKinds.Session, watch.SourceSessionId, WatchOperation, JsonSerializer.SerializeToNode(watch, EventWaitState.Json)!.AsObject());
        return Ok(new { watching = true });
    }

    private static RelayReply Watch(AggregateContext context, EventWaitOwnerWatch watch, bool cancel)
    {
        if (!context.Caller.IsInternal) return Error("InternalOnly");
        if (!Valid(watch) || watch.SourceSessionId != context.Key) return Error("InvalidArguments");
        if (watch.RepositoryId != context.Repository) return Error("RepositoryDenied");
        if (watch.ExpiresAt <= context.Now) return Ok(new { watching = false, expired = true });
        if (watch.ExpiresAt - context.Now > TimeSpan.FromHours(1)) return Error("InvalidArguments");
        var burial = "event-owner\u001f" + context.Repository + "\u001f" + context.Key + "\u001f" + Key(watch);
        if (EventWaitRetirement.Contains(context.State, burial, context.Now)) return Ok(new { watching = false, retired = true });
        var map = Map(context);
        foreach (var key in map.Where(x => Read(x.Value!.AsObject()).ExpiresAt <= context.Now).Select(x => x.Key).ToArray()) map.Remove(key);
        var id = Key(watch);
        if (map[id] is JsonObject prior)
        {
            if (Read(prior) != watch) return Error("RequestConflict");
            if (cancel) prior["status"] = "canceled";
            else if (prior["status"]?.GetValue<string>() == "active" && new SessionState(context.State).Closed)
            {
                Emit(context, watch, ClosureOutcome(context.State) ?? "target_gone");
                prior["status"] = "notified";
            }
            return Ok(new { watching = !cancel && prior["status"]?.GetValue<string>() == "active" });
        }
        if (new SessionState(context.State).Closed)
        {
            // Closed sources need no active slot, but they still record a
            // durable replay fence before emitting. Cancellation can overtake
            // this first install too; never resurrect it or emit twice.
            if (!EventWaitRetirement.TryRemember(context.State, [(burial, context.Now)], context.Now)) return Error("CapacityExceeded");
            if (!cancel) Emit(context, watch, ClosureOutcome(context.State) ?? "target_gone");
            return Ok(new { watching = false, closed = true });
        }
        if (map.Count >= 128) RetireSourceSlots(context, map, "event-owner", item => Key(Read(item)));
        if (map.Count >= 128) return Error("CapacityExceeded");
        var entry = new JsonObject { ["watch"] = JsonSerializer.SerializeToNode(watch, EventWaitState.Json), ["status"] = cancel ? "canceled" : "active" };
        map[id] = entry;
        if (!cancel && new SessionState(context.State).Closed)
        {
            Emit(context, watch, ClosureOutcome(context.State) ?? "target_gone"); entry["status"] = "notified";
        }
        return Ok(new { watching = !cancel });
    }

    private static RelayReply Closed(AggregateContext context, string outcome)
    {
        if (!context.Caller.IsInternal) return Error("InternalOnly");
        if (!new SessionState(context.State).Closed) return Error("OwnerNotClosed");
        var recorded = ClosureOutcome(context.State) == "stranded" || outcome == "stranded" ? "stranded" : "target_gone";
        context.State[ClosureOutcomeKey] = recorded;
        var map = Map(context);
        foreach (var entry in map.Select(x => x.Value).OfType<JsonObject>())
        {
            var watch = Read(entry);
            if (watch.SourceSessionId != context.Key || watch.RepositoryId != context.Repository) return Error("InvalidArguments");
            if (entry["status"]?.GetValue<string>() != "active" || watch.ExpiresAt <= context.Now) continue;
            Emit(context, watch, recorded); entry["status"] = "notified";
        }
        return Ok(new { notified = true });
    }

    private static RelayReply Deliver(AggregateContext context, EventWaitLifecycle lifecycle)
    {
        if (!context.Caller.IsInternal) return Error("InternalOnly");
        var watch = lifecycle.Watch;
        if (!Valid(watch) || watch.RepositoryId != context.Repository || watch.OwnerSessionId != context.Key || lifecycle.Outcome is not ("target_gone" or "stranded")) return Error("InvalidArguments");
        // A close and its unwatch can overtake this delivery. Consume the stale
        // message instead of retrying forever or recreating the closed wait map.
        if (new SessionState(context.State).Closed) return Ok(new { handled = false, closed = true });
        if (new EventWaitState(context.State).Entries?[watch.WaitId] is not JsonObject entry) return Ok(new { handled = false, unknown = true });
        if (entry["ownerWatch"]?.Deserialize<EventWaitOwnerWatch>(EventWaitState.Json) != watch) return Error("SubscriptionMismatch");
        if (entry["terminal"] is null)
        {
            var request = EventWaitState.Request(entry);
            // Failure/stranding is a negative terminal outcome, not a completed
            // workflow event. It must not hang behind a newer-completion test.
            EventWaitAggregate.Terminal(context, entry, request,
                request.ExpiresAt <= context.Now ? "expired" : lifecycle.Outcome, request.Revision);
        }
        return Ok(new { handled = true });
    }

    private static void Emit(AggregateContext context, EventWaitOwnerWatch watch, string outcome)
    {
        var payload = JsonSerializer.SerializeToNode(new EventWaitLifecycle(watch, outcome), EventWaitState.Json)!.AsObject();
        payload["repositoryId"] = context.Repository;
        context.Emit(AggregateKinds.Session, watch.OwnerSessionId, LifecycleOperation, payload);
    }
    internal static void EndWatch(AggregateContext context, JsonObject entry, EventWaitRequest request)
    {
        EndSourceWatch(context, entry, request);
        if (entry["ownerWatch"] is null) return;
        var watch = entry["ownerWatch"]!.Deserialize<EventWaitOwnerWatch>(EventWaitState.Json);
        if (watch is null || !Valid(watch) || watch.OwnerSessionId != context.Key || watch.RepositoryId != context.Repository ||
            watch.WaitId != request.WaitId || watch.ExpiresAt != request.ExpiresAt) throw new InvalidOperationException("Invalid owner watch binding");
        context.Emit(AggregateKinds.Session, watch.SourceSessionId, UnwatchOperation,
            JsonSerializer.SerializeToNode(watch, EventWaitState.Json)!.AsObject());
    }

    private static string? ClosureOutcome(JsonObject state)
    {
        var outcome = state[ClosureOutcomeKey]?.GetValue<string>();
        return outcome is null or "target_gone" or "stranded" ? outcome : throw new InvalidOperationException("Invalid closure outcome");
    }

    private static JsonObject Map(AggregateContext context)
    {
        var state = context.State;
        var map = state["eventWaitOwnerWatches"] switch
        {
            null => (JsonObject)(state["eventWaitOwnerWatches"] = new JsonObject()),
            JsonObject existing => existing,
            _ => throw new InvalidOperationException("Invalid source watch state")
        };
        if (map.Count > 128) throw new InvalidOperationException("Oversized source watch state");
        foreach (var pair in map)
        {
            if (pair.Value is not JsonObject entry) throw new InvalidOperationException("Invalid source watch record");
            var watch = Read(entry);
            if (!Valid(watch) || watch.RepositoryId != context.Repository || watch.SourceSessionId != context.Key || pair.Key != Key(watch) ||
                entry["status"]?.GetValue<string>() is not ("active" or "canceled" or "notified")) throw new InvalidOperationException("Invalid source watch record");
        }
        return map;
    }
    private static EventWaitOwnerWatch Read(JsonObject entry) => entry["watch"]?.Deserialize<EventWaitOwnerWatch>(EventWaitState.Json) ?? throw new InvalidOperationException("Invalid source watch record");
    private static string Key(EventWaitOwnerWatch watch) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { watch.OwnerSessionId, watch.WaitId, watch.WatchId }))));
    private static bool Valid(EventWaitOwnerWatch watch) => watch is not null &&
        new[] { watch.RepositoryId, watch.OwnerSessionId, watch.WaitId, watch.WatchId, watch.SourceSessionId }.All(x => x is { Length: > 0 and <= 128 } && !x.Any(char.IsControl) && !x.Any(char.IsSurrogate));
    private static RelayReply Ok(object data) => new(true, "Ok", JsonSerializer.Serialize(data), 0);
    private static RelayReply Error(string code) => new(false, code, "{}", 0);
}
