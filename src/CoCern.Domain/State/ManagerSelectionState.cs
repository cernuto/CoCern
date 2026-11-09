using System.Text.Json.Nodes;
using static CoCern.Domain.JsonFields;

namespace CoCern.Domain.State;

/// <summary>Private durable assignment provenance, independent of history eviction. Never a public response.</summary>
public sealed class ManagerAssignment(JsonObject json)
{
    public static StateShape Shape { get; } = StateShape.Object(
        ("sessionId", null), ("principalId", null), ("repositoryId", null), ("generation", null),
        ("assignedBy", null), ("assignedAt", null), ("requestId", null), ("humanWords", null), ("nativeId", null));

    public string SessionId => Text(json, "sessionId");
    public string PrincipalId => Text(json, "principalId");
    public string RepositoryId => Text(json, "repositoryId");
    public long Generation => Number(json, "generation");
    public string AssignedBy => Text(json, "assignedBy");
    public DateTimeOffset AssignedAt => Date(json, "assignedAt");
    public string RequestId => Text(json, "requestId");
    public string HumanWords => Text(json, "humanWords");
    /// <summary>The native chat appointed, when it has one: the same chat keeps the role across a new hub session. Private, and absent for other clients.</summary>
    internal string NativeId => Text(json, "nativeId");

    internal JsonObject ToJson() => json.DeepClone().AsObject();

    internal static ManagerAssignment Assigned(DirectoryEntry target, long generation, string actor, DateTimeOffset at, string requestId, string words)
    {
        var assignment = new JsonObject
        {
            ["sessionId"] = target.SessionId, ["principalId"] = target.PrincipalId, ["repositoryId"] = target.RepositoryId,
            ["generation"] = generation, ["assignedBy"] = actor, ["assignedAt"] = at.ToString("O"), ["requestId"] = requestId, ["humanWords"] = words
        };
        if (target.NativeId.Length > 0) assignment["nativeId"] = target.NativeId;
        return new(assignment);
    }
}

/// <summary>
/// One repository's manager seats: the repository's own generation, the chats holding a seat there (at most
/// <see cref="CoCern.Contracts.RelayLimits.MaxManagersPerRepository"/>), and the former managers this repository, and no other, refuses.
/// </summary>
public sealed class ManagerSeat(JsonObject json)
{
    public const string GenerationKey = "generation";
    public const string ManagersKey = "managers";
    public const string RevokedKey = "revoked";

    public static StateShape Shape { get; } = StateShape.Object((GenerationKey, null), (ManagersKey, ManagerAssignment.Shape), (RevokedKey, StateShape.Map(null)));

    /// <summary>The repository's selection generation: it moves on every add and every removal there, and nowhere else.</summary>
    public long Generation
    {
        get => Number(json, GenerationKey);
        internal set => json[GenerationKey] = value;
    }

    /// <summary>The chats holding a seat, in the order added.</summary>
    public IReadOnlyList<ManagerAssignment> Managers => json[ManagersKey] is JsonArray list ? list.OfType<JsonObject>().Select(item => new ManagerAssignment(item)).ToArray() : [];

    internal void Add(ManagerAssignment manager) => ArrayAt(json, ManagersKey).Add(manager.ToJson());

    internal void Remove(string sessionId)
    {
        if (json[ManagersKey] is JsonArray list && list.OfType<JsonObject>().FirstOrDefault(item => Text(item, "sessionId") == sessionId) is { } held) list.Remove(held);
    }

    /// <summary>Whether a session or native participant ID held a seat here and was removed by someone else. Private; IDs here never leave the hub.</summary>
    internal bool Revoked(string id) => id.Length > 0 && json[RevokedKey] is JsonObject revoked && revoked.ContainsKey(id);

    /// <summary>Remembers <paramref name="manager"/>'s session and native participant as revoked here at <paramref name="generation"/>; past the bound the oldest goes.</summary>
    internal void Revoke(ManagerAssignment manager, long generation)
    {
        var revoked = ObjectAt(json, RevokedKey);
        revoked[manager.SessionId] = generation;
        if (manager.NativeId.Length > 0) revoked[manager.NativeId] = generation;
        while (revoked.Count > CoCern.Contracts.RelayLimits.ManagerRevokedEntries) revoked.Remove(revoked.OrderBy(entry => Number(revoked, entry.Key)).First().Key);
    }

    /// <summary>A chat added here again is no longer a former manager here.</summary>
    internal void Restore(DirectoryEntry target)
    {
        if (json[RevokedKey] is not JsonObject revoked) return;
        revoked.Remove(target.SessionId);
        if (target.NativeId.Length > 0) revoked.Remove(target.NativeId);
    }
}

internal static class ManagerSelectionHistoryState
{
    internal static StateShape Shape { get; } = StateShape.Object(
        ("generation", null), ("command", null), ("previousSessionId", null), ("sessionId", null), ("repositoryId", null),
        ("actorPrincipalId", null), ("requestId", null), ("at", null), ("humanWords", null), ("recordedBySessionId", null));

    internal static StateShape AdminActionShape { get; } = StateShape.Object(
        ("seq", null), ("at", null), ("actorPrincipalId", null), ("actingSessionId", null), ("managerGeneration", null), ("tool", null), ("command", null), ("target", null),
        ("repositoryId", null), ("requestId", null), ("code", null), ("succeeded", null), ("decisionRef", null));
}
