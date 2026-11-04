using System.Text.Json.Nodes;
using CoCern.Contracts;

namespace CoCern.Domain.Messages;

/// <summary>
/// What a session reports about itself: its client's hub incarnation, machine, label and tool binding, and how fresh that is. The session
/// keeps it, and the control directory keeps the copy the session last registered (<see cref="SessionRegistration"/>). Nothing else of a
/// session's own state ever leaves it.
/// </summary>
public sealed record SessionPresence
{
    public const string HubIncarnationKey = "hubIncarnation";
    public const string MachineIdKey = "machineId";
    public const string LabelKey = "label";
    public const string BindingKey = "binding";
    public const string BindingVersionKey = "bindingVersion";
    public const string ObservedAtKey = "observedAt";
    public const string FreshUntilKey = "freshUntil";
    public const string AvailabilityKey = "availability";

    public string? HubIncarnation { get; init; }
    public string? MachineId { get; init; }
    public string? Label { get; init; }

    /// <summary>The tool binding the client reported, as it gave it; null when it reported none.</summary>
    public JsonNode? Binding { get; init; }

    public long? BindingVersion { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public DateTimeOffset? FreshUntil { get; init; }
    public string? Availability { get; init; }

    /// <summary>Writes each field this presence has into <paramref name="target"/>.</summary>
    internal void WriteTo(JsonObject target)
    {
        if (HubIncarnation is not null) target[HubIncarnationKey] = HubIncarnation;
        if (MachineId is not null) target[MachineIdKey] = MachineId;
        if (Label is not null) target[LabelKey] = Label;
        if (Binding is not null) target[BindingKey] = Binding.DeepClone();
        if (BindingVersion is { } version) target[BindingVersionKey] = version;
        if (ObservedAt is { } observed) target[ObservedAtKey] = observed.ToString("O");
        if (FreshUntil is { } fresh) target[FreshUntilKey] = fresh.ToString("O");
        if (Availability is not null) target[AvailabilityKey] = Availability;
    }

    /// <summary>Reads the presence fields <paramref name="read"/>'s object has, noting any of the wrong type.</summary>
    internal static SessionPresence Read(MessageReader read) => new()
    {
        HubIncarnation = read.OptionalText(HubIncarnationKey), MachineId = read.OptionalText(MachineIdKey), Label = read.OptionalText(LabelKey), Binding = read.Node(BindingKey),
        BindingVersion = read.OptionalNumber(BindingVersionKey, minimum: 0), ObservedAt = read.OptionalDate(ObservedAtKey), FreshUntil = read.OptionalDate(FreshUntilKey),
        Availability = read.OptionalText(AvailabilityKey)
    };
}

/// <summary>
/// A session as the hub records it: who opened it and its secret, the role it was opened with, its presence, and (in the directory) when
/// it last reported activity. The control directory's entry, the session's own copy, and the messages that carry one between them
/// (<see cref="SessionOpen"/>, <see cref="SessionRestore"/>, and the presence in <see cref="SessionRegistration"/>) all read and write this
/// one field set, so none of them can carry a field the others drop.
/// </summary>
public sealed record SessionRecord
{
    public const string SessionIdKey = "sessionId";
    public const string PrincipalIdKey = "principalId";
    public const string RepositoryIdKey = "repositoryId";
    public const string SessionTokenKey = ReadModels.SessionTokenKey;
    public const string RoleKey = "role";
    public const string ActiveAtKey = "activeAt";

    public string? NativeId { get; init; }
    public string? NativeGeneration { get; init; }
    public required string SessionId { get; init; }
    public required string PrincipalId { get; init; }
    public required string RepositoryId { get; init; }
    public required string SessionToken { get; init; }

    /// <summary>
    /// The role given at <c>session_open</c>: it routes manager-bound attention and grants nothing, and it never changes. One longer than
    /// <see cref="RelayLimits.MaxActivityRoleLength"/> is never recorded.
    /// </summary>
    public string? Role { get; init; }

    public SessionPresence Presence { get; init; } = new();

    /// <summary>The last activity the session reported, as the directory keeps it (<c>activeAt</c>); a session's own copy has none.</summary>
    public DateTimeOffset? ActiveAt { get; init; }

    /// <summary>
    /// The record <c>session_open</c> makes from its client's arguments: only the client's own fields (hub incarnation, machine, label,
    /// binding, and a role within its bound), never freshness or activity, which only the hub sets. The control directory adds the initial
    /// freshness (<see cref="WithInitialFreshness"/>); the hub's direct initialization of the session sends this as it is, and the session
    /// starts from the same defaults.
    /// </summary>
    public static SessionRecord Opened(string sessionId, string sessionToken, string principalId, string repositoryId, JsonObject client) => new()
    {
        SessionId = sessionId, SessionToken = sessionToken, PrincipalId = principalId, RepositoryId = repositoryId,
        NativeId = client[NativeParticipant.Id]?.GetValue<string>(),
        NativeGeneration = client[NativeParticipant.Generation]?.GetValue<string>(),
        Role = JsonFields.Text(client, "role") is { Length: > 0 and <= RelayLimits.MaxActivityRoleLength } role ? role : null,
        Presence = new()
        {
            HubIncarnation = JsonFields.Text(client, "hubIncarnation"), MachineId = JsonFields.Text(client, "machineId"), Label = JsonFields.Text(client, "label"),
            Binding = client["binding"]?.DeepClone()
        }
    };

    /// <summary>A just-opened session's freshness: binding version 0, observed now and fresh for a short while, availability unknown.</summary>
    public SessionRecord WithInitialFreshness(DateTimeOffset now) => this with
    {
        Presence = Presence with { BindingVersion = 0, ObservedAt = now, FreshUntil = now.Add(ControlAggregate.InitialFreshness), Availability = "unknown" }
    };

    public JsonObject ToJson()
    {
        var json = new JsonObject { [SessionIdKey] = SessionId, [PrincipalIdKey] = PrincipalId, [RepositoryIdKey] = RepositoryId, [SessionTokenKey] = SessionToken };
        if (NativeId is not null) json[NativeParticipant.Id] = NativeId;
        if (NativeGeneration is not null) json[NativeParticipant.Generation] = NativeGeneration;
        if (Role is not null) json[RoleKey] = Role;
        Presence.WriteTo(json);
        if (ActiveAt is { } active) json[ActiveAtKey] = active.ToString("O");
        return json;
    }

    /// <summary>Reads a record (a message's, or a session's own state), naming each missing or invalid field.</summary>
    public static Decoding<SessionRecord> Decode(JsonObject json)
    {
        var read = new MessageReader(json);
        var record = Read(read);
        return read.Result(record);
    }

    internal static SessionRecord Read(MessageReader read) => new()
    {
        SessionId = read.Text(SessionIdKey), PrincipalId = read.Text(PrincipalIdKey), RepositoryId = read.Text(RepositoryIdKey), SessionToken = read.Text(SessionTokenKey),
        NativeId = read.OptionalText(NativeParticipant.Id), NativeGeneration = read.OptionalText(NativeParticipant.Generation),
        Role = read.OptionalText(RoleKey), Presence = SessionPresence.Read(read), ActiveAt = read.OptionalDate(ActiveAtKey)
    };
}
