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
        if (HubIncarnation is not null) target["hubIncarnation"] = HubIncarnation;
        if (MachineId is not null) target["machineId"] = MachineId;
        if (Label is not null) target["label"] = Label;
        if (Binding is not null) target["binding"] = Binding.DeepClone();
        if (BindingVersion is { } version) target["bindingVersion"] = version;
        if (ObservedAt is { } observed) target["observedAt"] = observed.ToString("O");
        if (FreshUntil is { } fresh) target["freshUntil"] = fresh.ToString("O");
        if (Availability is not null) target["availability"] = Availability;
    }

    /// <summary>Reads the presence fields <paramref name="read"/>'s object has, noting any of the wrong type.</summary>
    internal static SessionPresence Read(MessageReader read) => new()
    {
        HubIncarnation = read.OptionalText("hubIncarnation"), MachineId = read.OptionalText("machineId"), Label = read.OptionalText("label"), Binding = read.Node("binding"),
        BindingVersion = read.OptionalNumber("bindingVersion", minimum: 0), ObservedAt = read.OptionalDate("observedAt"), FreshUntil = read.OptionalDate("freshUntil"),
        Availability = read.OptionalText("availability")
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
        var json = new JsonObject { ["sessionId"] = SessionId, ["principalId"] = PrincipalId, ["repositoryId"] = RepositoryId, [ReadModels.SessionTokenKey] = SessionToken };
        if (Role is not null) json["role"] = Role;
        Presence.WriteTo(json);
        if (ActiveAt is { } active) json["activeAt"] = active.ToString("O");
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
        SessionId = read.Text("sessionId"), PrincipalId = read.Text("principalId"), RepositoryId = read.Text("repositoryId"), SessionToken = read.Text(ReadModels.SessionTokenKey),
        Role = read.OptionalText("role"), Presence = SessionPresence.Read(read), ActiveAt = read.OptionalDate("activeAt")
    };
}
