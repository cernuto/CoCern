using CoCern.Contracts;
using CoCern.Domain.Messages;
using CoCern.Domain.State;
using static CoCern.Contracts.RelayErrorCodes;
using static CoCern.Domain.Replies;

namespace CoCern.Domain;

/// <summary>
/// The directory's answers about named peers, for a verified native caller only: one repository's rows across principals (another PC has its own
/// credential), grouped by participant. A row counts as verified only through <see cref="ControlState.CurrentNativeSession"/>; label, binding, role
/// and machine never do. Equal names are refused with a choice: recency, client and machine never break the tie.
/// </summary>
internal static class PeerDirectory
{
    private sealed record Peer(string ParticipantId, string SessionId, DirectoryEntry Entry, string Client, string Name)
    {
        internal PeerRow Row(string caller) => new(SessionId, Name, Client, Entry.MachineId, ParticipantId.Length > 0, ParticipantId.Length > 0 && ParticipantId == caller);
    }

    public static RelayReply Resolve(AggregateContext context)
    {
        if (!PeerResolve.Decode(context.Args).TryGet(out var query, out var malformed)) return Malformed(malformed);
        var state = new ControlState(context.State);
        var repository = context.Repository;
        if (!CallerIsCurrent(state, repository, query.CallerParticipantId, query.CallerSessionId)) return Error(Unavailable, new { retry = true });
        var peers = Peers(state, repository);
        var wanted = query.Name.Trim();
        // Whole-label equality, trimmed and ignoring case, never a prefix. A ref is the row's current session, so a replaced one finds nothing.
        var matches = query.Ref.Length > 0 ? peers.Where(peer => peer.SessionId == query.Ref).ToList()
            : peers.Where(peer => peer.Name.Length > 0 && string.Equals(peer.Name, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) return Answer(None(PeerRefusals.Unknown));
        if (matches.Count > 1) return Ambiguous(matches, query.CallerParticipantId);
        var match = matches[0];
        if (match.ParticipantId.Length == 0) return Answer(None(PeerRefusals.Unverified));
        return state.CurrentNativeSession(match.ParticipantId, repository) is { } verified
            ? Answer(new PeerResolution(PeerOutcome.Resolved, verified, match.Row(query.CallerParticipantId)))
            : Answer(None(PeerRefusals.Replacing));
    }

    public static RelayReply ListPeers(AggregateContext context)
    {
        if (!PeerList.Decode(context.Args).TryGet(out var query, out var malformed)) return Malformed(malformed);
        var state = new ControlState(context.State);
        var repository = context.Repository;
        if (!CallerIsCurrent(state, repository, query.CallerParticipantId, query.CallerSessionId)) return Error(Unavailable, new { retry = true });
        var peers = Peers(state, repository);
        var rows = peers.OrderByDescending(peer => peer.ParticipantId.Length > 0 && peer.ParticipantId == query.CallerParticipantId)
            .ThenByDescending(peer => peer.Entry.LastSeen).ThenBy(peer => peer.SessionId, StringComparer.Ordinal)
            .Take(RelayLimits.MaxPageSize).Select(peer => peer.Row(query.CallerParticipantId)).ToList();
        return Fit(rows, peers.Count > rows.Count, (page, truncated) => Ok(new PeerListing(page, truncated).ToJson()));
    }

    // A session mid-close or a superseded duplicate is not this participant's current row, so it asks again rather than reading as one.
    private static bool CallerIsCurrent(ControlState state, string repository, string participant, string session) =>
        state.CurrentNativeSession(participant, repository)?.SessionId == session;

    /// <summary>The repository filter is mandatory: the control grain is hub-wide. One entry per participant, and one per legacy row.</summary>
    private static List<Peer> Peers(ControlState state, string repository)
    {
        var peers = new Dictionary<string, Peer>(StringComparer.Ordinal);
        foreach (var (id, entry) in state.Entries())
        {
            if (entry is null || entry.RepositoryId != repository) continue;
            var participant = entry.NativeId;
            var client = participant.Length == 0 ? "" : state.NativeClient(participant);
            var peer = new Peer(participant, id, entry, client, DisplayName(entry, client));
            var key = participant.Length == 0 ? "session:" + id : participant;
            if (!peers.TryGetValue(key, out var chosen) || Rank(state, peer, chosen) > 0) peers[key] = peer;
        }
        return peers.Values.ToList();
    }

    // Among one participant's rows, while a duplicate settles: the row its private join names, else the latest seen. This picks a row, never a peer.
    private static int Rank(ControlState state, Peer candidate, Peer chosen)
    {
        var current = state.NativeCurrentSession(candidate.ParticipantId);
        if (candidate.SessionId == current) return 1;
        if (chosen.SessionId == current) return -1;
        var seen = candidate.Entry.LastSeen.CompareTo(chosen.Entry.LastSeen);
        return seen != 0 ? seen : string.CompareOrdinal(chosen.SessionId, candidate.SessionId);
    }

    /// <summary>The label, or the client's own default for a chat with no title, so untitled chats of one client share a name and are refused as ambiguous.</summary>
    private static string DisplayName(DirectoryEntry entry, string client) => entry.Label.Trim() is { Length: > 0 } label ? label : client switch
    {
        ChatProviders.Claude => "Claude",
        ChatProviders.Codex => "Codex",
        _ => ""
    };

    private static RelayReply Ambiguous(List<Peer> matches, string caller)
    {
        // The order only decides which choices fit. The answer stays a refusal whichever row is newest.
        var rows = matches.OrderByDescending(peer => peer.Entry.LastSeen).ThenBy(peer => peer.SessionId, StringComparer.Ordinal)
            .Take(RelayLimits.MaxPeerChoices).Select(peer => peer.Row(caller)).ToList();
        return Fit(rows, matches.Count > rows.Count, (page, truncated) => Answer(new PeerResolution(PeerOutcome.Ambiguous, Choices: page, Truncated: truncated)));
    }

    private static PeerResolution None(string reason) => new(PeerOutcome.None, Reason: reason);

    private static RelayReply Answer(PeerResolution resolution) => Ok(resolution.ToJson());

    /// <summary>Keeps the longest prefix of <paramref name="rows"/> that fits a reply. Labels and machine names are long only by a client's choice.</summary>
    private static RelayReply Fit(List<PeerRow> rows, bool truncated, Func<IReadOnlyList<PeerRow>, bool, RelayReply> reply)
    {
        while (rows.Count > 1 && !FitsWire(reply(rows, truncated), RelayLimits.AggregateReplyBytes))
        {
            rows.RemoveAt(rows.Count - 1);
            truncated = true;
        }
        return reply(rows, truncated);
    }
}
