using System.Text;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain;
using CoCern.Domain.Messages;
using Xunit;

namespace CoCern.Tests;

/// <summary>A chat's handle on its session: the ID and the secret <c>session_open</c> returned.</summary>
internal sealed record Chat(string Id, string Token);

/// <summary>
/// Every aggregate's envelope, driven through <see cref="CoordinationStateMachine.Apply"/> with explicit times, with the outbox effects
/// between them delivered only when a test says so. <see cref="Deliver"/> is the one way an effect reaches its target, whatever order a
/// test or a delivery mode picks:
/// <list type="bullet">
/// <item><see cref="DeliverAll"/> drains every outbox in the order effects were queued, as an outbox that never fails would.</item>
/// <item><see cref="DeliverInOrder"/> drains the named sources' outboxes first, one source before the next: a stalled outbox is the usual
/// way one aggregate's news overtakes another's.</item>
/// <item><see cref="Explore"/> delivers the pending effects in every order, and with second copies placed anywhere later, as an outbox
/// that retries one effect while delivering the next, or whose removal failed after its target committed, would.</item>
/// </list>
/// The hub also makes the direct calls Core makes beside an outbox copy (<c>session_open</c>'s <see cref="Operations.Open"/>,
/// <c>action_begin</c>'s <see cref="Operations.Block"/>): delivered at once, or held as one more pending item when
/// <see cref="HoldDirectCalls"/> is set.
/// </summary>
internal sealed class AggregateHub
{
    public const string Repository = "repo";
    public static readonly RelayCaller Alice = new("alice", false, [Repository]);
    public static readonly RelayCaller Admin = new("admin", true, [Repository]);
    public static readonly RelayCaller Sweeper = new("relay-sweep", true, [], true);
    public static readonly RelayCaller BoardCaller = new("board", false, [Repository], true);
    public static readonly string BoardKey = AggregateKinds.BoardKey(Repository, AggregateKinds.DefaultBoard);

    private readonly Dictionary<(string Kind, string Key), StateEnvelope<CoordinationDocument>> aggregates = new();
    private readonly List<DurableEffect> direct = [];
    private readonly List<DurableEffect> copies = [];
    private readonly HashSet<string> copied = [];
    private int requests;

    /// <summary>When set, a direct call is held as a pending item instead of being delivered at once, so a delivery mode can place it.</summary>
    public bool HoldDirectCalls { get; set; }

    public StateEnvelope<CoordinationDocument> Of(string kind, string key) =>
        aggregates.TryGetValue((kind, key), out var envelope) ? envelope : aggregates[(kind, key)] = new();

    public JsonObject State(string kind, string key) => JsonNode.Parse(Of(kind, key).Value.Json)!.AsObject();
    public JsonObject Directory() => State(AggregateKinds.Control, AggregateKinds.ControlKey)["sessions"] as JsonObject ?? new JsonObject();

    /// <summary>Every effect still waiting in an outbox, in the order a FIFO delivery would take them.</summary>
    public IReadOnlyList<DurableEffect> Queued => aggregates.Values.SelectMany(envelope => envelope.Effects).ToList();

    public RelayReply Apply(string kind, string key, string operation, JsonObject args, RelayCaller caller, DateTimeOffset now)
    {
        args["repositoryId"] ??= Repository;
        return CoordinationStateMachine.Apply(Of(kind, key), kind, key, caller, operation, args, now);
    }

    /// <summary>
    /// Opens a session as the hub does: the directory records it and queues the session's initialization, and the hub initializes the
    /// session directly as well, from the client's fields (<see cref="SessionRecord.Opened"/>). The queued copy stays in the directory's
    /// outbox; it deduplicates at the session.
    /// </summary>
    public Chat Open(string label, DateTimeOffset now, string? machineId = null, string? role = null, string? requestId = null)
    {
        var args = new JsonObject { ["requestId"] = requestId ?? "open-" + ++requests, ["label"] = label, ["hubIncarnation"] = "hub-1" };
        if (machineId is not null) args["machineId"] = machineId;
        if (role is not null) args["role"] = role;
        var opened = Data(Apply(AggregateKinds.Control, AggregateKinds.ControlKey, RelayTools.SessionOpen, args, Alice, now));
        var chat = new Chat(opened["sessionId"]!.GetValue<string>(), opened["sessionToken"]!.GetValue<string>());
        var record = SessionRecord.Opened(chat.Id, chat.Token, Alice.PrincipalId, Repository, args);
        List<DurableEffect> initialize = [new("direct-open-" + ++requests, SessionOpen.TargetKind, chat.Id, SessionOpen.Operation, new SessionOpen(record).ToJson().ToJsonString(), now)];
        CoordinationStateMachine.StampEffects(initialize, 0, Alice);
        Call(initialize[0], now);
        return chat;
    }

