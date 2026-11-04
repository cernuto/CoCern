using System.Text.Json.Nodes;
using static CoCern.Domain.JsonFields;

namespace CoCern.Domain.State;

/// <summary>Private durable assignment provenance, independent of history eviction. Never a public response.</summary>
public sealed class ManagerAssignment(JsonObject json)
{
    public static StateShape Shape { get; } = StateShape.Object(
        ("sessionId", null), ("principalId", null), ("repositoryId", null), ("generation", null),
        ("assignedBy", null), ("assignedAt", null), ("requestId", null), ("humanWords", null));

    public string SessionId => Text(json, "sessionId");
    public string PrincipalId => Text(json, "principalId");
    public string RepositoryId => Text(json, "repositoryId");
    public long Generation => Number(json, "generation");
    public string AssignedBy => Text(json, "assignedBy");
    public DateTimeOffset AssignedAt => Date(json, "assignedAt");
    public string RequestId => Text(json, "requestId");
    public string HumanWords => Text(json, "humanWords");

    internal JsonObject ToJson() => json.DeepClone().AsObject();

    internal static ManagerAssignment Assigned(DirectoryEntry target, long generation, string actor, DateTimeOffset at, string requestId, string words) => new(new()
    {
        ["sessionId"] = target.SessionId, ["principalId"] = target.PrincipalId, ["repositoryId"] = target.RepositoryId,
        ["generation"] = generation, ["assignedBy"] = actor, ["assignedAt"] = at.ToString("O"), ["requestId"] = requestId, ["humanWords"] = words
    });
}

internal static class ManagerSelectionHistoryState
{
    internal static StateShape Shape { get; } = StateShape.Object(
        ("generation", null), ("command", null), ("previousSessionId", null), ("sessionId", null), ("repositoryId", null),
        ("actorPrincipalId", null), ("requestId", null), ("at", null), ("humanWords", null));
}
