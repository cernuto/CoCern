using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain.Messages;
using static CoCern.Domain.JsonFields;

namespace CoCern.Domain.State;

/// <summary>
/// The control aggregate's stored document, read and written through named, typed members only: the session directory (a
/// <see cref="DirectoryEntry"/> per session), the fences of closes still waiting for their session's answer, the registered resources and
/// their machine-scoped aliases (<see cref="ResourceRegistry"/>), whether dispatch is paused, and the cancellation requests. Every key it
/// stores is declared in <see cref="Shape"/>. Like the handlers before it, a member that reaches a map creates it when absent.
/// </summary>
public sealed class ControlState(JsonObject json)
{
    public const string SessionsKey = "sessions";
    public const string ClosingKey = "closing";
    public const string CloseSeqKey = "closeSeq";
    public const string ResourcesKey = "resources";
    public const string AliasesKey = "aliases";
    public const string PausedKey = "paused";
    public const string CancellationsKey = "cancellations";

    /// <summary>Every key the directory stores: its entries by session ID, close fences by session ID, resources by ID, aliases by machine and name, and cancellations by session and action.</summary>
    public static StateShape Shape { get; } = CommonState.Shape.With(
        (SessionsKey, StateShape.Map(DirectoryEntry.Shape)), (ClosingKey, StateShape.Map(null)), (CloseSeqKey, null),
        (ResourcesKey, StateShape.Map(RegisteredResource.Shape)), (AliasesKey, StateShape.Map(null)),
        (PausedKey, null), (CancellationsKey, StateShape.Map(Cancellation.Shape)));

    private JsonObject Sessions => ObjectAt(json, SessionsKey);

    /// <summary>How many sessions the directory lists.</summary>
    public int SessionCount => Sessions.Count;

    /// <summary>Every listed session by ID, in the order listed. An entry that is not an object reads as null.</summary>
    public IEnumerable<(string Id, DirectoryEntry? Entry)> Entries() =>
        Sessions.Select(entry => (entry.Key, entry.Value is JsonObject record ? new DirectoryEntry(record) : null));

    /// <summary>The entry for <paramref name="sessionId"/>, or null when the directory does not list it.</summary>
    public DirectoryEntry? Entry(string sessionId) => Sessions[sessionId] is JsonObject record ? new(record) : null;

    /// <summary>Lists <paramref name="record"/> under its session ID, replacing any entry there, and returns the new entry.</summary>
    public DirectoryEntry Enter(SessionRecord record)
    {
        var entry = record.ToJson();
        Sessions[record.SessionId] = entry;
        return new(entry);
    }

    /// <summary>Drops <paramref name="sessionId"/> from the directory.</summary>
    public void Leave(string sessionId) => Sessions.Remove(sessionId);

    /// <summary>
    /// Fences one close of <paramref name="sessionId"/>: returns its new <c>closeSeq</c>, which the session's one answer carries back, a
    /// restore or an acknowledgement. Either settles the fence (<see cref="Unfence"/>). A fence lives only while its close is in flight, so
    /// the directory keeps none for sessions long closed; past <see cref="RelayLimits.MaxRecords"/> in flight the oldest is dropped, and its
    /// late answer is then ignored.
    /// </summary>
    public long Fence(string sessionId)
    {
        var seq = Number(json, CloseSeqKey) + 1;
        json[CloseSeqKey] = seq;
        var closing = ObjectAt(json, ClosingKey);
        closing.Remove(sessionId);
        closing[sessionId] = seq;
        while (closing.Count > RelayLimits.MaxRecords) closing.Remove(closing.First().Key);
        return seq;
    }

    /// <summary>Whether <paramref name="closeSeq"/> answers the close of <paramref name="sessionId"/> still in flight.</summary>
    public bool Fenced(string sessionId, long closeSeq) =>
        json[ClosingKey] is JsonObject closing && closing[sessionId] is JsonValue fence && fence.TryGetValue<long>(out var seq) && closeSeq == seq;

