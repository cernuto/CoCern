using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain.State;
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
/// The last-seen times the discovery grain holds in memory but has not written yet (it writes an announce that changes nothing else at most every
/// <see cref="PersistEvery"/> per machine), so a reader of the stored row, such as the dashboard, still sees a live machine as live. A stored time newer
/// than the remembered one wins, and a remembered one is used only within <see cref="PersistEvery"/> plus a minute of the stored one.
/// </summary>
public static class DiscoveryContacts
{
    public static readonly TimeSpan PersistEvery = TimeSpan.FromMinutes(5);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> Seen = new(StringComparer.Ordinal);

    public static void Record(string machineId, DateTimeOffset at)
    {
        // Bounded by the machine table; a table that somehow outgrew it starts over, which only costs readers up to PersistEvery of staleness.
        if (Seen.Count > 4 * DiscoveryLimits.MaxMachines) Seen.Clear();
        Seen.AddOrUpdate(machineId, at, (_, current) => at > current ? at : current);
    }

    /// <summary>The fresher of <paramref name="stored"/> and the remembered unwritten time for <paramref name="machineId"/>.</summary>
    public static DateTimeOffset? Freshest(string machineId, DateTimeOffset? stored) =>
        stored is { } persisted && Seen.TryGetValue(machineId, out var remembered) && remembered > persisted && remembered - persisted <= PersistEvery + TimeSpan.FromMinutes(1)
            ? remembered : stored;
}

/// <summary>
/// The machine-scoped discovery aggregate: enrolled machines, their hashed credentials, the chats each announced and a deny list.
/// It decides identity and wiring only. Its document is <see cref="DiscoveryState"/>; the envelope changes only when a mutation succeeds.
/// </summary>
public static class DiscoveryStateMachine
{
    public static RelayReply Apply(StateEnvelope<CoordinationDocument> envelope, RelayCaller caller, string operation, JsonObject args, DateTimeOffset now) =>
        Apply(envelope, caller, operation, args, now, out _);

