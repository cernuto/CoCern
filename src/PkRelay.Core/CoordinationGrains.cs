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
        foreach (var effect in candidate.Effects.Where(x => Selected(x) && x.DueAt <= now).ToArray())
        {
            var payload = JsonNode.Parse(effect.PayloadJson)!.AsObject();
            var effectCaller = CoordinationStateMachine.TakeEffectCaller(payload);
            RelayReply result;
            try
            {
                if (Kind == AggregateKinds.Session && effect.TargetKind == AggregateKinds.Session && effect.Operation == Operations.Deliver && GrainContext is not null && effect.TargetKey == this.GetPrimaryKeyString())
                {
                    // A non-reentrant grain cannot await an RPC to itself. Commit its inbox entry
                    // and outbox removal together so a stale dispatch snapshot cannot erase delivery.
                    result = CoordinationStateMachine.Apply(candidate, Kind, effect.TargetKey, effectCaller, effect.Operation, payload, DateTimeOffset.UtcNow);
                }
                else result = await Aggregate(grains, effect.TargetKind, effect.TargetKey).ExecuteAsync(effectCaller, effect.Operation, payload.ToJsonString());
            }
            catch (Exception) { result = Replies.Error(RelayErrorCodes.DeliveryUnavailable); }
            if (!result.Ok)
            {
                candidate.Effects[candidate.Effects.FindIndex(x => x.Id == effect.Id)] = effect with { DueAt = DateTimeOffset.UtcNow.Add(RedeliveryDelay) };
                if (!await CommitAsync(candidate)) return Replies.Error(RelayErrorCodes.StorageFailure);
                return Replies.Error(RelayErrorCodes.DeliveryPending, new { effectId = effect.Id, reason = result.Code });
            }
            candidate.Effects.RemoveAll(x => x.Id == effect.Id);
            if (!await CommitAsync(candidate)) return Replies.Error(RelayErrorCodes.StorageFailure);
            if (effect.TargetKind == AggregateKinds.Session && effect.Operation == Operations.Deliver && services.GetService<ICoordinationNotifier>() is { } notifier)
            {
                // Hints are best effort; the durable inbox remains authoritative.
                try { await notifier.NotifyAsync(effect.TargetKey, JsonFields.Text(payload, "messageId")); }
                catch (Exception) { }
            }
            candidate = Clone(persistent.State);
        }
        if (candidate.DueWork.RemoveAll(x => (workId.Length == 0 || x.Id == workId) && x.DueAt <= now) > 0 && !await CommitAsync(candidate)) return Replies.Error(RelayErrorCodes.StorageFailure);
        if (candidate.Effects.Any(Selected)) return Replies.Error(RelayErrorCodes.DeliveryPending);
        return Replies.Ok(new { dispatched = true });
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