    /// <summary>Settles the close of <paramref name="sessionId"/> in flight, if any.</summary>
    public void Unfence(string sessionId)
    {
        if (json[ClosingKey] is not JsonObject closing) return;
        closing.Remove(sessionId);
        if (closing.Count == 0) json.Remove(ClosingKey);
    }

    /// <summary>The registered resources and the machine-scoped aliases that name them.</summary>
    public ResourceRegistry Resources => new(ObjectAt(json, ResourcesKey), ObjectAt(json, AliasesKey));

    /// <summary>Whether dispatch is paused (<c>pause</c> or <c>stop</c>, until <c>resume</c>). A stored value that is not a boolean is invalid.</summary>
    public bool Paused
    {
        get => Flag(json, PausedKey);
        set => json[PausedKey] = value;
    }

    private JsonObject Cancellations => ObjectAt(json, CancellationsKey);

    /// <summary>How many cancellation requests the directory holds.</summary>
    public int CancellationCount => Cancellations.Count;

    /// <summary>The cancellation requests for <paramref name="sessionId"/>'s actions, each by its target (session and action) and as stored.</summary>
    public IEnumerable<(string Target, JsonNode? Request)> CancellationsOf(string sessionId) =>
        Cancellations.Where(entry => entry.Key.StartsWith(sessionId + ":", StringComparison.Ordinal)).Select(entry => (entry.Key, entry.Value));

    /// <summary>Records that <paramref name="requestedBy"/> asked, at <paramref name="at"/>, to cancel action <paramref name="actionId"/> of <paramref name="sessionId"/>.</summary>
    public void RequestCancellation(string sessionId, string actionId, string requestedBy, DateTimeOffset at) =>
        Cancellations[sessionId + ":" + actionId] = Cancellation.Requested(requestedBy, at);
}

/// <summary>
/// One session as the directory lists it: the <see cref="SessionRecord"/> fields under the names the record writes, with the activity
/// the session last reported (<see cref="ActiveAt"/>), and the directory's own marks: whether the session answers for unresolved work
/// (<see cref="OwnsWorkAt"/>, ordered by <see cref="WorkSeq"/>) and since when it holds unread mail (<see cref="MailHeldAt"/>).
/// </summary>
public sealed class DirectoryEntry(JsonObject json)
{
    // The record's fields, as SessionRecord and SessionPresence write them.
    public const string SessionIdKey = "sessionId";
    public const string PrincipalIdKey = "principalId";
    public const string RepositoryIdKey = "repositoryId";
    public const string SessionTokenKey = ReadModels.SessionTokenKey;
    public const string RoleKey = "role";
    public const string HubIncarnationKey = "hubIncarnation";
    public const string MachineIdKey = "machineId";
    public const string LabelKey = "label";
    public const string BindingKey = "binding";
    public const string BindingVersionKey = "bindingVersion";
    public const string ObservedAtKey = "observedAt";
    public const string FreshUntilKey = "freshUntil";
    public const string AvailabilityKey = "availability";
    public const string ActiveAtKey = "activeAt";

    // The directory's own marks.
    public const string OwnsWorkKey = "ownsWork";
    public const string WorkSeqKey = "ownsWorkSeq";
    public const string MailHeldAtKey = "mailHeldAt";

    /// <summary>Every key an entry stores. The binding is the client's own, so its keys are not the directory's to name.</summary>
    public static StateShape Shape { get; } = StateShape.Object(
        (SessionIdKey, null), (PrincipalIdKey, null), (RepositoryIdKey, null), (SessionTokenKey, null), (RoleKey, null), (HubIncarnationKey, null), (MachineIdKey, null),
        (LabelKey, null), (BindingKey, StateShape.Any), (BindingVersionKey, null), (ObservedAtKey, null), (FreshUntilKey, null), (AvailabilityKey, null), (ActiveAtKey, null),
        (OwnsWorkKey, null), (WorkSeqKey, null), (MailHeldAtKey, null));

    /// <summary>The keys that never leave the directory in a worker listing: the secret, the principal and the directory's own marks.</summary>
    private static readonly string[] Private = [SessionTokenKey, PrincipalIdKey, OwnsWorkKey, WorkSeqKey, MailHeldAtKey];

