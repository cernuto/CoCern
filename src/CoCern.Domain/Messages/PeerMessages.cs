using System.Text.Json.Nodes;
using CoCern.Contracts;
using static CoCern.Domain.JsonFields;

namespace CoCern.Domain.Messages;

/// <summary>
/// <see cref="Operations.PeerResolve"/>: which one chat <see cref="Name"/> (or <see cref="Ref"/>, from an earlier choice) means in this
/// repository, asked for a verified native caller. Read-only and serialized with the directory. A label only nominates a candidate, so it can
/// cause a refusal but never a grant. No public tool sends this: <c>peers</c> and later consumers project its answer by allow-list.
/// </summary>
public sealed record PeerResolve(string RepositoryId, string CallerParticipantId, string CallerSessionId, string Name = "", string Ref = "") : IInternalRequest<PeerResolve>
{
    public static string Operation => Operations.PeerResolve;
    public static string TargetKind => AggregateKinds.Control;

    public JsonObject ToJson() => new()
    {
        ["repositoryId"] = RepositoryId, ["callerParticipantId"] = CallerParticipantId, ["callerSessionId"] = CallerSessionId, ["name"] = Name, ["ref"] = Ref
    };

    public static Decoding<PeerResolve> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        var (repository, participant, session) = (read.Text("repositoryId"), read.Text("callerParticipantId"), read.Text("callerSessionId"));
        var (name, reference) = (read.OptionalText("name") ?? "", read.OptionalText("ref") ?? "");
        // Exactly one of the two says what is being looked up.
        read.Check(string.IsNullOrWhiteSpace(name) != string.IsNullOrWhiteSpace(reference), "name");
        return read.Result(new PeerResolve(repository, participant, session, name, reference));
    }
}

/// <summary><see cref="Operations.PeerList"/>: this repository's chats, as a verified native caller may see them. Read-only and bounded.</summary>
public sealed record PeerList(string RepositoryId, string CallerParticipantId, string CallerSessionId) : IInternalRequest<PeerList>
{
    public static string Operation => Operations.PeerList;
    public static string TargetKind => AggregateKinds.Control;

    public JsonObject ToJson() => new() { ["repositoryId"] = RepositoryId, ["callerParticipantId"] = CallerParticipantId, ["callerSessionId"] = CallerSessionId };

    public static Decoding<PeerList> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        return read.Result(new PeerList(read.Text("repositoryId"), read.Text("callerParticipantId"), read.Text("callerSessionId")));
    }
}

/// <summary>Internal authenticated lookup of one current native session.</summary>
public sealed record NativeCurrentQuery(string RepositoryId, string SessionId, string SessionToken, string ParticipantId) : IInternalRequest<NativeCurrentQuery>
{
    public static string Operation => Operations.NativeCurrent;
    public static string TargetKind => AggregateKinds.Control;
    public JsonObject ToJson() => new() { ["repositoryId"] = RepositoryId, ["sessionId"] = SessionId, ["sessionToken"] = SessionToken, ["participantId"] = ParticipantId };
    public static Decoding<NativeCurrentQuery> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        return read.Result(new NativeCurrentQuery(read.Text("repositoryId"), read.Text("sessionId"), read.Text("sessionToken"), read.Text("participantId")));
    }
}

/// <summary>
/// One chat as the directory tells the hub about it. <see cref="SessionId"/> is the row's current session: the hub joins it to wake routes, and
/// it is a choice's <c>ref</c>. This record holds no participant ID, so no projection of it can show one.
/// </summary>
public sealed record PeerRow(string SessionId, string Name, string Client, string MachineId, bool Verified, bool Self)
{
    public JsonObject ToJson() => new()
    {
        ["sessionId"] = SessionId, ["name"] = Name, ["client"] = Client, ["machineId"] = MachineId, ["verified"] = Verified, ["self"] = Self
    };

    public static PeerRow Parse(JsonObject json) =>
        new(Text(json, "sessionId"), Text(json, "name"), Text(json, "client"), Text(json, "machineId"), IsTrue(json, "verified"), IsTrue(json, "self"));

    internal static IReadOnlyList<PeerRow> ParseAll(JsonNode? rows) => (rows as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(Parse).ToArray();

    internal static JsonArray ToArray(IEnumerable<PeerRow> rows) => new(rows.Select(row => (JsonNode?)row.ToJson()).ToArray());
}

/// <summary>What the directory answers a <see cref="PeerList"/>.</summary>
public sealed record PeerListing(IReadOnlyList<PeerRow> Rows, bool Truncated)
{
    public JsonObject ToJson() => new() { ["rows"] = PeerRow.ToArray(Rows), ["truncated"] = Truncated };

    public static PeerListing Parse(JsonObject json) => new(PeerRow.ParseAll(json["rows"]), IsTrue(json, "truncated"));
}

public enum PeerOutcome { Resolved, Ambiguous, None }

/// <summary>Why a lookup named nobody the caller can act on.</summary>
public static class PeerRefusals
{
    /// <summary>No row: the same for a name nobody has, a closed chat and another repository's chat.</summary>
    public const string Unknown = "unknown";
    /// <summary>A close or a duplicate is in flight for the one match; retry.</summary>
    public const string Replacing = "replacing";
    /// <summary>The one match is a legacy row with no hub-proven identity.</summary>
    public const string Unverified = "unverified";
}

/// <summary>
/// What the directory answers a <see cref="PeerResolve"/>. <see cref="Participant"/> is set only for <see cref="PeerOutcome.Resolved"/> and stays
/// on the hub: a public reply is built from <see cref="Peer"/> and <see cref="Choices"/> alone.
/// </summary>
public sealed record PeerResolution(PeerOutcome Outcome, VerifiedNativeParticipant? Participant = null, PeerRow? Peer = null, IReadOnlyList<PeerRow>? Choices = null, bool Truncated = false, string Reason = "")
{
    private const string ParticipantKey = "participantId";

    public JsonObject ToJson()
    {
        var json = new JsonObject { ["outcome"] = Outcome.ToString(), ["truncated"] = Truncated, ["reason"] = Reason };
        if (Participant is { } participant) json[ParticipantKey] = participant.ParticipantId;
        if (Peer is { } peer) json["peer"] = peer.ToJson();
        if (Choices is { } choices) json["choices"] = PeerRow.ToArray(choices);
        return json;
    }

    public static PeerResolution Parse(JsonObject json, string repositoryId)
    {
        var peer = json["peer"] is JsonObject row ? PeerRow.Parse(row) : null;
        var participant = Text(json, ParticipantKey) is { Length: > 0 } id && peer is not null ? new VerifiedNativeParticipant(id, peer.SessionId, repositoryId) : null;
        // Anything unreadable is a refusal, never a grant.
        var outcome = Enum.TryParse<PeerOutcome>(Text(json, "outcome"), out var parsed) ? parsed : PeerOutcome.None;
        if (outcome == PeerOutcome.Resolved && participant is null) outcome = PeerOutcome.None;
        return new(outcome, participant, peer, PeerRow.ParseAll(json["choices"]), IsTrue(json, "truncated"), Text(json, "reason", PeerRefusals.Unknown) is { Length: > 0 } reason ? reason : PeerRefusals.Unknown);
    }
}
