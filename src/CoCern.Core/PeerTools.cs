using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain;
using CoCern.Domain.Messages;
using static CoCern.Contracts.RelayErrorCodes;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Core;

/// <summary>
/// <c>peers</c>, for native chats: this repository's chats, or the one a name or a choice's ref means. Every public reply is built field by field
/// from an allow-list, so no participant ID, generation or request can reach one. Raw clients keep <c>worker_list</c> and <c>chat_directory</c>.
/// </summary>
internal sealed class PeerTools(Aggregates aggregates, IMachineDirectory directory, string? hubIncarnation = null)
{
    private const string NativeOnly = "peers answers native chats only; use chat_directory.";

    public async Task<RelayReply> RunAsync(RelayCaller caller, JsonObject args, VerifiedNativeParticipant? from, CancellationToken cancellationToken)
    {
        if (from is null) return Error(SessionDenied, new { guidance = NativeOnly });
        var (name, reference) = (Text(args, "name"), Text(args, "ref"));
        if (name.Length == 0 && reference.Length == 0)
        {
            var listed = await aggregates.Request(AggregateKinds.ControlKey, caller, new PeerList(from.RepositoryId, from.ParticipantId, from.SessionId));
            return listed.Ok ? PublicListing(PeerListing.Parse(Aggregates.Parse(listed)), await ReachableAsync(from.RepositoryId, cancellationToken)) : listed;
        }
        var (resolution, refusal) = await ResolveAsync(caller, from, name, reference);
        return resolution is null ? refusal! : PublicResolution(resolution, await ReachableAsync(from.RepositoryId, cancellationToken));
    }

    /// <summary>
    /// The resolver, typed, for a hub-side consumer. It stores nothing and is never replayed: a mutation that resolves names must check its own stored
    /// receipt first, so a retry whose name has since turned ambiguous still gets its original answer. The participant it returns stays on the hub.
    /// </summary>
    internal async Task<(PeerResolution? Resolution, RelayReply? Refusal)> ResolveAsync(RelayCaller caller, VerifiedNativeParticipant from, string name, string reference)
    {
        var reply = await aggregates.Request(AggregateKinds.ControlKey, caller, new PeerResolve(from.RepositoryId, from.ParticipantId, from.SessionId, name, reference));
        if (!reply.Ok) return (null, reply);
        return (PeerResolution.Parse(Aggregates.Parse(reply), from.RepositoryId), null);
    }

    /// <summary>The session IDs with a live announced wake route now. An unavailable directory reads as none, which <c>state</c> reports as unconfirmed.</summary>
    private async Task<IReadOnlySet<string>> ReachableAsync(string repository, CancellationToken cancellationToken)
    {
        var reply = await directory.DirectoryAsync([repository], cancellationToken, hubIncarnation);
        if (!reply.Ok || Aggregates.Parse(reply)["items"] is not JsonArray items) return new HashSet<string>();
        return items.OfType<JsonObject>().Where(item => IsTrue(item, "reachable")).Select(item => Text(item, "relaySessionId")).Where(id => id.Length > 0).ToHashSet(StringComparer.Ordinal);
    }

    internal static RelayReply PublicListing(PeerListing listing, IReadOnlySet<string> reachable) =>
        Ok(new JsonObject { ["items"] = new JsonArray(listing.Rows.Select(row => (JsonNode?)Row(row, reachable)).ToArray()), ["truncated"] = listing.Truncated, ["untrusted"] = "name" });

    /// <summary>A name nobody here has and another repository's chat answer the same bytes: nothing echoes what was asked.</summary>
    internal static RelayReply PublicResolution(PeerResolution resolution, IReadOnlySet<string> reachable) => resolution.Outcome switch
    {
        PeerOutcome.Resolved => Ok(new JsonObject { ["peer"] = Row(resolution.Peer!, reachable), ["untrusted"] = "name" }),
        PeerOutcome.Ambiguous => Error(PeerAmbiguous, new JsonObject
        {
            ["choices"] = new JsonArray(resolution.Choices!.Select(row => (JsonNode?)Choice(row, reachable)).ToArray()), ["truncated"] = resolution.Truncated, ["untrusted"] = "name, machine"
        }),
        _ => Error(NotFound, new { reason = resolution.Reason })
    };

    private static JsonObject Row(PeerRow row, IReadOnlySet<string> reachable) => new()
    {
        ["name"] = row.Name, ["client"] = row.Client.Length > 0 ? row.Client : ChatProviders.Unknown,
        // Only what the hub holds today: a live announced route, or no proof of one. It has no signal for mid-turn.
        ["state"] = reachable.Contains(row.SessionId) ? "reachable" : "unconfirmed", ["self"] = row.Self
    };

    // The exception path: a session ID as ref rotates with the session, adds no concept, and is valid only while current.
    private static JsonObject Choice(PeerRow row, IReadOnlySet<string> reachable)
    {
        var choice = Row(row, reachable);
        choice["machine"] = row.MachineId;
        choice["ref"] = row.SessionId;
        choice["verified"] = row.Verified;
        return choice;
    }
}
