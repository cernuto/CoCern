using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
using PkRelay.Contracts;
using PkRelay.Domain;

namespace PkRelay.Core;

public interface IRelayAggregateGrain : IGrainWithStringKey
{
    Task<RelayReply> ExecuteAsync(RelayCaller caller, string operation, string argumentsJson);
}

public interface ISessionGrain : IRelayAggregateGrain;
public interface ITaskBoardGrain : IRelayAggregateGrain;
public interface ILockGrain : IRelayAggregateGrain;
public interface IControlGrain : IRelayAggregateGrain;

/// <summary>
/// Orleans adapter for one aggregate. It owns activation, persistence and outbox delivery; every rule is applied by
/// <see cref="CoordinationStateMachine"/> against a working copy that replaces the persisted state only after a successful write.
/// </summary>
public abstract class CoordinationGrain(IPersistentState<StateEnvelope<CoordinationDocument>> persistent, IGrainFactory grains, IServiceProvider services) : Grain, IRelayAggregateGrain
{
    private static readonly TimeSpan RedeliveryDelay = TimeSpan.FromSeconds(5);
    protected abstract string Kind { get; }
    private bool uncertainCommit;

    public async Task<RelayReply> ExecuteAsync(RelayCaller caller, string operation, string argumentsJson)
    {
        JsonObject args;
        if (uncertainCommit) return Replies.Error(RelayErrorCodes.StorageFailure);
        try { args = JsonNode.Parse(argumentsJson)?.AsObject() ?? new(); }
        catch (JsonException) { return Replies.Error(RelayErrorCodes.InvalidArguments); }
        if (!caller.IsInternal) return Replies.Error(RelayErrorCodes.InternalOnly);
        if (operation == Operations.Dispatch) return await DispatchAsync(args);
        var candidate = Clone(persistent.State);
        var firstNewEffect = candidate.Effects.Count;
        var priorTaskRevision = Kind == AggregateKinds.Board ? CoordinationStateMachine.TaskRevision(candidate) : null;
        var reply = CoordinationStateMachine.Apply(candidate, Kind, this.GetPrimaryKeyString(), caller, operation, args, DateTimeOffset.UtcNow);
        if (!reply.Ok || !CoordinationStateMachine.RequiresCommit(Kind, operation, priorTaskRevision, candidate)) return reply;
        CoordinationStateMachine.StampEffects(candidate.Effects, firstNewEffect, caller);
        CoordinationStateMachine.ScheduleLeases(candidate);
        return await CommitAsync(candidate) ? reply : Replies.Error(RelayErrorCodes.StorageFailure);
    }

