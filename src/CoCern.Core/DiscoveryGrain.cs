using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
using CoCern.Contracts;
using CoCern.Domain;
using CoCern.Domain.Messages;
using static CoCern.Domain.JsonFields;

namespace CoCern.Core;

public interface IDiscoveryGrain : IGrainWithStringKey
{
    Task<RelayReply> ExecuteAsync(RelayCaller caller, string operation, string argumentsJson);
}

/// <summary>
/// Orleans adapter for the discovery aggregate. It owns activation and persistence; every rule is applied by <see cref="DiscoveryStateMachine"/>
/// against a working copy that replaces the persisted state only after a successful write. It uses no outbox, leases or dispatch.
/// </summary>
[GrainType("cocern.discovery")]
public sealed class DiscoveryGrain([PersistentState("discovery", "relay")] IPersistentState<StateEnvelope<CoordinationDocument>> persistent) : Grain, IDiscoveryGrain
{
    private bool uncertainCommit;

    public async Task<RelayReply> ExecuteAsync(RelayCaller caller, string operation, string argumentsJson)
    {
        JsonObject args;
        if (uncertainCommit) return Replies.Error(RelayErrorCodes.StorageFailure);
        try { args = JsonNode.Parse(argumentsJson)?.AsObject() ?? new(); }
        catch (JsonException) { return Replies.Error(RelayErrorCodes.InvalidArguments); }
        if (!caller.IsInternal) return Replies.Error(RelayErrorCodes.InternalOnly);
        var candidate = Clone(persistent.State);
        var reply = DiscoveryStateMachine.Apply(candidate, caller, operation, args, DateTimeOffset.UtcNow);
        if (!reply.Ok || operation is DiscoveryOperations.Validate or DiscoveryOperations.Directory) return reply;
        return await CommitAsync(candidate) ? reply : Replies.Error(RelayErrorCodes.StorageFailure);
    }

    private async Task<bool> CommitAsync(StateEnvelope<CoordinationDocument> candidate)
    {
        var previous = persistent.State;
        persistent.State = candidate;
        try { await persistent.WriteStateAsync(); return true; }
        catch (Exception)
        {
            // Reload reconciles uncertain writes before any further operation.
            uncertainCommit = true;
            persistent.State = previous;
            // A reload that fails too leaves the commit uncertain (the next call is refused until a fresh activation reloads); never a 500.
            try { await persistent.ReadStateAsync(); uncertainCommit = false; }
            catch (Exception) { }
            finally { DeactivateOnIdle(); }
            return false;
        }
    }

    private static StateEnvelope<CoordinationDocument> Clone(StateEnvelope<CoordinationDocument> state) => new()
    {
        SchemaVersion = state.SchemaVersion, Value = new() { Json = state.Value.Json }, Effects = [.. state.Effects], DueWork = [.. state.DueWork]
    };
}

/// <summary>An enrolled machine as the hub knows it: its stable ID and the principal its connections act as.</summary>
public sealed record MachineIdentity(string MachineId, string PrincipalId);

/// <summary>The outcome of an enrollment: the machine, or the reply code explaining the refusal (MachineRevoked, AliasConflict, CapacityExceeded, ...).</summary>
public sealed record EnrollResult(MachineIdentity? Machine, string? Code);

/// <summary>How a machine credential fared: valid, unknown to the hub (NotFound), not decidable now because the discovery grain failed, or not checked because the caller's address is over its failure budget.</summary>
public enum CredentialStatus { Valid, Unknown, Unavailable, Throttled }

/// <summary>The outcome of checking a machine credential; <see cref="Machine"/> is set only when <see cref="Status"/> is Valid. <see cref="Fresh"/> is true only for an Unknown the grain has just answered NotFound, not one served from the negative cache.</summary>
public sealed record CredentialCheck(CredentialStatus Status, MachineIdentity? Machine = null, bool Fresh = false);

/// <summary>The hub's view of enrolled machines. Credentials are hashed before they reach the discovery grain and are never stored or cached raw.</summary>
public interface IMachineDirectory
{
    /// <summary>Enrolls a machine or re-enrolls it with the same credential; null when the hub refuses (revoked, credential mismatch, capacity).</summary>
    Task<MachineIdentity?> EnrollAsync(string machineId, string machineName, string credential, CancellationToken ct);

    /// <summary>Like <see cref="EnrollAsync"/>, but a refusal carries the reply code so callers can answer precisely.</summary>
    Task<EnrollResult> TryEnrollAsync(string machineId, string machineName, string credential, CancellationToken ct);

