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
        var map = Map(context);
        foreach (var key in map.Where(x => Read(x.Value!.AsObject()).ExpiresAt <= context.Now).Select(x => x.Key).ToArray()) map.Remove(key);
        var id = Key(watch);
        if (map[id] is JsonObject prior)
        {
            if (Read(prior) != watch) return Error("RequestConflict");
            if (cancel) prior["status"] = "canceled";
            return Ok(new { watching = !cancel && prior["status"]?.GetValue<string>() == "active" });
        }
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
        if (new EventWaitState(context.State).Entries?[watch.WaitId] is not JsonObject entry) return Error("WaitUnknown");
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