    public string SessionId => Text(json, SessionIdKey);
    public string PrincipalId => Text(json, PrincipalIdKey);
    public string RepositoryId => Text(json, RepositoryIdKey);

    /// <summary>The role given at <c>session_open</c>, which routes manager-bound attention; empty when none.</summary>
    public string Role => Text(json, RoleKey);

    public string Label => Text(json, LabelKey);
    public long BindingVersion => Number(json, BindingVersionKey);
    public DateTimeOffset ObservedAt => Date(json, ObservedAtKey);
    public DateTimeOffset FreshUntil => Date(json, FreshUntilKey);

    /// <summary>The last activity the session reported, or the earliest time when it reported none.</summary>
    public DateTimeOffset ActiveAt
    {
        get => Date(json, ActiveAtKey);
        set => json[ActiveAtKey] = value.ToString("O");
    }

    /// <summary>When the directory last heard of the session: the latest of its freshness, its reported activity and its last observation.</summary>
    public DateTimeOffset LastSeen => new[] { FreshUntil, ActiveAt, ObservedAt }.Max();

    /// <summary>
    /// Takes the session's registered <paramref name="presence"/>: each field it has replaces the entry's, except that its times
    /// (observed, fresh until) only ever move forward.
    /// </summary>
    public void Register(SessionPresence presence)
    {
        var reported = new JsonObject();
        presence.WriteTo(reported);
        foreach (var (property, value) in reported)
            if (property is not (ObservedAtKey or FreshUntilKey) || Date(reported, property) > Date(json, property)) json[property] = value?.DeepClone();
    }

    /// <summary>When the session last said it answers for unresolved work, in Unix seconds; null when it does not.</summary>
    public long? OwnsWorkAt
    {
        get => json[OwnsWorkKey] is JsonValue value && value.TryGetValue<long>(out var seconds) ? seconds : null;
        set
        {
            if (value is { } seconds) json[OwnsWorkKey] = seconds;
            else json.Remove(OwnsWorkKey);
        }
    }

    /// <summary>Whether the session answers for an unresolved action: it reported so within <see cref="RelayLimits.SessionWorkRetentionDays"/>.</summary>
    public bool OwnsWork(DateTimeOffset now) => WorkLedger.Seconds(json[OwnsWorkKey]) > WorkLedger.Cutoff(now);

    /// <summary>The number of the session's last ownership report applied; an older or repeated one is stale.</summary>
    public long WorkSeq
    {
        get => Number(json, WorkSeqKey);
        set => json[WorkSeqKey] = value;
    }

    /// <summary>When the session refused an idle close because it had unread mail; null when it never did.</summary>
    public DateTimeOffset? MailHeldAt
    {
        get => OptionalDate(json, MailHeldAtKey);
        set
        {
            if (value is { } held) json[MailHeldAtKey] = held.ToString("O");
            else json.Remove(MailHeldAtKey);
        }
    }

    /// <summary>
    /// The entry as <c>worker_list</c> shows it: its record without the secret, the principal, the directory's own marks or the hub
    /// incarnation, with <c>stale</c> when it is from another incarnation than <paramref name="hubIncarnation"/> or past its freshness, in
    /// which case its availability reads <c>unknown</c>.
    /// </summary>
    public JsonObject Worker(DateTimeOffset now, string hubIncarnation)
    {
        var copy = json.DeepClone().AsObject();
        foreach (var key in Private) copy.Remove(key);
        var stale = Date(copy, FreshUntilKey) <= now || Text(copy, HubIncarnationKey) != hubIncarnation;
        copy["stale"] = stale; copy.Remove(HubIncarnationKey);
        if (stale) copy[AvailabilityKey] = "unknown";
        return copy;
    }
}

