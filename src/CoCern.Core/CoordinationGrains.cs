using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
using CoCern.Contracts;
using CoCern.Domain;

namespace CoCern.Core;

public interface IRelayAggregateGrain : IGrainWithStringKey
{
    Task<RelayReply> ExecuteAsync(RelayCaller caller, string operation, string argumentsJson);
}

public interface ISessionGrain : IRelayAggregateGrain;
public interface ITaskBoardGrain : IRelayAggregateGrain;
public interface ILockGrain : IRelayAggregateGrain;
public interface IControlGrain : IRelayAggregateGrain;
public interface IAnomalyMonitorGrain : IRelayAggregateGrain;

/// <summary>
/// Orleans adapter for one aggregate. It owns activation, persistence and outbox delivery; every rule is applied by
/// <see cref="CoordinationStateMachine"/> against a working copy that replaces the persisted state only after a successful write.
/// </summary>
public abstract class CoordinationGrain(IPersistentState<StateEnvelope<CoordinationDocument>> persistent, IGrainFactory grains, IServiceProvider services) : Grain, IRelayAggregateGrain
{
    private static readonly TimeSpan RedeliveryDelay = TimeSpan.FromSeconds(5);
    protected abstract string Kind { get; }
    private bool uncertainCommit;

    /// <summary>The time operations and due work are judged by. The anomaly monitor reads it from its options, so tests can move it.</summary>
    protected virtual DateTimeOffset Now => DateTimeOffset.UtcNow;

    private protected IServiceProvider Services => services;

    /// <summary><c>Relay:Board:ArchiveAfterDays</c>, or its default when the host registers no board options.</summary>
    private protected int ArchiveAfterDays => services.GetService<BoardOptions>()?.ArchiveAfterDays ?? RelayLimits.DefaultArchiveAfterDays;