    /// <summary>The hub's check of the session behind every call; it reports activity at most hourly.</summary>
    public RelayReply Verify(Chat chat, DateTimeOffset now) => Session(chat, Operations.Verify, new(), now);

    public RelayReply Session(Chat chat, string operation, JsonObject args, DateTimeOffset now)
    {
        args["sessionId"] = chat.Id; args["sessionToken"] = chat.Token; args["hubIncarnation"] = "hub-1";
        if (CoordinationStateMachine.IsMutation(operation)) args["requestId"] ??= "r-" + ++requests;
        return Apply(AggregateKinds.Session, chat.Id, operation, args, Alice with { IsInternal = true }, now);
    }

    public RelayReply Send(Chat from, string to, string text, DateTimeOffset now, bool supersede = false)
    {
        var args = new JsonObject { ["recipientSessionId"] = to, ["text"] = text };
        if (supersede) args["supersede"] = true;
        return Session(from, RelayTools.SendMessage, args, now);
    }

    public List<JsonObject> Inbox(Chat chat, DateTimeOffset now) =>
        Data(Session(chat, RelayTools.CheckInbox, new(), now))["items"]!.AsArray().Select(item => item!.AsObject()).ToList();

    /// <summary>A board call. A new action also blocks its resources directly, as the hub does beside the board's own queued blocks.</summary>
    public RelayReply Board(string tool, JsonObject args, DateTimeOffset now, RelayCaller? caller = null)
    {
        args["requestId"] ??= "b-" + ++requests;
        var board = Of(AggregateKinds.Board, BoardKey);
        var before = board.Effects.Select(effect => effect.Id).ToHashSet();
        var reply = Apply(AggregateKinds.Board, BoardKey, tool, args, caller ?? Alice, now);
        if (tool == RelayTools.ActionBegin && reply.Ok && JsonNode.Parse(reply.DataJson)?["replayAllowed"]?.GetValue<bool>() == true)
            foreach (var block in board.Effects.Where(effect => effect.Operation == Operations.Block && !before.Contains(effect.Id)).ToList())
                Call(block with { Id = "direct-" + block.Id }, now);
        return reply;
    }

    public RelayReply Close(JsonObject args, DateTimeOffset now)
    {
        args["command"] = "close_sessions"; args["requestId"] = "close-" + ++requests;
        return Apply(AggregateKinds.Control, AggregateKinds.ControlKey, RelayTools.ControlRequest, args, Admin, now);
    }

    public RelayReply Sweep(int idleHours, DateTimeOffset now) =>
        Apply(AggregateKinds.Control, AggregateKinds.ControlKey, Operations.SweepSessions, new() { ["repositoryId"] = "*", ["idleHours"] = idleHours }, Sweeper, now);

    /// <summary>
    /// Delivers one effect (queued, a direct call, or a second copy) to its target with the caller it carries, and removes it from wherever
    /// it waited. Every delivery mode goes through here.
    /// </summary>
    public RelayReply Deliver(DurableEffect effect, DateTimeOffset now)
    {
        foreach (var envelope in aggregates.Values) envelope.Effects.RemoveAll(queued => queued.Id == effect.Id);
        direct.RemoveAll(held => held.Id == effect.Id);
        copies.RemoveAll(copy => copy.Id == effect.Id);
        var payload = JsonNode.Parse(effect.PayloadJson)!.AsObject();
        var caller = CoordinationStateMachine.TakeEffectCaller(payload);
        return CoordinationStateMachine.Apply(Of(effect.TargetKind, effect.TargetKey), effect.TargetKind, effect.TargetKey, caller, effect.Operation, payload, now);
    }

    /// <summary>Removes and returns every queued effect of <paramref name="operation"/>, for a test that loses one on purpose (or delivers it later).</summary>
    public List<DurableEffect> Discard(string operation)
    {
        var discarded = new List<DurableEffect>();
        foreach (var envelope in aggregates.Values)
        {
            discarded.AddRange(envelope.Effects.Where(effect => effect.Operation == operation));
            envelope.Effects.RemoveAll(effect => effect.Operation == operation);
        }
        return discarded;
    }