    /// <summary>The machine owning the credential, or null. Positive results are cached for 30 seconds.</summary>
    Task<MachineIdentity?> ValidateAsync(string credential, CancellationToken ct);

    /// <summary>Like <see cref="ValidateAsync"/>, but tells an unknown credential (the grain said NotFound) from a grain failure, which must never be treated as a rejection. When <paramref name="mayAskGrain"/> says no, the grain is not asked and the result is Throttled.</summary>
    Task<CredentialCheck> CheckAsync(string credential, Func<bool>? mayAskGrain, CancellationToken ct);

    /// <summary>Records the machine's announced chats. The machine ID always comes from <paramref name="machine"/>, never from the announcement.</summary>
    Task<RelayReply> AnnounceAsync(MachineIdentity machine, JsonObject announcement, CancellationToken ct);

    Task<RelayReply> DirectoryAsync(IReadOnlyList<string> repositories, CancellationToken ct);

    /// <summary>Which of <paramref name="sessionIds"/> the machine's principal opened in <paramref name="repository"/>, read from the control session directory; null when it cannot be read now.</summary>
    Task<IReadOnlySet<string>?> OwnedSessionsAsync(MachineIdentity machine, string repository, IReadOnlyCollection<string> sessionIds, CancellationToken ct);

    /// <summary>Revokes a machine (administrators only) and evicts its cached validations so revocation is immediate.</summary>
    Task<RelayReply> RevokeAsync(RelayCaller administrator, string machineId, CancellationToken ct);
}

