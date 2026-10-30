using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
using PkRelay.Contracts;
using PkRelay.Domain;
using static PkRelay.Domain.JsonFields;

namespace PkRelay.Core;

public interface IDiscoveryGrain : IGrainWithStringKey
{
    Task<RelayReply> ExecuteAsync(RelayCaller caller, string operation, string argumentsJson);
}

/// <summary>
/// Orleans adapter for the discovery aggregate. It owns activation and persistence; every rule is applied by <see cref="DiscoveryStateMachine"/>
/// against a working copy that replaces the persisted state only after a successful write. It uses no outbox, leases or dispatch.
/// </summary>
[GrainType("pkrelay.discovery")]
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
            try { await persistent.ReadStateAsync(); uncertainCommit = false; }
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

/// <summary>The hub's view of enrolled machines. Credentials are hashed before they reach the discovery grain and are never stored or cached raw.</summary>
public interface IMachineDirectory
{
    /// <summary>Enrolls a machine or re-enrolls it with the same credential; null when the hub refuses (revoked, credential mismatch, capacity).</summary>
    Task<MachineIdentity?> EnrollAsync(string machineId, string machineName, string credential, CancellationToken ct);

    /// <summary>Like <see cref="EnrollAsync"/>, but a refusal carries the reply code so callers can answer precisely.</summary>
    Task<EnrollResult> TryEnrollAsync(string machineId, string machineName, string credential, CancellationToken ct);

    /// <summary>The machine owning the credential, or null. Positive results are cached for 30 seconds.</summary>
    Task<MachineIdentity?> ValidateAsync(string credential, CancellationToken ct);

    /// <summary>Records the machine's announced chats. The machine ID always comes from <paramref name="machine"/>, never from the announcement.</summary>
    Task<RelayReply> AnnounceAsync(MachineIdentity machine, JsonObject announcement, CancellationToken ct);

    Task<RelayReply> DirectoryAsync(IReadOnlyList<string> repositories, CancellationToken ct);

    /// <summary>Revokes a machine (administrators only) and evicts its cached validations so revocation is immediate.</summary>
    Task<RelayReply> RevokeAsync(RelayCaller administrator, string machineId, CancellationToken ct);
}

public sealed class MachineDirectory(IGrainFactory grains, TimeProvider? clock = null) : IMachineDirectory
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);
    private static readonly RelayCaller Internal = new("relay-discovery", false, [], true);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (MachineIdentity Machine, DateTimeOffset Expires)> cache = new(StringComparer.Ordinal);
    private long revocations;

    public async Task<MachineIdentity?> EnrollAsync(string machineId, string machineName, string credential, CancellationToken ct) => (await TryEnrollAsync(machineId, machineName, credential, ct)).Machine;

    public async Task<EnrollResult> TryEnrollAsync(string machineId, string machineName, string credential, CancellationToken ct)
    {
        var reply = await CallAsync(Internal, DiscoveryOperations.Enroll, new() { ["machineId"] = machineId, ["machineName"] = machineName, ["credentialHash"] = Hash(credential) }, ct);
        return reply.Ok ? new(Identity(reply), null) : new(null, reply.Code);
    }

    public async Task<MachineIdentity?> ValidateAsync(string credential, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(credential)) return null;
        var hash = Hash(credential);
        if (cache.TryGetValue(hash, out var cached))
        {
            if (cached.Expires > time.GetUtcNow()) return cached.Machine;
            cache.TryRemove(hash, out _);
        }
        var generation = Interlocked.Read(ref revocations);
        var reply = await CallAsync(Internal, DiscoveryOperations.Validate, new() { ["credentialHash"] = hash }, ct);
        if (!reply.Ok) return null;
        var machine = Identity(reply);
        // Write first, then re-check: a revocation that completed while the grain was being asked must not leave this stale entry behind. This call still returns what the grain said.
        var entry = (Machine: machine, Expires: time.GetUtcNow().Add(CacheLifetime));
        cache[hash] = entry;
        if (Interlocked.Read(ref revocations) != generation) cache.TryRemove(new KeyValuePair<string, (MachineIdentity Machine, DateTimeOffset Expires)>(hash, entry));
        return machine;
    }

    public Task<RelayReply> AnnounceAsync(MachineIdentity machine, JsonObject announcement, CancellationToken ct)
    {
        var args = announcement.DeepClone().AsObject();
        args["machineId"] = machine.MachineId;
        return CallAsync(Internal, DiscoveryOperations.Announce, args, ct);
    }

    public Task<RelayReply> DirectoryAsync(IReadOnlyList<string> repositories, CancellationToken ct) =>
        CallAsync(Internal, DiscoveryOperations.Directory, new() { ["repositories"] = new JsonArray(repositories.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()) }, ct);

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