    /// <summary>
    /// Runs this aggregate's own due checks before its outbox is delivered, committing what they change and raise. Returns false when
    /// that commit failed. <paramref name="workId"/> is empty for all due work. The anomaly monitor runs its analytics check here.
    /// </summary>
    private protected virtual Task<bool> RunDueChecksAsync(StateEnvelope<CoordinationDocument> candidate, DateTimeOffset now, string workId) => Task.FromResult(true);

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
        var reply = ApplyInternal(candidate, caller, operation, args, Now, services.GetService<OrderOptions>() is { Enabled: true }, ArchiveAfterDays);
        // A read that emitted an effect (a verify's hourly activity report) commits it, or the effect would be lost.
        if (!reply.Ok || !CoordinationStateMachine.RequiresCommit(Kind, operation, priorTaskRevision, candidate) && candidate.Effects.Count == firstNewEffect) return reply;
        CoordinationStateMachine.ScheduleLeases(candidate, Kind == AggregateKinds.Board ? ArchiveAfterDays : 0);
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
        var now = Now;
        var candidate = Clone(persistent.State);
        var attention = services.GetService<AttentionOptions>();
        var ordersOn = services.GetService<OrderOptions>() is { Enabled: true };
        if (operation.Length == 0 && actionKey.Length == 0)
        {
            if (!await RunDueChecksAsync(candidate, now, workId)) return Replies.Error(RelayErrorCodes.StorageFailure);
            candidate = Clone(persistent.State);
        }
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
            if (EffectDelivery.Dropped(effect, attention, ordersOn))
            {
                candidate.Effects.RemoveAll(x => x.Id == effect.Id);
                if (!await CommitAsync(candidate)) return Replies.Error(RelayErrorCodes.StorageFailure);
                candidate = Clone(persistent.State);
                continue;
            }
            EffectDelivery.Prepare(effect, payload, attention);
            RelayReply result;
            try
            {
                if (Kind == AggregateKinds.Session && effect.TargetKind == AggregateKinds.Session && (effect.Operation == Operations.Deliver || Operations.IsAttention(effect.Operation)) && GrainContext is not null && effect.TargetKey == this.GetPrimaryKeyString())
                {
                    // A non-reentrant grain cannot await an RPC to itself. Commit its inbox entry
                    // and outbox removal together so a stale dispatch snapshot cannot erase delivery.
                    result = ApplyInternal(candidate, effectCaller, effect.Operation, payload, DateTimeOffset.UtcNow);
                }
                else result = await Aggregate(grains, effect.TargetKind, effect.TargetKey).ExecuteAsync(effectCaller, effect.Operation, payload.ToJsonString());
            }
            catch (Exception) { result = Replies.Error(RelayErrorCodes.DeliveryUnavailable); }
            var failure = result.Ok ? (EffectDelivery.Failure?)null : EffectDelivery.OnFailure(effect, payload, result.Code, Kind, ordersOn);
            // The failed order run is applied on this board's own state, so it is logged, raised to the managers and counted.
            if (failure == EffectDelivery.Failure.DropAsFailedOrderRun)
                ApplyInternal(candidate, effectCaller, Operations.OrderFailed, new JsonObject
                {
                    ["repositoryId"] = JsonFields.Text(payload, "repositoryId"), ["boardId"] = JsonFields.Text(payload, "boardId", "default"), ["orderId"] = JsonFields.Text(payload, Operations.OrderIdField),
                    ["run"] = JsonFields.Number(payload, Operations.OrderRunField), ["reason"] = "session " + effect.TargetKey + " refused the " + JsonFields.Text(payload, "kind") + " item (" + result.Code + ")"
                }, now, ordersEnabled: true);
            if (failure == EffectDelivery.Failure.Retry)
            {
                candidate.Effects[candidate.Effects.FindIndex(x => x.Id == effect.Id)] = effect with { DueAt = DateTimeOffset.UtcNow.Add(RedeliveryDelay) };
                if (!await CommitAsync(candidate)) return Replies.Error(RelayErrorCodes.StorageFailure);
                return Replies.Error(RelayErrorCodes.DeliveryPending, new { effectId = effect.Id, reason = result.Code });
            }
            candidate.Effects.RemoveAll(x => x.Id == effect.Id);
            if (!await CommitAsync(candidate)) return Replies.Error(RelayErrorCodes.StorageFailure);
            // A message wakes its chat only with attention off; with it on, only top-tier items (and the idle hold) wake.
            if (effect.TargetKind == AggregateKinds.Session && effect.Operation == Operations.Deliver && attention is { Enabled: false }) RecordWake(effect.TargetKey, payload, result);
            // Only a new top-tier attention item may wake its chat, so only that one counts as a wake and sends a hint.
            var urgent = effect.TargetKind == AggregateKinds.Session && effect.Operation == Operations.AttentionRaise && result.Ok && EffectDelivery.UrgentRaise(payload, result);
            if (urgent) RecordWake(effect.TargetKey, payload, Replies.Ok(new { delivered = true }));
            if (effect.TargetKind == AggregateKinds.Session && (effect.Operation == Operations.Deliver || urgent) && services.GetService<ICoordinationNotifier>() is { } notifier)
            {
                // Hints are best effort; the durable inbox remains authoritative.
                try { await notifier.NotifyAsync(effect.TargetKey, urgent ? "attention:" + JsonFields.Text(JsonNode.Parse(result.DataJson)!.AsObject(), "itemId") : JsonFields.Text(payload, "messageId")); }
                catch (Exception) { }
            }
            candidate = Clone(persistent.State);
        }
        // A filtered dispatch (one operation or action) ran no due checks, so it must not clear them.
        if (operation.Length == 0 && actionKey.Length == 0 && candidate.DueWork.RemoveAll(x => (workId.Length == 0 || x.Id == workId) && x.DueAt <= now) > 0 && !await CommitAsync(candidate)) return Replies.Error(RelayErrorCodes.StorageFailure);
        if (candidate.Effects.Any(Selected)) return Replies.Error(RelayErrorCodes.DeliveryPending);
        return Replies.Ok(new { dispatched = true });
    }

    /// <summary>Applies a lock's due lease check and commits what it raised, with the lock's repository as the effects' authority.</summary>
    private async Task<bool> CheckLeaseAsync(StateEnvelope<CoordinationDocument> candidate, DateTimeOffset now)
    {
        var repository = JsonFields.Text(JsonNode.Parse(candidate.Value.Json)!.AsObject(), "repositoryId");
        var checker = new RelayCaller("relay-dispatcher", false, [repository], true);
        ApplyInternal(candidate, checker, Operations.LeaseCheck, new JsonObject { ["repositoryId"] = repository }, now);
        candidate.DueWork.RemoveAll(x => x.Kind == DueWorkKinds.LeaseWarning && x.DueAt <= now);
        CoordinationStateMachine.ScheduleLeases(candidate);
        return await CommitAsync(candidate);
    }

    /// <summary>
    /// Applies an operation the grain drives itself (a due sweep or check, a self-delivery, an order failure) and stamps the effects it
    /// emitted with <paramref name="caller"/>; <see cref="ExecuteAsync"/> applies every call through it too. Without the stamp, delivery would rebuild an empty
    /// caller that no target accepts, and advisory effects would be dropped.
    /// </summary>
    private protected RelayReply ApplyInternal(StateEnvelope<CoordinationDocument> candidate, RelayCaller caller, string operation, JsonObject args, DateTimeOffset now,
        bool ordersEnabled = false, int archiveAfterDays = RelayLimits.DefaultArchiveAfterDays)
    {
        var first = candidate.Effects.Count;
        var reply = CoordinationStateMachine.Apply(candidate, Kind, this.GetPrimaryKeyString(), caller, operation, args, now, ordersEnabled, archiveAfterDays);
        if (reply.Ok) CoordinationStateMachine.StampEffects(candidate.Effects, first, caller);
        return reply;
    }

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
                Ok = true, Code = "Ok", Target = JsonFields.Text(payload, "taskId") is { Length: > 0 } taskId ? CallTelemetry.TaskTarget(payload, taskId) : null
            });
        }
        catch (Exception) { /* Telemetry never affects delivery. */ }
    }

    private protected async Task<bool> CommitAsync(StateEnvelope<CoordinationDocument> candidate)
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
            catch (Exception) { /* Still uncertain: every call answers StorageFailure until the grain reactivates. */ }
            finally { DeactivateOnIdle(); }
            return false;
        }
    }

    private protected static StateEnvelope<CoordinationDocument> Clone(StateEnvelope<CoordinationDocument> state) => new()
    {
        SchemaVersion = state.SchemaVersion, Value = new() { Json = state.Value.Json }, Effects = [.. state.Effects], DueWork = [.. state.DueWork]
    };

    internal static IRelayAggregateGrain Aggregate(IGrainFactory factory, string kind, string key) => kind.Replace("cocern.", "", StringComparison.Ordinal) switch
    {
        AggregateKinds.Session => factory.GetGrain<ISessionGrain>(key),
        AggregateKinds.Board => factory.GetGrain<ITaskBoardGrain>(key),
        AggregateKinds.Lock => factory.GetGrain<ILockGrain>(key),
        AggregateKinds.Control => factory.GetGrain<IControlGrain>(key),
        AggregateKinds.Anomaly => factory.GetGrain<IAnomalyMonitorGrain>(key),
        _ => throw new ArgumentException("Unknown coordination grain type.", nameof(kind))
    };
}