public sealed class MachineDirectory(IGrainFactory grains, TimeProvider? clock = null) : IMachineDirectory
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);
    private static readonly RelayCaller Internal = new("relay-discovery", false, [], true);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private static readonly TimeSpan NegativeLifetime = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<string, (MachineIdentity Machine, DateTimeOffset Expires)> cache = new(StringComparer.Ordinal);
    /// <summary>Credential hashes the grain answered NotFound for, so junk tokens reach it at most once a minute each. Never keyed by the raw token.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> unknown = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> unknownOrder = new();
    private long revocations;
    private long enrollments;

    public async Task<MachineIdentity?> EnrollAsync(string machineId, string machineName, string credential, CancellationToken ct) => (await TryEnrollAsync(machineId, machineName, credential, ct)).Machine;

    public async Task<EnrollResult> TryEnrollAsync(string machineId, string machineName, string credential, CancellationToken ct)
    {
        var hash = Hash(credential);
        try
        {
            var reply = await CallAsync(Internal, DiscoveryOperations.Enroll, new() { ["machineId"] = machineId, ["machineName"] = machineName, ["credentialHash"] = hash }, ct);
            return reply.Ok ? new(Identity(reply), null) : new(null, reply.Code);
        }
        finally
        {
            // A credential just enrolled must work at once, even if its hash was refused moments ago. A validation still in flight re-checks the counter and drops its entry.
            Interlocked.Increment(ref enrollments);
            unknown.TryRemove(hash, out _);
        }
    }

    public const int NegativeCacheSize = 4096;

    public async Task<MachineIdentity?> ValidateAsync(string credential, CancellationToken ct) => (await CheckAsync(credential, null, ct)).Machine;

    public async Task<CredentialCheck> CheckAsync(string credential, Func<bool>? mayAskGrain, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(credential)) return new(CredentialStatus.Unknown);
        var hash = Hash(credential);
        if (cache.TryGetValue(hash, out var cached))
        {
            if (cached.Expires > time.GetUtcNow()) return new(CredentialStatus.Valid, cached.Machine);
            cache.TryRemove(hash, out _);
        }
        if (unknown.TryGetValue(hash, out var until))
        {
            if (until > time.GetUtcNow()) return new(CredentialStatus.Unknown);
            unknown.TryRemove(new KeyValuePair<string, DateTimeOffset>(hash, until));
        }
        // Over its failure budget, the caller is refused without asking the grain; that refusal says nothing about the credential, so it is not cached.
        if (mayAskGrain is not null && !mayAskGrain()) return new(CredentialStatus.Throttled);
        var generation = Interlocked.Read(ref revocations);
        var enrolled = Interlocked.Read(ref enrollments);
        RelayReply reply;
        try { reply = await CallAsync(Internal, DiscoveryOperations.Validate, new() { ["credentialHash"] = hash }, ct); }
        catch (Exception exception) when (exception is not OperationCanceledException) { return new(CredentialStatus.Unavailable); }
        // Only NotFound means the hub does not know the credential; any other failure (StorageFailure while a write is uncertain, ...) says nothing about it.
        if (!reply.Ok && reply.Code != RelayErrorCodes.NotFound) return new(CredentialStatus.Unavailable);
        if (!reply.Ok) { RememberUnknown(hash, enrolled); return new(CredentialStatus.Unknown, null, true); }
        var machine = Identity(reply);
        // Write first, then re-check: a revocation that completed while the grain was being asked must not leave this stale entry behind. This call still returns what the grain said.
        var entry = (Machine: machine, Expires: time.GetUtcNow().Add(CacheLifetime));
        cache[hash] = entry;
        if (Interlocked.Read(ref revocations) != generation) cache.TryRemove(new KeyValuePair<string, (MachineIdentity Machine, DateTimeOffset Expires)>(hash, entry));
        return new(CredentialStatus.Valid, machine);
    }

    public Task<RelayReply> AnnounceAsync(MachineIdentity machine, JsonObject announcement, CancellationToken ct)
    {
        var args = announcement.DeepClone().AsObject();
        args["machineId"] = machine.MachineId;
        return CallAsync(Internal, DiscoveryOperations.Announce, args, ct);
    }

    /// <summary>
    /// Open registered chats in <paramref name="repositories"/>, enriched by announced native routes. Registration alone never implies
    /// reachability. An unavailable source leaves the other source visible, with the result marked incomplete.
    /// </summary>
    public async Task<RelayReply> DirectoryAsync(IReadOnlyList<string> repositories, CancellationToken ct)
    {
        RelayReply reply;
        JsonObject? announced = null;
        try
        {
            reply = await CallAsync(Internal, DiscoveryOperations.Directory, new() { ["repositories"] = new JsonArray(repositories.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()) }, ct);
            if (reply.Ok && JsonNode.Parse(reply.DataJson) is JsonObject data && data["items"] is JsonArray) announced = data;
        }
        catch (Exception exception) when (exception is not OperationCanceledException) { reply = Replies.Error(RelayErrorCodes.StorageFailure); }
        static string? Field(JsonNode? item, string name) => item?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        var items = new List<JsonNode>();
        var seen = new HashSet<(string, string)>();
        var incomplete = announced is null || announced["truncated"]?.GetValue<bool>() == true;
        var anySource = announced is not null;
        foreach (var repository in repositories.Distinct(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var (workers, complete, available) = await DirectoryWorkersAsync(repository, ct);
            anySource |= available;
            incomplete |= !complete;
            var open = workers.Select(item => Field(item, "sessionId")).OfType<string>().ToHashSet(StringComparer.Ordinal);
            foreach (var item in announced?["items"] as JsonArray ?? [])
                if (Field(item, "repositoryId") == repository && Field(item, "relaySessionId") is { Length: > 0 } id && (!complete || open.Contains(id)) && seen.Add((repository, id))) items.Add(item!.DeepClone());
            foreach (var worker in workers)
                if (Field(worker, "sessionId") is { Length: > 0 } id && seen.Add((repository, id)))
                {
                    // No node announces a hub-local Claude chat; its own waiter's reads register it as waiting, and that waiter is its wake route.
                    var waiting = Field(worker, "availability") == ToolRouter.WaitingAvailability && worker["stale"]?.GetValue<bool>() != true;
                    var item = new JsonObject { ["repositoryId"] = repository, ["relaySessionId"] = id, ["label"] = Field(worker, "label") ?? "", ["reachable"] = waiting };
                    if (waiting) item["provider"] = ChatProviders.Claude;
                    if (Field(worker, "machineId") is { Length: > 0 } machineId) item["machineId"] = machineId;
                    items.Add(item);
                }
        }
        if (!anySource) return reply;
        RelayReply Result(int count) => Replies.Ok(new { items = items.Take(count), truncated = incomplete || count < items.Count });
        if (Replies.FitsWire(Result(items.Count), RelayLimits.AggregateReplyBytes)) return Result(items.Count);
        var low = 0; var high = items.Count;
        while (low < high) { var middle = (low + high + 1) / 2; if (Replies.FitsWire(Result(middle), RelayLimits.AggregateReplyBytes)) low = middle; else high = middle - 1; }
        return Result(low);
    }

    /// <summary>Read the existing bounded worker pages; only a complete listing can prove an announced session has closed.</summary>
    private async Task<(List<JsonObject> Workers, bool Complete, bool Available)> DirectoryWorkersAsync(string repository, CancellationToken ct)
    {
        var workers = new List<JsonObject>();
        var cursor = 0;
        var available = false;
        try
        {
            while (cursor < RelayLimits.MaxRecords)
            {
                ct.ThrowIfCancellationRequested();
                var args = new JsonObject { ["repositoryId"] = repository, ["cursor"] = cursor, ["limit"] = RelayLimits.MaxPageSize, ["includeStale"] = true };
                var found = await grains.GetGrain<IControlGrain>(AggregateKinds.ControlKey).ExecuteAsync(new RelayCaller(Internal.PrincipalId, false, [repository], true), RelayTools.WorkerList, args.ToJsonString());
                if (!found.Ok || JsonNode.Parse(found.DataJson) is not JsonObject data || data["items"] is not JsonArray page) break;
                available = true;
                foreach (var item in page.OfType<JsonObject>())
                {
                    if (item["sessionId"] is not JsonValue || item["repositoryId"]?.GetValue<string>() != repository) return (workers, false, available);
                    workers.Add(item);
                }
                if (data["nextCursor"] is null) return (workers, data["truncated"]?.GetValue<bool>() != true, available);
                var next = data["nextCursor"]!.GetValue<int>();
                if (next <= cursor || next >= RelayLimits.MaxRecords) break;
                cursor = next;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException) { }
        return (workers, false, available);
    }

    public async Task<IReadOnlySet<string>?> OwnedSessionsAsync(MachineIdentity machine, string repository, IReadOnlyCollection<string> sessionIds, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var query = new SessionOwnersQuery(repository, sessionIds.ToArray());
        RelayReply reply;
        // A grain fault means "cannot be read now", like a not-Ok reply, so the caller answers 503 rather than 500.
        try { reply = await grains.GetGrain<IControlGrain>(AggregateKinds.ControlKey).ExecuteAsync(new RelayCaller(machine.PrincipalId, false, [repository], true), SessionOwnersQuery.Operation, query.ToJson().ToJsonString()); }
        catch (Exception exception) when (exception is not OperationCanceledException) { return null; }
        return reply.Ok ? (JsonNode.Parse(reply.DataJson)?["owned"] as JsonArray ?? []).Select(x => x?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.Ordinal) : null;
    }

    public async Task<RelayReply> RevokeAsync(RelayCaller administrator, string machineId, CancellationToken ct)
    {
        try { return await CallAsync(administrator with { IsInternal = true }, DiscoveryOperations.Revoke, new() { ["machineId"] = machineId }, ct); }
        finally
        {
            // Bump after the call settles, then evict (even when it failed: a revocation that may have committed must not leave a usable cached credential).
            // A validation that started before this point re-checks the counter after writing its entry and removes it.
            Interlocked.Increment(ref revocations);
            foreach (var entry in cache.Where(x => x.Value.Machine.MachineId == machineId)) cache.TryRemove(entry.Key, out _);
        }
    }

    private void RememberUnknown(string hash, long enrolled)
    {
        var expires = time.GetUtcNow().Add(NegativeLifetime);
        if (unknown.TryAdd(hash, expires)) unknownOrder.Enqueue(hash);
        else unknown[hash] = expires;
        if (Interlocked.Read(ref enrollments) != enrolled) unknown.TryRemove(new KeyValuePair<string, DateTimeOffset>(hash, expires));
        // Bounded by the queue, which holds every cached hash at least once: drop the oldest first. A hash cached again later is queued again, so a stale queue entry only evicts it early, costing one extra grain call.
        while (unknownOrder.Count > NegativeCacheSize && unknownOrder.TryDequeue(out var oldest)) unknown.TryRemove(oldest, out _);
    }

    private async Task<RelayReply> CallAsync(RelayCaller caller, string operation, JsonObject args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return await grains.GetGrain<IDiscoveryGrain>(AggregateKinds.ControlKey).ExecuteAsync(caller, operation, args.ToJsonString());
    }

    private static MachineIdentity Identity(RelayReply reply)
    {
        var data = JsonNode.Parse(reply.DataJson)!.AsObject();
        return new(Text(data, "machineId"), Text(data, "principalId"));
    }

    private static string Hash(string credential) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));
}
