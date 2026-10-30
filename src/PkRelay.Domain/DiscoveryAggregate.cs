using System.Text.Json.Nodes;
using PkRelay.Contracts;
using static PkRelay.Contracts.RelayErrorCodes;
using static PkRelay.Domain.JsonFields;
using static PkRelay.Domain.Replies;

namespace PkRelay.Domain;

/// <summary>Hub-internal operations on the discovery aggregate. Callers must be internal.</summary>
public static class DiscoveryOperations
{
    public const string Enroll = "__enroll";
    public const string Validate = "__validate";
    public const string Announce = "__announce";
    public const string Directory = "__directory";
    public const string Revoke = "__revoke";
}

/// <summary>Bounds and timing of machine discovery.</summary>
public static class DiscoveryLimits
{
    public const int MaxMachines = 256;
    public const int MaxChatsPerMachine = 64;
    public static readonly TimeSpan AnnounceInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan LivenessTimeout = TimeSpan.FromSeconds(90);
}

/// <summary>
/// The machine-scoped discovery aggregate: enrolled machines, their hashed credentials, the chats each announced and a deny list.
/// It decides identity and wiring only. State is <c>{ machines: { id: {...} }, denied: { id: true } }</c>; the envelope changes only when a mutation succeeds.
/// </summary>
public static class DiscoveryStateMachine
{
    public static RelayReply Apply(StateEnvelope<CoordinationDocument> envelope, RelayCaller caller, string operation, JsonObject args, DateTimeOffset now)
    {
        if (ByteSize(args) > RelayLimits.MaxPayloadBytes) return Error(PayloadTooLarge);
        if (!caller.IsInternal) return Error(InternalOnly);
        var state = JsonNode.Parse(envelope.Value.Json)!.AsObject();
        RelayReply result;
        try
        {
            result = operation switch
            {
                DiscoveryOperations.Enroll => Enroll(state, args),
                DiscoveryOperations.Validate => Validate(state, args),
                DiscoveryOperations.Announce => Announce(state, args, now),
                DiscoveryOperations.Directory => Directory(state, args, now),
                DiscoveryOperations.Revoke => Revoke(state, caller, args),
                _ => Error(UnknownTool)
            };
        }
        catch (ArgumentException exception) { return Error(InvalidArguments, new { error = exception.Message }); }
        catch (InvalidOperationException) { return Error(InvalidArguments); }
        catch (FormatException) { return Error(InvalidArguments); }
        if (!result.Ok) return result;
        if (!FitsWire(result, RelayLimits.AggregateReplyBytes)) return Error(PayloadTooLarge);
        if (operation is DiscoveryOperations.Enroll or DiscoveryOperations.Announce or DiscoveryOperations.Revoke) envelope.Value = new() { Json = state.ToJsonString() };
        return result;
    }

    private static string PrincipalOf(string machineId) => "machine:" + machineId;

    private static RelayReply Enroll(JsonObject state, JsonObject args)
    {
        var id = Required(args, "machineId");
        var name = Required(args, "machineName");
        var hash = Required(args, "credentialHash");
        if (ObjectAt(state, "denied").ContainsKey(id)) return Error(MachineRevoked);
        var machines = ObjectAt(state, "machines");
        if (machines[id] is JsonObject existing)
        {
            if (!SecretEquals(Text(existing, "credentialHash"), hash)) return Error(AliasConflict);
            existing["machineName"] = name;
        }
        else
        {
            if (machines.Count >= DiscoveryLimits.MaxMachines) return Error(CapacityExceeded);
            machines[id] = new JsonObject { ["machineName"] = name, ["credentialHash"] = hash, ["lastSeen"] = null, ["clients"] = new JsonArray(), ["chats"] = new JsonArray() };
        }
        return Ok(new { machineId = id, principalId = PrincipalOf(id) });
    }

    private static RelayReply Validate(JsonObject state, JsonObject args)
    {
        var hash = Required(args, "credentialHash");
        var found = ObjectAt(state, "machines").FirstOrDefault(x => x.Value is JsonObject machine && SecretEquals(Text(machine, "credentialHash"), hash));
        return found.Key is null ? Error(NotFound) : Ok(new { machineId = found.Key, principalId = PrincipalOf(found.Key) });
    }

