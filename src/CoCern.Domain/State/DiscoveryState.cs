using System.Text.Json.Nodes;
using System.Text.Json;
using CoCern.Contracts;
using static CoCern.Domain.JsonFields;

namespace CoCern.Domain.State;

/// <summary>
/// The discovery aggregate's stored document, read and written through named, typed members only: the enrolled machines (an
/// <see cref="EnrolledMachine"/> per machine ID, with its hashed credential and the chats it last announced), the revoked machine IDs,
/// and the expired ones, kept with the Unix seconds they expired at so none is reissued. Every key it stores is declared in
/// <see cref="Shape"/>. Discovery is applied by <see cref="DiscoveryStateMachine"/>, not the repository-scoped state machine, so its
/// document has none of the keys every coordination aggregate keeps (<see cref="CommonState"/>). Like the handlers before it, a member
/// that reaches a map creates it when absent.
/// </summary>
public sealed class DiscoveryState(JsonObject json)
{
    public const string MachinesKey = "machines";
    public const string DeniedKey = "denied";
    public const string ExpiredKey = "expired";

    /// <summary>Every key discovery stores: machines, revoked IDs (each <c>true</c>) and expired IDs (each its expiry in Unix seconds), all by machine ID.</summary>
    public static StateShape Shape { get; } = StateShape.Object(
        (MachinesKey, StateShape.Map(EnrolledMachine.Shape)), (DeniedKey, StateShape.Map(null)), (ExpiredKey, StateShape.Map(null)));

    private JsonObject Machines => ObjectAt(json, MachinesKey);
    private JsonObject Denied => ObjectAt(json, DeniedKey);
    private JsonObject Expired => ObjectAt(json, ExpiredKey);

    public int MachineCount => Machines.Count;

    /// <summary>Every enrolled machine by ID, in the order enrolled.</summary>
    public IEnumerable<(string Id, EnrolledMachine Machine)> EnrolledMachines() =>
        Machines.Where(entry => entry.Value is JsonObject).Select(entry => (entry.Key, new EnrolledMachine(entry.Value!.AsObject())));

    public bool HasMachine(string machineId) => Machines.ContainsKey(machineId);

    /// <summary>The machine enrolled as <paramref name="machineId"/>, or null.</summary>
    public EnrolledMachine? Machine(string machineId) => Machines[machineId] is JsonObject machine ? new(machine) : null;

    /// <summary>Enrolls a new machine: named, with its credential's hash, enrolled now, never seen, with no clients or chats yet.</summary>
    public EnrolledMachine Enroll(string machineId, string name, string credentialHash, DateTimeOffset now)
    {
        var machine = new JsonObject
        {
            [EnrolledMachine.NameKey] = name, [EnrolledMachine.CredentialHashKey] = credentialHash, [EnrolledMachine.EnrolledAtKey] = now.ToString("O"),
            [EnrolledMachine.LastSeenKey] = null, [EnrolledMachine.ClientsKey] = new JsonArray(), [EnrolledMachine.ChatsKey] = new JsonArray()
        };
        Machines[machineId] = machine;
        return new(machine);
    }

    public void RemoveMachine(string machineId) => Machines.Remove(machineId);

    /// <summary>Whether <paramref name="machineId"/> was revoked; a revoked ID never enrolls again.</summary>
    public bool IsDenied(string machineId) => Denied.ContainsKey(machineId);

    public int DeniedCount => Denied.Count;

    public void Deny(string machineId) => Denied[machineId] = true;

    /// <summary>Whether <paramref name="machineId"/> expired; its ID is its principal, so it is never reissued.</summary>
    public bool IsExpired(string machineId) => Expired.ContainsKey(machineId);

    public int ExpiredCount => Expired.Count;

    /// <summary>The expired ID that expired first (by ID on a tie), or null when none has.</summary>
    public string? OldestExpired() =>
        Expired.OrderBy(entry => Number(Expired, entry.Key)).ThenBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => entry.Key).FirstOrDefault();

    public void ForgetExpired(string machineId) => Expired.Remove(machineId);

    /// <summary>Remembers <paramref name="machineId"/> as expired at <paramref name="now"/>.</summary>
    public void Expire(string machineId, DateTimeOffset now) => Expired[machineId] = now.ToUnixTimeSeconds();
}

/// <summary>
/// One enrolled machine: its name, its credential's hash (never the credential), when it enrolled and last announced, the clients it
/// runs, and the chats it last announced (<see cref="AnnouncedChat"/>).
/// </summary>
public sealed class EnrolledMachine(JsonObject json)
{
    public const string NameKey = "machineName";
    public const string CredentialHashKey = "credentialHash";
    public const string EnrolledAtKey = "enrolledAt";
    public const string LastSeenKey = "lastSeen";
    public const string ClientsKey = "clients";
    public const string ChatsKey = "chats";
    public const string NodeBuildKey = "nodeBuild";