/// <summary>
/// The registered resources (keyed by canonical ID) and the aliases that name them (keyed by machine and name, both compared ignoring
/// case), as one registry: a name means the resource with that ID, else the one its machine registered it as an alias for.
/// </summary>
public sealed class ResourceRegistry(JsonObject resources, JsonObject aliases)
{
    /// <summary>The resource registered under <paramref name="resourceId"/>, in whatever repository; null when none is.</summary>
    public RegisteredResource? Resource(string resourceId) => resources[resourceId] is JsonObject resource ? new(resource) : null;

    /// <summary>The canonical ID <paramref name="name"/> is an alias for on <paramref name="machineId"/>, or null.</summary>
    public string? Alias(string machineId, string name) => aliases[AliasKey(machineId, name)] is JsonValue alias ? alias.GetValue<string>() : null;

    public void SetAlias(string machineId, string name, string resourceId) => aliases[AliasKey(machineId, name)] = resourceId;

    /// <summary>
    /// The resource <paramref name="name"/> stands for in <paramref name="repositoryId"/>: the one registered under that ID, else the one it
    /// is an alias for on <paramref name="machineId"/>; null when neither is registered in that repository.
    /// </summary>
    public RegisteredResource? Find(string repositoryId, string machineId, string name)
    {
        var found = Resource(name);
        if (found is null && Alias(machineId, name) is { } canonical) found = Resource(canonical);
        return found is null || found.RepositoryId != repositoryId ? null : found;
    }

    /// <summary>Registers <paramref name="resourceId"/> for <paramref name="repositoryId"/> on <paramref name="machineId"/>, with every name it answers to.</summary>
    public void Register(string resourceId, string repositoryId, string machineId, IReadOnlyList<string> names) => resources[resourceId] = new JsonObject
    {
        [RegisteredResource.ResourceIdKey] = resourceId, [RegisteredResource.RepositoryIdKey] = repositoryId, [RegisteredResource.MachineIdKey] = machineId,
        [RegisteredResource.AliasesKey] = JsonSerializer.SerializeToNode(names)
    };

    private static string AliasKey(string machine, string name) => machine.ToUpperInvariant() + ":" + name.ToUpperInvariant();
}

/// <summary>One registered resource: its canonical ID, the repository and machine that registered it, and every name it answers to there.</summary>
public sealed class RegisteredResource(JsonObject json)
{
    public const string ResourceIdKey = "resourceId";
    public const string RepositoryIdKey = "repositoryId";
    public const string MachineIdKey = "machineId";
    public const string AliasesKey = "aliases";

    public static StateShape Shape { get; } = StateShape.Object((ResourceIdKey, null), (RepositoryIdKey, null), (MachineIdKey, null), (AliasesKey, null));

    public string ResourceId => Text(json, ResourceIdKey);
    public string RepositoryId => Text(json, RepositoryIdKey);
    public string MachineId => Text(json, MachineIdKey);
    public IReadOnlyList<string> Aliases => Strings(json[AliasesKey]).ToArray();

    /// <summary>The resource as stored, which is what resolving it replies.</summary>
    public JsonObject ToReply() => json.DeepClone().AsObject();
}

/// <summary>An administrator's request to cancel one action, by whom and when; its status stays unknown, since the hub cannot stop an action itself.</summary>
public sealed class Cancellation(JsonObject json)
{
    public const string RequestedByKey = "requestedBy";
    public const string AtKey = "at";
    public const string StatusKey = "status";

    /// <summary>The only status a request has: asked for, outcome unknown.</summary>
    public const string RequestedUnknown = "requested_unknown";

    public static StateShape Shape { get; } = StateShape.Object((RequestedByKey, null), (AtKey, null), (StatusKey, null));

    public string RequestedBy => Text(json, RequestedByKey);
    public DateTimeOffset At => Date(json, AtKey);
    public string Status => Text(json, StatusKey);

    /// <summary>A new request. Its time is written as the serializer writes a time, as requests always were.</summary>
    internal static JsonObject Requested(string requestedBy, DateTimeOffset at) =>
        new() { [RequestedByKey] = requestedBy, [AtKey] = JsonSerializer.SerializeToNode(at), [StatusKey] = RequestedUnknown };
}
