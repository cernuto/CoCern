using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;

namespace PkRelay.Core;

public abstract class CoordinationGrain(IPersistentState<StateEnvelope<CoordinationDocument>> persistent, IGrainFactory grains, IServiceProvider services) : Grain, IRelayAggregateGrain
{
    protected abstract string Kind { get; }
    private bool uncertainCommit;

    public async Task<RelayReply> ExecuteAsync(RelayCaller caller, string operation, string argumentsJson)
    {
        JsonObject args;
        if (uncertainCommit) return CoordinationStateMachine.Error("StorageFailure");
        try { args = JsonNode.Parse(argumentsJson)?.AsObject() ?? new(); }
        catch (JsonException) { return CoordinationStateMachine.Error("InvalidArguments"); }
        if (!caller.IsInternal) return CoordinationStateMachine.Error("InternalOnly");
        if (operation == "__dispatch") return await DispatchAsync(caller, args);
        var candidate = Clone(persistent.State);
        var effectCount = candidate.Effects.Count;
        var reply = CoordinationStateMachine.Apply(candidate, Kind, this.GetPrimaryKeyString(), caller, operation, args, DateTimeOffset.UtcNow);
        if (!reply.Ok || !CoordinationStateMachine.IsMutation(operation)) return reply;
        for (var index = effectCount; index < candidate.Effects.Count; index++)
        {
            var effect = candidate.Effects[index]; var payload = JsonNode.Parse(effect.PayloadJson)!.AsObject();
            payload["__principalId"] = caller.PrincipalId; payload["__administrator"] = caller.IsAdministrator;
            payload["__repositories"] = JsonSerializer.SerializeToNode(caller.Repositories);
            candidate.Effects[index] = effect with { PayloadJson = payload.ToJsonString() };
        }
        ScheduleLease(candidate);
        return await CommitAsync(candidate) ? reply : CoordinationStateMachine.Error("StorageFailure");
    }

