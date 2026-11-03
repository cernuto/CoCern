using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;

namespace CoCern.Domain;

/// <summary>
/// Rules that readers of stored aggregate state apply: storage's commit, the dashboard's observation and the discovery directory. They
/// live beside the aggregates that write that state, so a reader outside the Domain asks here instead of deriving them again from the
/// JSON, where copies drift apart.
/// </summary>
public static class ReadModels
{
    /// <summary>The top-level key a session's record keeps once the directory closed it (see <see cref="IsClosed(JsonObject)"/>).</summary>
    public const string ClosedAtKey = "expiredAt";

    /// <summary>
    /// Whether a session's state says it was closed. A closed session keeps its record, with the time it closed at the top level, so its
    /// chat is told it expired. Only that top-level key counts: the record also holds the client's own <c>binding</c>, which may use any
    /// key, <c>expiredAt</c> included.
    /// </summary>
    public static bool IsClosed(JsonObject sessionState) => sessionState[ClosedAtKey] is not null;

    /// <summary>
    /// <see cref="IsClosed(JsonObject)"/> for the state as stored. Only the top-level keys are read and nested values are skipped, so
    /// storage can ask on every session commit without parsing the record.
    /// </summary>
    public static bool IsClosed(string sessionJson)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(sessionJson), new JsonReaderOptions { MaxDepth = 64 });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var closedAt = reader.ValueTextEquals(ClosedAtKey);
            reader.Read();
            if (closedAt && reader.TokenType != JsonTokenType.Null) return true;
            reader.Skip();
        }
        return false;
    }

    /// <summary>
    /// The top-level key under which every aggregate keeps the stored replies to its recent requests, so that a retry is answered, not
    /// run again (<see cref="CoordinationStateMachine.Apply"/>). A stored reply holds whatever that reply returned.
    /// </summary>
    public const string RequestsKey = "requests";

    /// <summary>The top-level key for the requests whose stored replies were evicted before their window ended (see <see cref="Retention"/>).</summary>
    public const string RequestTombstonesKey = "requestTombstones";

    /// <summary>The top-level key for a session's secret.</summary>
    public const string SessionTokenKey = "sessionToken";

    /// <summary>
    /// The aggregate kinds whose state may leave the live database in an analytics snapshot: boards and locks. Session state (private
    /// inboxes, attention notes, bindings), the control directory and discovery (machine credentials) never do; analytics reads sessions
    /// only through the activity log.
    /// </summary>
    public static readonly IReadOnlyList<string> SnapshotKinds = [AggregateKinds.Board, AggregateKinds.Lock];

    /// <summary>
    /// The top-level state keys an analytics snapshot removes even from <see cref="SnapshotKinds"/>: the idempotency caches
    /// (<see cref="RequestsKey"/>, whose stored replies can hold anything an earlier reply returned, and <see cref="RequestTombstonesKey"/>)
    /// and any session secret (<see cref="SessionTokenKey"/>).
    /// </summary>
    public static readonly IReadOnlyList<string> PrivateStateKeys = [RequestsKey, RequestTombstonesKey, SessionTokenKey];

    /// <summary>
    /// Whether a machine announced recently enough to count as live: its <see cref="State.EnrolledMachine.LastSeenKey"/> lies within
    /// <see cref="DiscoveryLimits.LivenessTimeout"/> of <paramref name="now"/>, on either side. A stamp a little ahead is normal: the dashboard
    /// takes its time before it reads, and an announcement that lands in between is stamped later, which must not show a live machine as
    /// gone. A stamp further ahead comes from a clock since set back and is no evidence of recent contact, so it never keeps a silent machine
    /// live for more than twice the timeout; the machine's next announcement restamps it.
    /// </summary>
    public static bool IsLive(DateTimeOffset? lastSeen, DateTimeOffset now) =>
        lastSeen is { } seen && (now - seen).Duration() <= DiscoveryLimits.LivenessTimeout;

    /// <summary>
    /// Whether a message can wake an announced chat now: its machine is live (<see cref="IsLive"/>) and its provider's own wake path is
    /// running. A Codex chat is woken by the node (<see cref="State.AnnouncedChat.WakeableKey"/>); a Claude chat by its waiting inbox call
    /// (<see cref="State.AnnouncedChat.WaiterRunningKey"/>). A chat of any other provider, or one the node could not map
    /// (<see cref="ChatProviders.Unknown"/>), is never reachable. Providers compare ignoring case, as nodes announce them.
    /// </summary>
    public static bool IsReachable(string provider, bool wakeable, bool waiterRunning, DateTimeOffset? machineLastSeen, DateTimeOffset now) =>
        IsLive(machineLastSeen, now) && (string.Equals(provider, ChatProviders.Codex, StringComparison.OrdinalIgnoreCase) ? wakeable
            : string.Equals(provider, ChatProviders.Claude, StringComparison.OrdinalIgnoreCase) && waiterRunning);
}
