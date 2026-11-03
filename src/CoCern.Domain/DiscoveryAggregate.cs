using System.Text.Json.Nodes;
using CoCern.Contracts;
using static CoCern.Contracts.RelayErrorCodes;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Domain;

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
    /// <summary>Expired machine IDs kept so none is reissued; the oldest goes first. Freeing a slot takes 30 idle days, so this lasts years.</summary>
    public const int MaxExpiredIds = 4 * MaxMachines;
    public const int MaxChatsPerMachine = 64;
    public static readonly TimeSpan AnnounceInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan LivenessTimeout = TimeSpan.FromSeconds(90);
}

/// <summary>
/// The machine-scoped discovery aggregate: enrolled machines, their hashed credentials, the chats each announced and a deny list.
/// It decides identity and wiring only. State is <c>{ machines: { id: {...} }, denied: { id: true }, expired: { id: seconds } }</c>; the envelope changes only when a mutation succeeds.
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
                DiscoveryOperations.Enroll => Enroll(state, args, now),
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

    private static RelayReply Enroll(JsonObject state, JsonObject args, DateTimeOffset now)
    {
        var id = Required(args, "machineId");
        var name = Required(args, "machineName");
        var hash = Required(args, "credentialHash");
        if (ObjectAt(state, "denied").ContainsKey(id)) return Error(MachineRevoked);
        // A machine's ID is its principal, which owns actions and sessions; reissuing it would hand those to whoever enrolled next.
        if (ObjectAt(state, "expired").ContainsKey(id)) return Error(MachineExpired);
        var machines = ObjectAt(state, "machines");
        if (machines[id] is JsonObject existing)
        {
            if (!SecretEquals(Text(existing, "credentialHash"), hash)) return Error(AliasConflict);
            existing["machineName"] = name;
        }
        else
        {
            if (machines.Count >= DiscoveryLimits.MaxMachines && !RemoveExpired(state, machines, now)) return Error(CapacityExceeded);
            machines[id] = new JsonObject { ["machineName"] = name, ["credentialHash"] = hash, ["enrolledAt"] = now.ToString("O"), ["lastSeen"] = null, ["clients"] = new JsonArray(), ["chats"] = new JsonArray() };
        }
        return Ok(new { machineId = id, principalId = PrincipalOf(id) });
    }

    /// <summary>Frees one slot by removing the machine unseen longest, if unseen past the expiry, so strangers cannot hold the table forever.</summary>
    private static bool RemoveExpired(JsonObject state, JsonObject machines, DateTimeOffset now)
    {
        var cutoff = now - TimeSpan.FromDays(RelayLimits.MachineExpiryDays);
        // A machine that never announced counts from its enrollment; one from before enrolledAt existed counts as oldest.
        var oldest = machines.Select(x => (x.Key, Seen: Date(x.Value!.AsObject(), "lastSeen") is var seen && seen != DateTimeOffset.MinValue ? seen : Date(x.Value!.AsObject(), "enrolledAt")))
            .Where(x => x.Seen < cutoff).OrderBy(x => x.Seen).ThenBy(x => x.Key, StringComparer.Ordinal).FirstOrDefault();
        if (oldest.Key is null) return false;
        machines.Remove(oldest.Key);
        var expired = ObjectAt(state, "expired");
        if (expired.Count >= DiscoveryLimits.MaxExpiredIds)
            expired.Remove(expired.OrderBy(x => Number(expired, x.Key)).ThenBy(x => x.Key, StringComparer.Ordinal).First().Key);
        expired[oldest.Key] = now.ToUnixTimeSeconds();
        return true;
    }

    private static RelayReply Validate(JsonObject state, JsonObject args)
    {
        var hash = Required(args, "credentialHash");
        var found = ObjectAt(state, "machines").FirstOrDefault(x => x.Value is JsonObject machine && SecretEquals(Text(machine, "credentialHash"), hash));
        return found.Key is null ? Error(NotFound) : Ok(new { machineId = found.Key, principalId = PrincipalOf(found.Key) });
    }

    /// <summary>The provider a node announces for a session it has not mapped to a chat.</summary>
    private const string UnmappedProvider = ChatProviders.Unknown;

    /// <summary>When the announced chat's session was opened, or the earliest time when absent or unreadable.</summary>
    private static DateTimeOffset OpenedAt(JsonObject chat) =>
        chat["openedAt"] is JsonValue value && value.TryGetValue<string>(out var text) && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var at) ? at : DateTimeOffset.MinValue;

    private static RelayReply Announce(JsonObject state, JsonObject args, DateTimeOffset now)
    {
        var id = Required(args, "machineId");
        var name = Required(args, "machineName");
        var clients = StringList(args["clients"]).Select(x => x.Length is > 0 and <= RelayLimits.MaxIdentifierLength ? x : throw new ArgumentException("clients entries must be 1 to 256 characters.")).ToArray();
        var source = args["chats"] is null ? new JsonArray() : args["chats"] as JsonArray ?? throw new ArgumentException("chats must be an array.");
        if (source.Count > DiscoveryLimits.MaxChatsPerMachine) return Error(CapacityExceeded);
        var parsed = new List<(JsonObject Chat, DateTimeOffset OpenedAt, int Order)>();
        foreach (var node in source)
        {
            var chat = node as JsonObject ?? throw new ArgumentException("chats entries must be objects.");
            parsed.Add((new JsonObject
            {
                ["provider"] = Required(chat, "provider"), ["threadId"] = Required(chat, "threadId"), ["relaySessionId"] = Required(chat, "relaySessionId"),
                ["repositoryId"] = Required(chat, "repositoryId"), ["label"] = Required(chat, "label"), ["wakeable"] = Flag(chat, "wakeable"), ["waiterRunning"] = Flag(chat, "waiterRunning")
            }, OpenedAt(chat), parsed.Count));
        }
        // One provider thread maps to one current relay session: keep only the newest chat per (provider, thread), so a stale node cannot announce a superseded route.
        // Newest is the latest openedAt; without one, the earlier entry wins (the node lists newest first). Unmapped chats share a placeholder thread and are never collapsed.
        var chats = new JsonArray(parsed.GroupBy(x => Text(x.Chat, "provider") == UnmappedProvider ? "unmapped:" + x.Order : Text(x.Chat, "provider").ToLowerInvariant() + "/" + Text(x.Chat, "threadId").ToLowerInvariant())
            .Select(group => group.OrderByDescending(x => x.OpenedAt).ThenBy(x => x.Order).First()).OrderBy(x => x.Order).Select(x => (JsonNode)x.Chat).ToArray());
        if (ObjectAt(state, "machines")[id] is not JsonObject machine) return Error(NotFound);
        machine["machineName"] = name; machine["lastSeen"] = now.ToString("O");
        machine["clients"] = new JsonArray(clients.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()); machine["chats"] = chats;
        var routes = chats.Select(x => x!.AsObject()).Where(x => Flag(x, "wakeable")).Select(x => new { provider = Text(x, "provider"), threadId = Text(x, "threadId"), relaySessionId = Text(x, "relaySessionId"), repositoryId = Text(x, "repositoryId") }).ToArray();
        return Ok(new { routes });
    }

    /// <summary>The announced chats in the given repositories. Thread IDs, hashes and credentials never leave the aggregate.</summary>
    private static RelayReply Directory(JsonObject state, JsonObject args, DateTimeOffset now)
    {
        var repositories = StringList(args["repositories"]).ToHashSet(StringComparer.Ordinal);
        var items = new List<object>();
        foreach (var (_, node) in ObjectAt(state, "machines"))
        {
            var machine = node!.AsObject();
            var lastSeen = OptionalDate(machine, "lastSeen");
            foreach (var chat in ArrayAt(machine, "chats").Select(x => x!.AsObject()).Where(x => repositories.Contains(Text(x, "repositoryId"))))
            {
                var provider = Text(chat, "provider");
                var reachable = ReadModels.IsReachable(provider, Flag(chat, "wakeable"), Flag(chat, "waiterRunning"), lastSeen, now);
                items.Add(new { label = Text(chat, "label"), provider, machineName = Text(machine, "machineName"), relaySessionId = Text(chat, "relaySessionId"), repositoryId = Text(chat, "repositoryId"), reachable });
            }
        }
        if (Fits(items, items.Count)) return Ok(new { items, truncated = false });
        // Binary search the largest prefix that fits: the size grows with the count, and each probe serializes once.
        var low = 0; var high = items.Count;
        while (low < high) { var middle = (low + high + 1) / 2; if (Fits(items, middle)) low = middle; else high = middle - 1; }
        return Ok(new { items = items.Take(low), truncated = true });
    }

    private static bool Fits(List<object> items, int count) => FitsWire(Ok(new { items = items.Take(count), truncated = count < items.Count }), RelayLimits.AggregateReplyBytes);

    /// <summary>An absent value is empty; otherwise it must be an array of strings, with no nulls or other kinds of element.</summary>
    private static string[] StringList(JsonNode? node) => node switch
    {
        null => [],
        JsonArray array => array.Select(x => x is JsonValue value && value.TryGetValue<string>(out var text) ? text : throw new ArgumentException("Expected an array of strings.")).ToArray(),
        _ => throw new ArgumentException("Expected an array of strings.")
    };

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