    private async Task<RelayReply> DispatchAsync(RelayCaller caller, JsonObject args)
    {
        var workId = CoordinationStateMachine.Text(args, "workId");
        var operation = CoordinationStateMachine.Text(args, "operation"); var actionKey = CoordinationStateMachine.Text(args, "actionKey");
        var now = DateTimeOffset.UtcNow;
        var candidate = Clone(persistent.State);
        foreach (var effect in candidate.Effects.Where(x => (workId.Length == 0 || x.Id == workId) && (operation.Length == 0 || x.Operation == operation) && (actionKey.Length == 0 || JsonNode.Parse(x.PayloadJson)?["actionKey"]?.ToString() == actionKey) && x.DueAt <= now).ToArray())
        {
            var payload = JsonNode.Parse(effect.PayloadJson)!.AsObject();
            var effectCaller = new RelayCaller(CoordinationStateMachine.Text(payload, "__principalId"), payload["__administrator"]?.GetValue<bool>() ?? false, payload["__repositories"]?.Deserialize<string[]>() ?? [], true);
            payload.Remove("__principalId"); payload.Remove("__administrator"); payload.Remove("__repositories");
            RelayReply result;
            try { result = await Aggregate(grains, effect.TargetKind, effect.TargetKey).ExecuteAsync(effectCaller, effect.Operation, payload.ToJsonString()); }
            catch (Exception) { result = CoordinationStateMachine.Error("DeliveryUnavailable"); }
            if (!result.Ok)
            {
                candidate.Effects[candidate.Effects.FindIndex(x => x.Id == effect.Id)] = effect with { DueAt = DateTimeOffset.UtcNow.AddSeconds(5) };
                if (!await CommitAsync(candidate)) return CoordinationStateMachine.Error("StorageFailure");
                return CoordinationStateMachine.Error("DeliveryPending", new { effectId = effect.Id, reason = result.Code });
            }
            candidate.Effects.RemoveAll(x => x.Id == effect.Id);
            if (!await CommitAsync(candidate)) return CoordinationStateMachine.Error("StorageFailure");
            if (effect.TargetKind == "session" && effect.Operation == "__deliver" && services.GetService<ICoordinationNotifier>() is { } notifier)
            {
                // Hints are best effort; the durable inbox remains authoritative.
                try { await notifier.NotifyAsync(effect.TargetKey, CoordinationStateMachine.Text(payload, "messageId")); }
                catch (Exception) { }
            }
            candidate = Clone(persistent.State);
        }
        if (candidate.DueWork.RemoveAll(x => (workId.Length == 0 || x.Id == workId) && x.DueAt <= now) > 0 && !await CommitAsync(candidate)) return CoordinationStateMachine.Error("StorageFailure");
        if (candidate.Effects.Any(x => (workId.Length == 0 || x.Id == workId) && (operation.Length == 0 || x.Operation == operation) && (actionKey.Length == 0 || JsonNode.Parse(x.PayloadJson)?["actionKey"]?.ToString() == actionKey))) return CoordinationStateMachine.Error("DeliveryPending");
        return CoordinationStateMachine.Ok(new { dispatched = true });
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

    private static void ScheduleLease(StateEnvelope<CoordinationDocument> envelope)
    {
        var state = JsonNode.Parse(envelope.Value.Json)!.AsObject();
        envelope.DueWork.RemoveAll(x => x.Kind == "lease");
        if (DateTimeOffset.TryParse(CoordinationStateMachine.Text(state, "leaseUntil"), out var lease)) envelope.DueWork.Add(new("lease", "lease", lease));
        if (state["tasks"] is JsonObject tasks)
            foreach (var entry in tasks)
                if (entry.Value is JsonObject task && DateTimeOffset.TryParse(CoordinationStateMachine.Text(task, "leaseUntil"), out var taskLease)) envelope.DueWork.Add(new("lease:" + entry.Key, "lease", taskLease));
    }

    private static StateEnvelope<CoordinationDocument> Clone(StateEnvelope<CoordinationDocument> state) => new()
    {
        SchemaVersion = state.SchemaVersion, Value = new() { Json = state.Value.Json }, Effects = [.. state.Effects], DueWork = [.. state.DueWork]
    };

    internal static IRelayAggregateGrain Aggregate(IGrainFactory factory, string kind, string key) => kind.Replace("pkrelay.", "", StringComparison.Ordinal) switch
    {
        "session" => factory.GetGrain<ISessionGrain>(key),
        "board" => factory.GetGrain<ITaskBoardGrain>(key),
        "lock" => factory.GetGrain<ILockGrain>(key),
        "control" => factory.GetGrain<IControlGrain>(key),
        _ => throw new ArgumentException("Unknown coordination grain type.", nameof(kind))
    };
}

[GrainType("pkrelay.session")]
public sealed class SessionGrain([PersistentState("session", "relay")] IPersistentState<StateEnvelope<CoordinationDocument>> state, IGrainFactory grains, IServiceProvider services) : CoordinationGrain(state, grains, services), ISessionGrain
{ protected override string Kind => "session"; }

[GrainType("pkrelay.board")]
public sealed class TaskBoardGrain([PersistentState("board", "relay")] IPersistentState<StateEnvelope<CoordinationDocument>> state, IGrainFactory grains, IServiceProvider services) : CoordinationGrain(state, grains, services), ITaskBoardGrain
{ protected override string Kind => "board"; }

[GrainType("pkrelay.lock")]
public sealed class LockGrain([PersistentState("lock", "relay")] IPersistentState<StateEnvelope<CoordinationDocument>> state, IGrainFactory grains, IServiceProvider services) : CoordinationGrain(state, grains, services), ILockGrain
{ protected override string Kind => "lock"; }

[GrainType("pkrelay.control")]
public sealed class ControlGrain([PersistentState("control", "relay")] IPersistentState<StateEnvelope<CoordinationDocument>> state, IGrainFactory grains, IServiceProvider services) : CoordinationGrain(state, grains, services), IControlGrain
{ protected override string Kind => "control"; }