    /// <summary>Delivers due outbox effects (optionally one work item, operation or action) and removes each only after its target accepted it.</summary>
    private async Task<RelayReply> DispatchAsync(JsonObject args)
    {
        var workId = JsonFields.Text(args, "workId");
        var operation = JsonFields.Text(args, "operation");
        var actionKey = JsonFields.Text(args, "actionKey");
        bool Selected(DurableEffect effect) =>
            (workId.Length == 0 || effect.Id == workId) && (operation.Length == 0 || effect.Operation == operation)
            && (actionKey.Length == 0 || JsonNode.Parse(effect.PayloadJson)?["actionKey"]?.ToString() == actionKey);
        var now = DateTimeOffset.UtcNow;
        var candidate = Clone(persistent.State);
        var attention = services.GetService<AttentionOptions>();
        if (Kind == AggregateKinds.Lock && operation.Length == 0 && actionKey.Length == 0 && candidate.DueWork.Any(x => x.Kind == DueWorkKinds.LeaseWarning && x.DueAt <= now && (workId.Length == 0 || x.Id == workId)))
        {
            // The lease check commits its claim_expiring raise in the lock's own outbox; the pump delivers it next.
            if (!await CheckLeaseAsync(candidate, now)) return Replies.Error(RelayErrorCodes.StorageFailure);
            candidate = Clone(persistent.State);
        }
        foreach (var effect in candidate.Effects.Where(x => Selected(x) && x.DueAt <= now).ToArray())
        {
            var payload = JsonNode.Parse(effect.PayloadJson)!.AsObject();
            var effectCaller = CoordinationStateMachine.TakeEffectCaller(payload);
            // With attention turned off, items are neither stored nor routed: the effect is dropped.
            if (Operations.IsAttention(effect.Operation) && attention is { Enabled: false })
            {
                candidate.Effects.RemoveAll(x => x.Id == effect.Id);
                if (!await CommitAsync(candidate)) return Replies.Error(RelayErrorCodes.StorageFailure);
                candidate = Clone(persistent.State);
                continue;
            }
            // A raised item takes the hub's configured lifetime unless its source set one.
            if (effect.Operation == Operations.AttentionRaise && !payload.ContainsKey("expiryDays") && !payload.ContainsKey("expiresAt") && attention is not null)
                payload["expiryDays"] = attention.ItemExpiryDays;
            RelayReply result;
            try
            {
                if (Kind == AggregateKinds.Session && effect.TargetKind == AggregateKinds.Session && (effect.Operation == Operations.Deliver || Operations.IsAttention(effect.Operation)) && GrainContext is not null && effect.TargetKey == this.GetPrimaryKeyString())
                {
                    // A non-reentrant grain cannot await an RPC to itself. Commit its inbox entry
                    // and outbox removal together so a stale dispatch snapshot cannot erase delivery.
                    result = CoordinationStateMachine.Apply(candidate, Kind, effect.TargetKey, effectCaller, effect.Operation, payload, DateTimeOffset.UtcNow);
                }
                else result = await Aggregate(grains, effect.TargetKind, effect.TargetKey).ExecuteAsync(effectCaller, effect.Operation, payload.ToJsonString());
            }
            catch (Exception) { result = Replies.Error(RelayErrorCodes.DeliveryUnavailable); }
            // Attention is advisory. A session that refuses an item (unknown session, another repository, a malformed item) would refuse
            // it on every retry, so the effect is dropped rather than retried every few seconds forever. Infrastructure failures still retry.
            if (!result.Ok && !(Operations.IsAttention(effect.Operation) && !IsTransient(result.Code)))
            {
                candidate.Effects[candidate.Effects.FindIndex(x => x.Id == effect.Id)] = effect with { DueAt = DateTimeOffset.UtcNow.Add(RedeliveryDelay) };
                if (!await CommitAsync(candidate)) return Replies.Error(RelayErrorCodes.StorageFailure);
                return Replies.Error(RelayErrorCodes.DeliveryPending, new { effectId = effect.Id, reason = result.Code });
            }
            candidate.Effects.RemoveAll(x => x.Id == effect.Id);
            if (!await CommitAsync(candidate)) return Replies.Error(RelayErrorCodes.StorageFailure);
            if (effect.TargetKind == AggregateKinds.Session && effect.Operation == Operations.Deliver) RecordWake(effect.TargetKey, payload, result);
            // Only a new top-tier attention item may wake its chat, so only that one counts as a wake and sends a hint.
            var urgent = effect.TargetKind == AggregateKinds.Session && effect.Operation == Operations.AttentionRaise && result.Ok && UrgentRaise(payload, result);
            if (urgent) RecordWake(effect.TargetKey, payload, Replies.Ok(new { delivered = true }));
            if (effect.TargetKind == AggregateKinds.Session && (effect.Operation == Operations.Deliver || urgent) && services.GetService<ICoordinationNotifier>() is { } notifier)
            {
                // Hints are best effort; the durable inbox remains authoritative.
                try { await notifier.NotifyAsync(effect.TargetKey, urgent ? "attention:" + JsonFields.Text(JsonNode.Parse(result.DataJson)!.AsObject(), "itemId") : JsonFields.Text(payload, "messageId")); }
                catch (Exception) { }
            }
            candidate = Clone(persistent.State);
        }
        if (candidate.DueWork.RemoveAll(x => (workId.Length == 0 || x.Id == workId) && x.DueAt <= now) > 0 && !await CommitAsync(candidate)) return Replies.Error(RelayErrorCodes.StorageFailure);
        if (candidate.Effects.Any(Selected)) return Replies.Error(RelayErrorCodes.DeliveryPending);
        return Replies.Ok(new { dispatched = true });
    }

    /// <summary>Applies a lock's due lease check and commits what it raised, with the lock's repository as the effects' authority.</summary>
    private async Task<bool> CheckLeaseAsync(StateEnvelope<CoordinationDocument> candidate, DateTimeOffset now)
    {
        var repository = JsonFields.Text(JsonNode.Parse(candidate.Value.Json)!.AsObject(), "repositoryId");
        var first = candidate.Effects.Count;
        var checker = new RelayCaller("relay-dispatcher", false, [repository], true);
        CoordinationStateMachine.Apply(candidate, Kind, this.GetPrimaryKeyString(), checker, Operations.LeaseCheck, new JsonObject { ["repositoryId"] = repository }, now);
        CoordinationStateMachine.StampEffects(candidate.Effects, first, checker);
        candidate.DueWork.RemoveAll(x => x.Kind == DueWorkKinds.LeaseWarning && x.DueAt <= now);
        CoordinationStateMachine.ScheduleLeases(candidate);
        return await CommitAsync(candidate);
    }

    /// <summary>Whether a delivered raise stored a new item whose tier may wake a chat.</summary>
    private static bool UrgentRaise(JsonObject payload, RelayReply result)
    {
        var data = JsonNode.Parse(result.DataJson) as JsonObject;
        if (data?["raised"] is not JsonValue raised || !raised.TryGetValue<bool>(out var isRaised) || !isRaised || data["refreshed"] is not JsonValue refreshed || !refreshed.TryGetValue<bool>(out var isRefresh) || isRefresh) return false;
        var blocking = payload["blocking"] is JsonValue flag && flag.TryGetValue<bool>(out var value) && value;
        return AttentionWeights.Wakes(AttentionWeights.Tier(JsonFields.Text(payload, "kind"), JsonFields.Text(payload, "blocker") is { Length: > 0 } blocker ? blocker : null, blocking));
    }