[GrainType("cocern.session")]
public sealed class SessionGrain([PersistentState("session", "relay")] IPersistentState<StateEnvelope<CoordinationDocument>> state, IGrainFactory grains, IServiceProvider services) : CoordinationGrain(state, grains, services), ISessionGrain
{ protected override string Kind => AggregateKinds.Session; }

[GrainType("cocern.board")]
public sealed class TaskBoardGrain([PersistentState("board", "relay")] IPersistentState<StateEnvelope<CoordinationDocument>> state, IGrainFactory grains, IServiceProvider services) : CoordinationGrain(state, grains, services), ITaskBoardGrain
{
    protected override string Kind => AggregateKinds.Board;

    protected override DateTimeOffset Now => (Services.GetService<BoardOptions>()?.Time ?? TimeProvider.System).GetUtcNow();

    /// <summary>
    /// The board's due archive sweep: moves tasks finished longer than <c>Relay:Board:ArchiveAfterDays</c> to the archive and commits that,
    /// before the outbox is delivered. One in-memory pass and one commit, so the pump does not wait on anything slow.
    /// </summary>
    private protected override async Task<bool> RunDueChecksAsync(StateEnvelope<CoordinationDocument> candidate, DateTimeOffset now, string workId)
    {
        if (!candidate.DueWork.Any(x => x.Kind == DueWorkKinds.TaskArchive && x.DueAt <= now && (workId.Length == 0 || x.Id == workId))) return true;
        var repository = JsonFields.Text(JsonNode.Parse(candidate.Value.Json)!.AsObject(), "repositoryId");
        if (repository.Length == 0) return true;
        var sweeper = new RelayCaller("relay-archive", false, [repository], true);
        // The board's own ID, so the attention closes it emits name this board and not the default one.
        var sweep = new JsonObject { ["repositoryId"] = repository, ["boardId"] = BoardId(this.GetPrimaryKeyString()) };
        if (!ApplyInternal(candidate, sweeper, Operations.TaskArchive, sweep, now, false, ArchiveAfterDays).Ok)
        {
            // A sweep the board refuses would otherwise stay due and run on every pump: try again after a backoff.
            candidate.DueWork.RemoveAll(x => x.Kind == DueWorkKinds.TaskArchive);
            candidate.DueWork.Add(new(DueWorkKinds.TaskArchive, DueWorkKinds.TaskArchive, now.AddMinutes(RelayLimits.ArchiveRetryMinutes)));
            return await CommitAsync(candidate);
        }
        CoordinationStateMachine.ScheduleLeases(candidate, ArchiveAfterDays);
        return await CommitAsync(candidate);
    }

    /// <summary>The board ID of a board grain key (escaped repository, <c>/</c>, escaped board ID); the escaping keeps the first <c>/</c> the separator.</summary>
    internal static string BoardId(string key) => key.IndexOf('/') is var slash and >= 0 ? Uri.UnescapeDataString(key[(slash + 1)..]) : "default";
}

[GrainType("cocern.lock")]
public sealed class LockGrain([PersistentState("lock", "relay")] IPersistentState<StateEnvelope<CoordinationDocument>> state, IGrainFactory grains, IServiceProvider services) : CoordinationGrain(state, grains, services), ILockGrain
{ protected override string Kind => AggregateKinds.Lock; }

[GrainType("cocern.control")]
public sealed class ControlGrain([PersistentState("control", "relay")] IPersistentState<StateEnvelope<CoordinationDocument>> state, IGrainFactory grains, IServiceProvider services) : CoordinationGrain(state, grains, services), IControlGrain
{ protected override string Kind => AggregateKinds.Control; }
