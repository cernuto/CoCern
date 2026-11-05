using System.Text.Json.Nodes;
using CoCern.Contracts;

namespace CoCern.Domain.Messages;

/// <summary>
/// <see cref="Operations.Resolve"/>: the registered resource that <see cref="ResourceId"/> names in this repository, by its own ID or as
/// an alias registered on <see cref="MachineId"/> (empty for none). Read-only. The hub sends it before every resource claim (with the
/// session's machine) and for an order's <c>claim_released</c> trigger. The name is the client's, so it decodes as given, empty or not,
/// and the directory refuses one that is empty or too long as invalid arguments, as it refuses any client's bad identifier.
/// </summary>
public sealed record ResourceResolve(string RepositoryId, string ResourceId, string MachineId = "") : IInternalRequest<ResourceResolve>
{
    public static string Operation => Operations.Resolve;
    public static string TargetKind => AggregateKinds.Control;

    public JsonObject ToJson() => new() { ["repositoryId"] = RepositoryId, ["resourceId"] = ResourceId, ["machineId"] = MachineId };

    public static Decoding<ResourceResolve> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        var repository = read.Text("repositoryId");
        var resource = read.OptionalText("resourceId");
        read.Check(resource is not null, "resourceId");
        return read.Result(new ResourceResolve(repository, resource ?? "", read.OptionalText("machineId") ?? ""));
    }
}

/// <summary>
/// <see cref="Operations.ResolveAll"/>: the canonical ID of each of <see cref="ResourceIds"/> this repository registered, by ID or as an
/// alias on <see cref="MachineId"/> (empty for none), in one read. The hub sends it for a task's declared resources and for the resources
/// a next-work offer's candidates list. The directory looks up at most <see cref="RelayLimits.MaxOfferResourceLookups"/> and leaves
/// unknown names out of its reply.
/// </summary>
public sealed record ResourceResolveAll(string RepositoryId, IReadOnlyList<string> ResourceIds, string MachineId = "") : IInternalRequest<ResourceResolveAll>
{
    public static string Operation => Operations.ResolveAll;
    public static string TargetKind => AggregateKinds.Control;

    public JsonObject ToJson() => new()
    {
        ["repositoryId"] = RepositoryId, ["machineId"] = MachineId, ["resourceIds"] = new JsonArray(ResourceIds.Select(id => (JsonNode)id).ToArray())
    };

    public static Decoding<ResourceResolveAll> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        return read.Result(new ResourceResolveAll(read.Text("repositoryId"), read.Strings("resourceIds"), read.OptionalText("machineId") ?? ""));
    }
}

/// <summary>
/// <see cref="Operations.SweepSessions"/>: the hub's periodic sweep closes sessions in every repository not seen for
/// <see cref="IdleHours"/>. The session cleanup service sends it every ten minutes, scoped to <see cref="EveryRepository"/> as an
/// administrator, so it passes the repository check for the whole directory. The directory refuses an <see cref="IdleHours"/> below 1 as
/// invalid arguments.
/// </summary>
public sealed record SessionSweep(long IdleHours, string RepositoryId = SessionSweep.EveryRepository) : IInternalRequest<SessionSweep>
{
    /// <summary>The scope the sweep is sent under: it spans every repository, so it names none.</summary>
    public const string EveryRepository = "*";

    public static string Operation => Operations.SweepSessions;
    public static string TargetKind => AggregateKinds.Control;

    public JsonObject ToJson() => new() { ["repositoryId"] = RepositoryId, ["idleHours"] = IdleHours };

    public static Decoding<SessionSweep> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        var repository = read.Text("repositoryId");
        return read.Result(new SessionSweep(read.Number("idleHours"), repository));
    }
}

/// <summary>
/// <see cref="Operations.SessionOwners"/>: which of <see cref="SessionIds"/> the calling principal opened in this repository. Read-only;
/// the reply never names another principal's sessions. The hub asks it as a machine's principal, to check that the chats a machine
/// routes wakes for are its own.
/// </summary>
public sealed record SessionOwnersQuery(string RepositoryId, IReadOnlyList<string> SessionIds) : IInternalRequest<SessionOwnersQuery>
{
    public static string Operation => Operations.SessionOwners;
    public static string TargetKind => AggregateKinds.Control;

    public JsonObject ToJson() => new() { ["repositoryId"] = RepositoryId, ["sessionIds"] = new JsonArray(SessionIds.Select(id => (JsonNode)id).ToArray()) };

    public static Decoding<SessionOwnersQuery> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        return read.Result(new SessionOwnersQuery(read.Text("repositoryId"), read.Strings("sessionIds")));
    }
}

/// <summary>
/// <see cref="Operations.SessionsOpen"/>: which of <see cref="SessionIds"/> are still open in this repository, whoever opened them.
/// Read-only; nothing else about them is returned. The hub asks it before listing announced chats, so a chat whose session closed since its
/// machine last announced is left out.
/// </summary>
public sealed record NativeFileScopeQuery(string RepositoryId, string SessionId, string SessionToken, string ParticipantId) : IInternalRequest<NativeFileScopeQuery>
{
    public static string Operation => Operations.NativeFileScope;
    public static string TargetKind => AggregateKinds.Control;
    public JsonObject ToJson() => new() { ["repositoryId"] = RepositoryId, ["sessionId"] = SessionId, ["sessionToken"] = SessionToken, ["participantId"] = ParticipantId };
    public static Decoding<NativeFileScopeQuery> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        return read.Result(new NativeFileScopeQuery(read.Text("repositoryId"), read.Text("sessionId"), read.Text("sessionToken"), read.Text("participantId")));
    }
}

public sealed record SessionsOpenQuery(string RepositoryId, IReadOnlyList<string> SessionIds) : IInternalRequest<SessionsOpenQuery>
{
    public static string Operation => Operations.SessionsOpen;
    public static string TargetKind => AggregateKinds.Control;

    public JsonObject ToJson() => new() { ["repositoryId"] = RepositoryId, ["sessionIds"] = new JsonArray(SessionIds.Select(id => (JsonNode)id).ToArray()) };

    public static Decoding<SessionsOpenQuery> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        return read.Result(new SessionsOpenQuery(read.Text("repositoryId"), read.Strings("sessionIds")));
    }
}