    /// <summary>Failures a later attempt can succeed past: the target's storage or the grain call itself failed.</summary>
    private static bool IsTransient(string code) => code is RelayErrorCodes.StorageFailure or RelayErrorCodes.DeliveryUnavailable or RelayErrorCodes.DeliveryPending or RelayErrorCodes.Unavailable;

    /// <summary>Counts one wake: a message newly stored in a session inbox that may wake its chat. Redeliveries and <c>wake: false</c> messages are not wakes.</summary>
    private void RecordWake(string sessionId, JsonObject payload, RelayReply delivered)
    {
        if (services.GetService<IActivitySink>() is not { } sink) return;
        try
        {
            if (payload["wake"] is JsonValue wake && wake.TryGetValue<bool>(out var wakes) && !wakes) return;
            if (JsonNode.Parse(delivered.DataJson)?["delivered"] is not JsonValue stored || !stored.TryGetValue<bool>(out var isNew) || !isNew) return;
            sink.Record(new ActivityEvent
            {
                Timestamp = DateTimeOffset.UtcNow, Kind = ActivityEvent.Wake, RepositoryId = JsonFields.Text(payload, "repositoryId"), SessionId = sessionId,
                Ok = true, Code = "Ok", Target = JsonFields.Text(payload, "taskId") is { Length: > 0 } taskId ? "task:" + Uri.EscapeDataString(JsonFields.Text(payload, "repositoryId")) + "/" + Uri.EscapeDataString(JsonFields.Text(payload, "boardId", "default")) + "/" + Uri.EscapeDataString(taskId) : null
            });
        }
        catch (Exception) { /* Telemetry never affects delivery. */ }
    }

    private async Task<bool> CommitAsync(StateEnvelope<CoordinationDocument> candidate)
    {
        var previous = persistent.State;
        persistent.State = candidate;
        try { await persistent.WriteStateAsync(); return true; }
        catch (Exception)
        {
            // Reload reconciles uncertain writes before any further operation or dependent effect.
            uncertainCommit = true;
            persistent.State = previous;
            try { await persistent.ReadStateAsync(); uncertainCommit = false; }
            finally { DeactivateOnIdle(); }
            return false;
        }
    }

    private static StateEnvelope<CoordinationDocument> Clone(StateEnvelope<CoordinationDocument> state) => new()
    {
        SchemaVersion = state.SchemaVersion, Value = new() { Json = state.Value.Json }, Effects = [.. state.Effects], DueWork = [.. state.DueWork]
    };

    internal static IRelayAggregateGrain Aggregate(IGrainFactory factory, string kind, string key) => kind.Replace("pkrelay.", "", StringComparison.Ordinal) switch
    {
        AggregateKinds.Session => factory.GetGrain<ISessionGrain>(key),
        AggregateKinds.Board => factory.GetGrain<ITaskBoardGrain>(key),
        AggregateKinds.Lock => factory.GetGrain<ILockGrain>(key),
        AggregateKinds.Control => factory.GetGrain<IControlGrain>(key),
        _ => throw new ArgumentException("Unknown coordination grain type.", nameof(kind))
    };
}

[GrainType("pkrelay.session")]
public sealed class SessionGrain([PersistentState("session", "relay")] IPersistentState<StateEnvelope<CoordinationDocument>> state, IGrainFactory grains, IServiceProvider services) : CoordinationGrain(state, grains, services), ISessionGrain
{ protected override string Kind => AggregateKinds.Session; }

[GrainType("pkrelay.board")]
public sealed class TaskBoardGrain([PersistentState("board", "relay")] IPersistentState<StateEnvelope<CoordinationDocument>> state, IGrainFactory grains, IServiceProvider services) : CoordinationGrain(state, grains, services), ITaskBoardGrain
{ protected override string Kind => AggregateKinds.Board; }

[GrainType("pkrelay.lock")]
public sealed class LockGrain([PersistentState("lock", "relay")] IPersistentState<StateEnvelope<CoordinationDocument>> state, IGrainFactory grains, IServiceProvider services) : CoordinationGrain(state, grains, services), ILockGrain
{ protected override string Kind => AggregateKinds.Lock; }

[GrainType("pkrelay.control")]
public sealed class ControlGrain([PersistentState("control", "relay")] IPersistentState<StateEnvelope<CoordinationDocument>> state, IGrainFactory grains, IServiceProvider services) : CoordinationGrain(state, grains, services), IControlGrain
{ protected override string Kind => AggregateKinds.Control; }