    /// <param name="contactOnly">
    /// True when the operation was an announce that changed nothing but the machine's last-seen time: same name, clients, chats and node build. The caller
    /// may then keep the new time in memory without writing the whole document; see <c>DiscoveryGrain</c>.
    /// </param>
    public static RelayReply Apply(StateEnvelope<CoordinationDocument> envelope, RelayCaller caller, string operation, JsonObject args, DateTimeOffset now, out bool contactOnly)
    {
        contactOnly = false;
        if (ByteSize(args) > RelayLimits.MaxPayloadBytes) return Error(PayloadTooLarge);
        if (!caller.IsInternal) return Error(InternalOnly);
        var json = JsonNode.Parse(envelope.Value.Json)!.AsObject();
        var state = new DiscoveryState(json);
        RelayReply result;
        try
        {
            result = operation switch
            {
                DiscoveryOperations.Enroll => Enroll(state, args, now),
                DiscoveryOperations.Validate => Validate(state, args),
                DiscoveryOperations.Announce => Announce(state, args, now, out contactOnly),
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
        if (operation is DiscoveryOperations.Enroll or DiscoveryOperations.Announce or DiscoveryOperations.Revoke) envelope.Value = new() { Json = json.ToJsonString() };
        return result;
    }

    private static string PrincipalOf(string machineId) => "machine:" + machineId;

    private static RelayReply Enroll(DiscoveryState state, JsonObject args, DateTimeOffset now)
    {
        var id = Required(args, "machineId");
        var name = Required(args, "machineName");
        var hash = Required(args, "credentialHash");
        if (state.IsDenied(id)) return Error(MachineRevoked);
        // A machine's ID is its principal, which owns actions and sessions; reissuing it would hand those to whoever enrolled next.
        if (state.IsExpired(id)) return Error(MachineExpired);
        if (state.Machine(id) is { } existing)
        {
            if (!SecretEquals(existing.CredentialHash, hash)) return Error(AliasConflict);
            existing.Name = name;
        }
        else
        {
            if (state.MachineCount >= DiscoveryLimits.MaxMachines && !RemoveExpired(state, now)) return Error(CapacityExceeded);
            state.Enroll(id, name, hash, now);
        }
        return Ok(new { machineId = id, principalId = PrincipalOf(id) });
    }

    /// <summary>Frees one slot by removing the machine unseen longest, if unseen past the expiry, so strangers cannot hold the table forever.</summary>
    private static bool RemoveExpired(DiscoveryState state, DateTimeOffset now)
    {
        var cutoff = now - TimeSpan.FromDays(RelayLimits.MachineExpiryDays);
        // A machine that never announced counts from its enrollment; one from before enrolledAt existed counts as oldest.
        var oldest = state.EnrolledMachines().Select(x => (Key: x.Id, Seen: x.Machine.LastContact))
            .Where(x => x.Seen < cutoff).OrderBy(x => x.Seen).ThenBy(x => x.Key, StringComparer.Ordinal).FirstOrDefault();
        if (oldest.Key is null) return false;
        state.RemoveMachine(oldest.Key);
        if (state.ExpiredCount >= DiscoveryLimits.MaxExpiredIds && state.OldestExpired() is { } forgotten) state.ForgetExpired(forgotten);
        state.Expire(oldest.Key, now);
        return true;
    }

    private static RelayReply Validate(DiscoveryState state, JsonObject args)
    {
        var hash = Required(args, "credentialHash");
        var found = state.EnrolledMachines().FirstOrDefault(x => SecretEquals(x.Machine.CredentialHash, hash));
        return found.Id is null ? Error(NotFound) : Ok(new { machineId = found.Id, principalId = PrincipalOf(found.Id) });
    }

    /// <summary>The provider a node announces for a session it has not mapped to a chat.</summary>
    private const string UnmappedProvider = ChatProviders.Unknown;

    /// <summary>When the announced chat's session was opened, or the earliest time when absent or unreadable.</summary>
    private static DateTimeOffset OpenedAt(JsonObject chat) =>
        chat["openedAt"] is JsonValue value && value.TryGetValue<string>(out var text) && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var at) ? at : DateTimeOffset.MinValue;

    private static RelayReply Announce(DiscoveryState state, JsonObject args, DateTimeOffset now, out bool contactOnly)
    {
        contactOnly = false;
        var id = Required(args, "machineId");
        var name = Required(args, "machineName");
        var clients = StringList(args["clients"]).Select(x => x.Length is > 0 and <= RelayLimits.MaxIdentifierLength ? x : throw new ArgumentException("clients entries must be 1 to 256 characters.")).ToArray();
        var nodeBuild = ReadNodeBuild(args["nodeBuild"]);
        var source = args["chats"] is null ? new JsonArray() : args["chats"] as JsonArray ?? throw new ArgumentException("chats must be an array.");
        if (source.Count > DiscoveryLimits.MaxChatsPerMachine) return Error(CapacityExceeded);
        var parsed = new List<(AnnouncedChat Chat, DateTimeOffset OpenedAt, int Order)>();
        foreach (var node in source)
        {
            var chat = node as JsonObject ?? throw new ArgumentException("chats entries must be objects.");
            parsed.Add((AnnouncedChat.From(Required(chat, "provider"), Required(chat, "threadId"), Required(chat, "relaySessionId"), Required(chat, "repositoryId"), Required(chat, "label"),
                Flag(chat, "wakeable"), Flag(chat, "waiterRunning")), OpenedAt(chat), parsed.Count));
        }
        // One provider thread maps to one current relay session: keep only the newest chat per (provider, thread), so a stale node cannot announce a superseded route.
        // Newest is the latest openedAt; without one, the earlier entry wins (the node lists newest first). Unmapped chats share a placeholder thread and are never collapsed.
        var chats = parsed.GroupBy(x => x.Chat.Provider == UnmappedProvider ? "unmapped:" + x.Order : x.Chat.Provider.ToLowerInvariant() + "/" + x.Chat.ThreadId.ToLowerInvariant())
            .Select(group => group.OrderByDescending(x => x.OpenedAt).ThenBy(x => x.Order).First()).OrderBy(x => x.Order).Select(x => x.Chat).ToArray();
        if (state.Machine(id) is not { } machine) return Error(NotFound);
        var before = machine.WithoutLastSeen();
        machine.Announced(name, now, clients, chats, nodeBuild);
        contactOnly = JsonNode.DeepEquals(before, machine.WithoutLastSeen());
        var routes = chats.Where(x => x.Wakeable).Select(x => new { provider = x.Provider, threadId = x.ThreadId, relaySessionId = x.RelaySessionId, repositoryId = x.RepositoryId }).ToArray();
        return Ok(new { routes });
    }

    private static NodeBuildObservation? ReadNodeBuild(JsonNode? value)
    {
        if (value is null) return null;
        if (value is not JsonObject build) throw new ArgumentException("nodeBuild must be an object.");
        var version = Required(build, "productVersion");
        var content = Required(build, "contentId");
        var dll = Required(build, "adapterDllSha256");
        if (version.Any(char.IsControl) || content.Length != 64 || content.Any(c => !char.IsAsciiHexDigit(c)) ||
            dll.Length != 64 || dll.Any(c => !char.IsAsciiHexDigit(c)) ||
            build["processId"] is not JsonValue pid || !pid.TryGetValue<int>(out var processId) || processId <= 0)
            throw new ArgumentException("nodeBuild contains invalid build observations.");
        return new(version, content, dll, processId, ReadRefusal(build["lastRefusal"]));
    }

    /// <summary>
    /// The release a node last refused, when it sent one that is well formed: a release ID, a fixed reason code and a time. Anything else is dropped
    /// rather than failing the announcement, so an older or newer node never loses its chats over this report; nothing free-form is stored.
    /// </summary>
    private static NodeUpdateRefusal? ReadRefusal(JsonNode? value)
    {
        if (value is not JsonObject refusal) return null;
        var release = refusal["releaseId"] is JsonValue id && id.TryGetValue<string>(out var text) ? text : null;
        var reason = refusal["reason"] is JsonValue code && code.TryGetValue<string>(out var word) ? word : null;
        var at = refusal["atUtc"] is JsonValue when && when.TryGetValue<string>(out var stamp) &&
            DateTimeOffset.TryParse(stamp, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed) ? parsed : (DateTimeOffset?)null;
        if (release is not { Length: 64 } || release.Any(c => !(char.IsAsciiDigit(c) || c is >= 'A' and <= 'F')) || !NodeUpdateRefusals.IsCode(reason) || at is null) return null;
        return new(release, reason!, at.Value.ToUniversalTime());
    }

    /// <summary>The announced chats in the given repositories. Private thread IDs, credential hashes and credentials never leave the aggregate.</summary>
    private static RelayReply Directory(DiscoveryState state, JsonObject args, DateTimeOffset now)
    {
        var repositories = StringList(args["repositories"]).ToHashSet(StringComparer.Ordinal);
        var items = new List<object>();
        foreach (var (_, machine) in state.EnrolledMachines())
        {
            var lastSeen = machine.LastSeen;
            foreach (var chat in machine.Chats.Where(x => repositories.Contains(x.RepositoryId)))
            {
                var provider = chat.Provider;
                var reachable = ReadModels.IsReachable(provider, chat.Wakeable, chat.WaiterRunning, lastSeen, now);
                var nodeBuild = machine.NodeBuild is { } build ? new
                {
                    productVersion = build.ProductVersion, contentId = build.ContentId, adapterDllSha256 = build.AdapterDllSha256, processId = build.ProcessId,
                    lastRefusal = build.LastRefusal is { } refusal ? new { releaseId = refusal.ReleaseId, reason = refusal.Reason, atUtc = refusal.AtUtc } : null
                } : null;
                items.Add(new { label = chat.Label, provider, machineName = machine.Name, relaySessionId = chat.RelaySessionId, repositoryId = chat.RepositoryId, reachable, nodeBuild });
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

    private static RelayReply Revoke(DiscoveryState state, RelayCaller caller, JsonObject args)
    {
        if (!caller.IsAdministrator) return Error(AdministratorRequired);
        var id = Required(args, "machineId");
        if (!state.HasMachine(id) && !state.IsDenied(id)) return Error(NotFound);
        if (!state.IsDenied(id) && state.DeniedCount >= RelayLimits.MaxRecords) return Error(CapacityExceeded);
        state.RemoveMachine(id);
        state.Deny(id);
        return Ok(new { revoked = true });
    }
}