    /// <summary>Delivers every queued effect, and what those deliveries queue, until every outbox is empty.</summary>
    public void DeliverAll(DateTimeOffset now)
    {
        for (var round = 0; round < 20; round++)
        {
            var queued = Queued.Concat(direct).ToList();
            if (queued.Count == 0) return;
            foreach (var effect in queued) Accept(effect, Deliver(effect, now));
        }
        Assert.Fail("Effects kept queuing more effects.");
    }

    /// <summary>
    /// Delivers every queued effect, draining each of <paramref name="sources"/>' outboxes in turn (and what their deliveries queue there)
    /// before any later source's or any other outbox's, which then drain in queued order.
    /// </summary>
    public void DeliverInOrder(DateTimeOffset now, params (string Kind, string Key)[] sources)
    {
        for (var step = 0; step < 500; step++)
        {
            var next = sources.Select(source => Of(source.Kind, source.Key).Effects.FirstOrDefault()).FirstOrDefault(effect => effect is not null)
                ?? Queued.Concat(direct).FirstOrDefault();
            if (next is null) return;
            Accept(next, Deliver(next, now));
        }
        Assert.Fail("Effects kept queuing more effects.");
    }

    /// <summary>
    /// Fails the test unless the target took the effect as its outbox would leave it: accepted, or refused where the outbox drops the
    /// refusal (an attention item, or a lock the board's own copy of a block finds busy or unclaimed). Any other refusal would be retried
    /// forever. A malformed message fails whatever its delivery class: its emitter is wrong, and in production it would only be logged.
    /// </summary>
    public static void Accept(DurableEffect effect, RelayReply reply)
    {
        Assert.False(reply.Code == RelayErrorCodes.MalformedEffect, effect.Operation + " to " + effect.TargetKey + " is malformed: " + reply.DataJson);
        var dropped = Operations.IsAttention(effect.Operation) || effect.Operation == Operations.Block && reply.Code is RelayErrorCodes.Busy or RelayErrorCodes.ClaimRequired;
        Assert.True(reply.Ok || dropped, effect.Operation + " to " + effect.TargetKey + " was refused: " + reply.Code + " " + reply.DataJson);
    }

    /// <summary>Makes a direct call: delivered at once, or held for a delivery mode to place.</summary>
    private void Call(DurableEffect effect, DateTimeOffset now)
    {
        if (HoldDirectCalls) direct.Add(effect);
        else Accept(effect, Deliver(effect, now));
    }

    public AggregateHub Clone()
    {
        var clone = new AggregateHub { requests = requests, HoldDirectCalls = HoldDirectCalls };
        foreach (var ((kind, key), envelope) in aggregates)
            clone.aggregates[(kind, key)] = new() { SchemaVersion = envelope.SchemaVersion, Value = new() { Json = envelope.Value.Json }, Effects = [.. envelope.Effects], DueWork = [.. envelope.DueWork] };
        clone.direct.AddRange(direct);
        clone.copies.AddRange(copies);
        clone.copied.UnionWith(copied);
        return clone;
    }