    public static StateShape Shape { get; } = StateShape.Object(
        (NameKey, null), (CredentialHashKey, null), (EnrolledAtKey, null), (LastSeenKey, null), (ClientsKey, null), (ChatsKey, AnnouncedChat.Shape),
        (NodeBuildKey, StateShape.Object(("productVersion", null), ("contentId", null), ("adapterDllSha256", null), ("processId", null))));

    public string Name
    {
        get => Text(json, NameKey);
        set => json[NameKey] = value;
    }

    public string CredentialHash => Text(json, CredentialHashKey);

    /// <summary>When it enrolled; the earliest time for a machine enrolled before this was kept.</summary>
    public DateTimeOffset EnrolledAt => Date(json, EnrolledAtKey);

    /// <summary>When it last announced; null when it never has.</summary>
    public DateTimeOffset? LastSeen => OptionalDate(json, LastSeenKey);

    /// <summary>When the hub last heard from it: its last announcement, else its enrollment.</summary>
    public DateTimeOffset LastContact => LastSeen ?? EnrolledAt;

    public IReadOnlyList<string> Clients => Strings(json[ClientsKey]).ToArray();

    /// <summary>The chats it last announced.</summary>
    public IEnumerable<AnnouncedChat> Chats => (json[ChatsKey] as JsonArray ?? []).OfType<JsonObject>().Select(chat => new AnnouncedChat(chat));

    /// <summary>The last announcing node process, when observed; old or missing announcements remain unknown.</summary>
    public NodeBuildObservation? NodeBuild => json[NodeBuildKey]?.Deserialize<NodeBuildObservation>(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    /// <summary>Takes an announcement: the machine's name, that it was seen at <paramref name="now"/>, and the clients and chats it runs now.</summary>
    public void Announced(string name, DateTimeOffset now, IEnumerable<string> clients, IEnumerable<AnnouncedChat> chats, NodeBuildObservation? nodeBuild = null)
    {
        Name = name;
        json[LastSeenKey] = now.ToString("O");
        json[ClientsKey] = new JsonArray(clients.Select(client => (JsonNode)JsonValue.Create(client)!).ToArray());
        json[ChatsKey] = new JsonArray(chats.Select(chat => (JsonNode)chat.Json).ToArray());
        json[NodeBuildKey] = JsonSerializer.SerializeToNode(nodeBuild, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
}

/// <summary>
/// A chat a machine announced: its provider and thread, the relay session it is mapped to in a repository, its label, and whether it can
/// be woken now (<see cref="Wakeable"/> by its node, <see cref="WaiterRunning"/> by its own waiting inbox call).
/// </summary>
public sealed class AnnouncedChat(JsonObject json)
{
    public const string ProviderKey = "provider";
    public const string ThreadIdKey = "threadId";
    public const string RelaySessionIdKey = "relaySessionId";
    public const string RepositoryIdKey = "repositoryId";
    public const string LabelKey = "label";
    public const string WakeableKey = "wakeable";
    public const string WaiterRunningKey = "waiterRunning";

    public static StateShape Shape { get; } = StateShape.Object(
        (ProviderKey, null), (ThreadIdKey, null), (RelaySessionIdKey, null), (RepositoryIdKey, null), (LabelKey, null), (WakeableKey, null), (WaiterRunningKey, null));

    /// <summary>A chat from an announcement's checked fields.</summary>
    public static AnnouncedChat From(string provider, string threadId, string relaySessionId, string repositoryId, string label, bool wakeable, bool waiterRunning) => new(new JsonObject
    {
        [ProviderKey] = provider, [ThreadIdKey] = threadId, [RelaySessionIdKey] = relaySessionId, [RepositoryIdKey] = repositoryId, [LabelKey] = label,
        [WakeableKey] = wakeable, [WaiterRunningKey] = waiterRunning
    });

    internal JsonObject Json => json;

    public string Provider => Text(json, ProviderKey);
    public string ThreadId => Text(json, ThreadIdKey);
    public string RelaySessionId => Text(json, RelaySessionIdKey);
    public string RepositoryId => Text(json, RepositoryIdKey);
    public string Label => Text(json, LabelKey);

    /// <summary>Whether its node can wake it (a Codex chat). A stored value that is not a boolean is invalid.</summary>
    public bool Wakeable => Flag(json, WakeableKey);

    /// <summary>Whether its own waiting inbox call is running (a Claude chat). A stored value that is not a boolean is invalid.</summary>
    public bool WaiterRunning => Flag(json, WaiterRunningKey);
}