    private static RelayReply Announce(JsonObject state, JsonObject args, DateTimeOffset now)
    {
        var id = Required(args, "machineId");
        var name = Required(args, "machineName");
        var clients = Strings(args["clients"]).Select(x => x.Length is > 0 and <= RelayLimits.MaxIdentifierLength ? x : throw new ArgumentException("clients entries must be 1 to 256 characters.")).ToArray();
        var source = args["chats"] as JsonArray ?? new JsonArray();
        if (source.Count > DiscoveryLimits.MaxChatsPerMachine) return Error(CapacityExceeded);
        var chats = new JsonArray();
        foreach (var node in source)
        {
            var chat = node!.AsObject();
            chats.Add(new JsonObject
            {
                ["provider"] = Required(chat, "provider"), ["threadId"] = Required(chat, "threadId"), ["relaySessionId"] = Required(chat, "relaySessionId"),
                ["repositoryId"] = Required(chat, "repositoryId"), ["label"] = Required(chat, "label"), ["wakeable"] = Flag(chat, "wakeable"), ["waiterRunning"] = Flag(chat, "waiterRunning")
            });
        }
        if (ObjectAt(state, "machines")[id] is not JsonObject machine) return Error(NotFound);
        machine["machineName"] = name; machine["lastSeen"] = now.ToString("O");
        machine["clients"] = new JsonArray(clients.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()); machine["chats"] = chats;
        var routes = chats.Select(x => x!.AsObject()).Where(x => Flag(x, "wakeable")).Select(x => new { provider = Text(x, "provider"), threadId = Text(x, "threadId"), relaySessionId = Text(x, "relaySessionId"), repositoryId = Text(x, "repositoryId") }).ToArray();
        return Ok(new { routes });
    }

    /// <summary>The announced chats in the given repositories. Thread IDs, hashes and credentials never leave the aggregate.</summary>
    private static RelayReply Directory(JsonObject state, JsonObject args, DateTimeOffset now)
    {
        var repositories = Strings(args["repositories"]).ToHashSet(StringComparer.Ordinal);
        var items = new List<object>();
        foreach (var (_, node) in ObjectAt(state, "machines"))
        {
            var machine = node!.AsObject();
            var lastSeen = Date(machine, "lastSeen");
            var live = lastSeen != DateTimeOffset.MinValue && now - lastSeen <= DiscoveryLimits.LivenessTimeout;
            foreach (var chat in ArrayAt(machine, "chats").Select(x => x!.AsObject()).Where(x => repositories.Contains(Text(x, "repositoryId"))))
            {
                var provider = Text(chat, "provider");
                var ready = provider.ToLowerInvariant() switch { "codex" => Flag(chat, "wakeable"), "claude" => Flag(chat, "waiterRunning"), _ => false };
                items.Add(new { label = Text(chat, "label"), provider, machineName = Text(machine, "machineName"), relaySessionId = Text(chat, "relaySessionId"), repositoryId = Text(chat, "repositoryId"), reachable = live && ready });
            }
        }
        var truncated = false;
        while (items.Count > 0 && !FitsWire(Ok(new { items, truncated }), RelayLimits.AggregateReplyBytes)) { items.RemoveAt(items.Count - 1); truncated = true; }
        return Ok(new { items, truncated });
    }

    private static RelayReply Revoke(JsonObject state, RelayCaller caller, JsonObject args)
    {
        if (!caller.IsAdministrator) return Error(AdministratorRequired);
        var id = Required(args, "machineId");
        var machines = ObjectAt(state, "machines");
        var denied = ObjectAt(state, "denied");
        if (!machines.ContainsKey(id) && !denied.ContainsKey(id)) return Error(NotFound);
        if (!denied.ContainsKey(id) && denied.Count >= RelayLimits.MaxRecords) return Error(CapacityExceeded);
        machines.Remove(id);
        denied[id] = true;
        return Ok(new { revoked = true });
    }
}