    /// <summary>
    /// Delivers the pending effects (queued, held direct calls, and what each delivery queues) in every order: at each step any pending
    /// effect may go next, and any effect may also leave an independent second copy, delivered at any later step (each effect is copied at
    /// most once, and at most <paramref name="maxCopies"/> per schedule). <paramref name="check"/> runs on every drained hub. States
    /// reached twice are explored once. Fails when a schedule takes more than <paramref name="maxEffects"/> distinct effects, so a scenario
    /// stays small enough to explore in seconds. Returns the number of drained hubs checked.
    /// </summary>
    public int Explore(DateTimeOffset now, Action<AggregateHub> check, int maxCopies = 2, int maxEffects = 8)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var drained = 0;
        void Visit(AggregateHub hub, int effects)
        {
            if (!seen.Add(hub.Signature())) return;
            var pending = hub.Queued.Concat(hub.direct).ToList();
            if (pending.Count == 0 && hub.copies.Count == 0) { check(hub); drained++; return; }
            Assert.True(effects + pending.Count <= maxEffects, "The scenario takes more than " + maxEffects + " effects to drain; keep it smaller.");
            foreach (var effect in pending)
            {
                foreach (var copy in new[] { false, true })
                {
                    if (copy && (hub.copied.Count >= maxCopies || hub.copied.Contains(effect.Id))) continue;
                    var next = hub.Clone();
                    if (copy) { next.copied.Add(effect.Id); next.copies.Add(effect); }
                    Accept(effect, next.Deliver(effect, now));
                    Visit(next, effects + 1);
                }
            }
            foreach (var copy in hub.copies)
            {
                var next = hub.Clone();
                Accept(copy, next.Deliver(copy, now));
                Visit(next, effects);
            }
        }
        Visit(Clone(), 0);
        return drained;
    }

    /// <summary>What identifies a hub's state for <see cref="Explore"/>: every aggregate's document and pending effects, ignoring effect IDs.</summary>
    private string Signature()
    {
        static string Of(DurableEffect effect) => effect.TargetKind + "|" + effect.TargetKey + "|" + effect.Operation + "|" + effect.PayloadJson;
        var text = new StringBuilder();
        foreach (var ((kind, key), envelope) in aggregates.OrderBy(entry => entry.Key.Kind, StringComparer.Ordinal).ThenBy(entry => entry.Key.Key, StringComparer.Ordinal))
            text.Append(kind).Append('/').Append(key).Append('=').Append(envelope.Value.Json).Append('\n').AppendJoin('\n', envelope.Effects.Select(Of).Order(StringComparer.Ordinal)).Append('\n');
        text.Append("direct\n").AppendJoin('\n', direct.Select(Of).Order(StringComparer.Ordinal));
        text.Append("\ncopies\n").AppendJoin('\n', copies.Select(Of).Order(StringComparer.Ordinal));
        text.Append("\ncopied ").Append(copied.Count);
        return text.ToString();
    }

    /// <summary>
    /// The hand-off invariants a drained hub holds, over every session opened here (not only those the directory lists, so a session open
    /// but missing from it fails):
    /// <list type="bullet">
    /// <item>an opened session that has not expired is in the directory;</item>
    /// <item>a listed session that answers for an unresolved action is marked there as owning work, so no close takes it;</item>
    /// <item>no unresolved action answered for by an expired session is left without <c>ownerClosed</c>, or with that session's claim on
    /// its task still live: the principal's other session can recover it at once.</item>
    /// </list>
    /// </summary>
    public void AssertInvariants(DateTimeOffset now)
    {
        var directory = Directory();
        var expired = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ((kind, id), envelope) in aggregates)
        {
            if (kind != AggregateKinds.Session) continue;
            var session = JsonNode.Parse(envelope.Value.Json)!.AsObject();
            if (session["expiredAt"] is not null) { expired.Add(id); continue; }
            if (session["principalId"] is null) continue;
            Assert.True(directory.ContainsKey(id), "Session " + id + " is open but missing from the directory.");
            if (session["work"]?["openActions"] is JsonObject { Count: > 0 } || session["work"]?["openActionsOverflow"] is not null)
                Assert.True(directory[id]!["ownsWork"] is not null, "Session " + id + " answers for an unresolved action, but the directory does not know.");
        }
        foreach (var ((kind, key), envelope) in aggregates)
        {
            if (kind != AggregateKinds.Board || JsonNode.Parse(envelope.Value.Json)!["tasks"] is not JsonObject tasks) continue;
            foreach (var (taskId, node) in tasks)
            {
                if (node is not JsonObject task || task["actions"] is not JsonObject actions) continue;
                foreach (var action in actions.Select(entry => entry.Value).OfType<JsonObject>().Where(Unresolved))
                {
                    var owner = action["recovery"]?["sessionId"]?.GetValue<string>() ?? action["sessionId"]?.GetValue<string>() ?? "";
                    if (!expired.Contains(owner)) continue;
                    Assert.True(action["ownerClosed"] is not null, "Action " + action["actionId"] + " on " + taskId + " is stranded: its session " + owner + " expired without telling the board.");
                    if (task["owner"]?.GetValue<string>() == owner)
                        Assert.True(DateTimeOffset.Parse(task["leaseUntil"]!.GetValue<string>()) <= now, "Task " + taskId + " is still claimed by the expired session " + owner + ".");
                }
            }
        }
    }

    private static bool Unresolved(JsonObject action) =>
        action["status"]?.GetValue<string>() is "invoking" or "started" or "unknown" or "incomplete" || action["restoration"]?.GetValue<string>() is "pending" or "blocked";

    public static JsonObject Data(RelayReply reply)
    {
        Assert.True(reply.Ok, reply.Code + " " + reply.DataJson);
        return JsonNode.Parse(reply.DataJson)!.AsObject();
    }
}
