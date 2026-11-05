using System.Text.Json.Nodes;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using CoCern.Contracts;
using CoCern.Domain.Messages;
using CoCern.Domain.State;

namespace CoCern.Domain;

/// <summary>
/// One-shot records in existing aggregate state with existing MessageDelivery effects.
/// Primary-session private operations reuse its transaction and durable outbox.
/// Public tool composition and qualifying source handshakes remain separate.
/// </summary>
public static class EventWaitAggregate
{
    public const string RegisterOperation = "__event_wait_register";
    public const string CancelOperation = "__event_wait_cancel";
    public const string DueOperation = "__event_wait_due";

    internal static RelayReply Handle(AggregateContext context)
    {
        if (!context.Caller.IsInternal) return Error("InternalOnly");
        if (context.Operation == DueOperation)
            return new SessionState(context.State).Closed ? Ok(new { checkedWaits = false }) : Due(context);
        if (context.Operation == CancelOperation) return Cancel(context, context.Args["waitId"]?.GetValue<string>() ?? "");
        if (context.Operation != RegisterOperation) return Error("InvalidArguments");
        EventWaitRequest? request;
        try { request = context.Args["request"]?.Deserialize<EventWaitRequest>(EventWaitState.Json); }
        catch (JsonException) { return Error("InvalidArguments"); }
        if (request is null) return Error("InvalidArguments");
        var existed = new EventWaitState(context.State).Entries?.ContainsKey(request.WaitId) == true;
        var result = Register(context, request);
        if (!result.Ok || existed) return result;
        // A future outbox effect is indexed by the existing due-work store and
        // pump. Its payload carries no session credential; the private target
        // rechecks persisted deadlines, so replay and stale timers cannot refire.
        var identity = JsonSerializer.Serialize(new[] { context.Repository, context.Key, request.WaitId });
        var id = "event-wait-due-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        context.Effects.Add(new(id, AggregateKinds.Session, context.Key, DueOperation,
            new JsonObject { ["repositoryId"] = context.Repository, ["sessionId"] = context.Key }.ToJsonString(), request.ExpiresAt));
        return result;
    }

    // Domain transition entry, analogous to CoordinationStateMachine.Apply. No tool
    // or scheduler is registered; final integration can call the internal handlers
    // in the existing session transaction instead of introducing another engine.
    public static RelayReply Apply(StateEnvelope<CoordinationDocument> envelope, RelayCaller caller,
        string sessionKey, string operation, JsonObject args, DateTimeOffset now,
        EventWaitRequest? request = null, EventWaitSignal? signal = null)
    {
        var state = JsonNode.Parse(envelope.Value.Json)!.AsObject();
        var context = new AggregateContext(state, caller, operation, args, now) { Key = sessionKey };
        if (!RepositoryMatches(context)) return Error("RepositoryDenied");
        RelayReply result;
        try { result = operation switch
        {
            "register" when request is not null => Register(context, request, signal),
            "observe" when signal is not null => Observe(context, signal),
            "cancel" => Cancel(context, args["waitId"]?.GetValue<string>() ?? ""),
            "due" => Due(context),
            _ => Error("InvalidArguments")
        }; }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        { return Error("InvalidArguments"); }
        if (envelope.Effects.Count + context.Effects.Count > RelayLimits.MaxRecords) return Error("CapacityExceeded");
        if (result.Ok)
        {
            envelope.Value = new() { Json = state.ToJsonString() };
            CoordinationStateMachine.StampEffects(context.Effects, 0, caller);
            envelope.Effects.AddRange(context.Effects);
        }
        return result;
    }

    public static DateTimeOffset? NextDue(StateEnvelope<CoordinationDocument> envelope)
    {
        var entries = new EventWaitState(JsonNode.Parse(envelope.Value.Json)!.AsObject()).Entries;
        if (entries is null) return null;
        return entries.Select(x => x.Value).OfType<JsonObject>().Where(x => x["terminal"] is null)
            .Select(x => (DateTimeOffset?)EventWaitState.Request(x).ExpiresAt).Min();
    }

    internal static RelayReply Register(AggregateContext context, EventWaitRequest request, EventWaitSignal? snapshot = null)
    {
        if (Authorize(context) is { } denied) return denied;
        if (!Valid(request)) return Error("InvalidArguments");
        if (snapshot is not null && !context.Caller.IsInternal) return Error("InternalOnly");
        if (snapshot is not null && !ValidSignal(snapshot)) return Error("InvalidArguments");
        var waits = new EventWaitState(context.State).CreateEntries();
        if (waits[request.WaitId] is JsonObject existing)
        {
            if (EventWaitState.Request(existing) != request) return Error("RequestConflict");
            if (existing["terminal"] is null && request.ExpiresAt <= context.Now)
                Terminal(context, existing, request, "expired", request.Revision);
            return Reply(existing);
        }
        var replayKey = "event-wait\u001f" + context.Repository + "\u001f" + context.Key + "\u001f" + request.WaitId;
        if (EventWaitRetirement.Contains(context.State, replayKey, context.Now)) return Error("RequestExpired");
        if (request.ExpiresAt <= context.Now || request.ExpiresAt - context.Now > TimeSpan.FromHours(1)) return Error("InvalidArguments");
        if (waits.Count >= EventWaitState.MaxRetained)
        {
            // Active subscriptions never leave this map. Retirement refuses
            // pressure instead of evicting any unexpired replay fence.
            var retired = waits.Where(pair => pair.Value?["terminal"] is JsonObject)
                .OrderBy(pair => pair.Value!["terminal"]!["at"]!.GetValue<string>(), StringComparer.Ordinal)
                .Take(EventWaitState.MaxRetained / 4).ToArray();
            var buried = new List<(string Key, DateTimeOffset At)>();
            foreach (var pair in retired)
            {
                buried.Add(("event-wait\u001f" + context.Repository + "\u001f" + context.Key + "\u001f" + pair.Key, context.Now));
            }
            if (!EventWaitRetirement.TryRemember(context.State, buried, context.Now)) return Error("CapacityExceeded");
            foreach (var pair in retired) waits.Remove(pair.Key);
        }
        if (waits.Count >= EventWaitState.MaxRetained || waits.Count(x => x.Value?["terminal"] is null) >= EventWaitState.MaxActive)
            return Error("CapacityExceeded");
        var entry = new JsonObject { ["request"] = JsonSerializer.SerializeToNode(request, EventWaitState.Json) };
        waits[request.WaitId] = entry;
        if (snapshot is not null && Matches(request, snapshot, context.Repository))
            Terminal(context, entry, request, snapshot.Outcome, snapshot.Revision);
        return Reply(entry);
    }

    internal static RelayReply Observe(AggregateContext context, EventWaitSignal signal)
    {
        if (!context.Caller.IsInternal) return Error("InternalOnly");
        if (signal.RepositoryId != context.Repository) return Error("RepositoryDenied");
        if (!ValidSignal(signal)) return Error("InvalidArguments");
        var waits = new EventWaitState(context.State).Entries;
        if (waits is not null)
            foreach (var entry in waits.Select(x => x.Value).OfType<JsonObject>())
            {
                if (entry["terminal"] is not null) continue;
                var request = EventWaitState.Request(entry);
                if (request.ExpiresAt <= context.Now) Terminal(context, entry, request, "expired", request.Revision);
                else if (Matches(request, signal, context.Repository)) Terminal(context, entry, request, signal.Outcome, signal.Revision);
            }
        return Ok(new { observed = true });
    }

    internal static RelayReply Cancel(AggregateContext context, string waitId)
    {
        if (Authorize(context) is { } denied) return denied;
        if (new EventWaitState(context.State).Entries?[waitId] is not JsonObject entry) return Error("WaitUnknown");
        var request = EventWaitState.Request(entry);
        if (entry["terminal"] is null)
            Terminal(context, entry, request, request.ExpiresAt <= context.Now ? "expired" : "canceled", request.Revision);
        return Reply(entry);
    }

    internal static RelayReply Due(AggregateContext context)
    {
        if (!context.Caller.IsInternal) return Error("InternalOnly");
        var entries = new EventWaitState(context.State).Entries;
        if (entries is not null)
            foreach (var entry in entries.Select(x => x.Value).OfType<JsonObject>())
                if (entry["terminal"] is null && EventWaitState.Request(entry) is { } request && request.ExpiresAt <= context.Now)
                    Terminal(context, entry, request, "expired", request.Revision);
        return Ok(new { checkedWaits = true });
    }

    private static bool RepositoryMatches(AggregateContext context) => context.Caller.MayAccess(context.Repository) &&
        context.State["repositoryId"]?.GetValue<string>() == context.Repository && new SessionState(context.State).SessionId == context.Key;

    internal static RelayReply? Authorize(AggregateContext context)
    {
        if (context.SessionId != context.Key) return Error("SessionDenied");
        var args = new JsonObject { ["repositoryId"] = context.Repository, ["sessionId"] = context.SessionId,
            ["sessionToken"] = context.Args["sessionToken"]?.DeepClone() };
        var verification = new AggregateContext(context.State, context.Caller, Operations.Verify, args, context.Now) { Key = context.Key };
        var reply = SessionAggregate.Handle(verification);
        if (!reply.Ok) return reply;
        context.Effects.AddRange(verification.Effects);
        return null;
    }

    private static bool Valid(EventWaitRequest request) => Bounded(request.WaitId, 128) && Bounded(request.Target, 512) &&
        Bounded(request.Revision, 128, request.Kind is "peer_reply" or "task_completed") && (request.Kind switch
        {
            "task_completed" => request.Revision == "" && request.BaselineRevision is >= 0 && Bounded(request.QualifyingData, 256) && request.PeerSessionId is null && request.ReplyTo is null,
            "claim_released" => request.BaselineRevision is null && request.QualifyingData is null && request.PeerSessionId is null && request.ReplyTo is null,
            "peer_reply" => request.Revision == "" && request.BaselineRevision is null && request.QualifyingData is null && Bounded(request.PeerSessionId, 128) && Bounded(request.ReplyTo, 128) && request.Target == request.ReplyTo,
            _ => false
        });

    private static bool Bounded(string? value, int max, bool allowEmpty = false) => value is not null &&
        (allowEmpty || value.Length > 0) && value.Length <= max && !value.Any(char.IsControl) && !value.Any(char.IsSurrogate);

    private static bool ValidSignal(EventWaitSignal signal) =>
        signal.Kind is "task_completed" or "claim_released" or "peer_reply" &&
        signal.Outcome is "satisfied" or "target_gone" or "stranded" &&
        Bounded(signal.RepositoryId, 512) && Bounded(signal.Target, 512) && Bounded(signal.Revision, 128) &&
        (signal.PeerSessionId is null || Bounded(signal.PeerSessionId, 128)) &&
        (signal.ReplyTo is null || Bounded(signal.ReplyTo, 128)) &&
        (signal.Kind == "task_completed" ? TryRevision(signal.Revision, out _) && Bounded(signal.QualifyingData, 256) : signal.QualifyingData is null);

    private static bool Matches(EventWaitRequest request, EventWaitSignal signal, string repository) =>
        signal.RepositoryId == repository && (signal.Outcome is "satisfied" or "target_gone" or "stranded") &&
        request.Kind == signal.Kind && request.Target == signal.Target &&
        (request.Kind switch
        {
            "peer_reply" => request.PeerSessionId == signal.PeerSessionId && request.ReplyTo == signal.ReplyTo,
            "task_completed" => request.BaselineRevision is { } baseline && TryRevision(signal.Revision, out var revision) &&
                revision > baseline && request.QualifyingData == signal.QualifyingData,
            "claim_released" => request.Revision == signal.Revision,
            _ => false
        });

    private static bool TryRevision(string value, out long revision) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out revision) && revision >= 0 && value == revision.ToString(CultureInfo.InvariantCulture);

    internal static void Terminal(AggregateContext context, JsonObject entry, EventWaitRequest request, string outcome, string revision)
    {
        if (entry["terminal"] is not null) return;
        var identity = JsonSerializer.Serialize(new[] { context.Repository, context.Key, request.WaitId });
        var id = "event-wait-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        var receipt = new JsonObject { ["waitId"] = request.WaitId, ["outcome"] = outcome, ["revision"] = revision,
            ["at"] = context.Now.ToString("O"), ["messageId"] = id };
        entry["terminal"] = receipt;
        context.Send(context.Key, new MessageDelivery(new InboxMessage { MessageId = id, RepositoryId = context.Repository,
            Kind = "event_wait_terminal", Author = "CoCern", SentAt = context.Now, Text = receipt.ToJsonString(), Wake = true }));
        EventWaitSources.EndWatch(context, entry, request);
    }

    private static RelayReply Reply(JsonObject entry) => Ok(new { registered = true, terminal = entry["terminal"]?.DeepClone() });
    private static RelayReply Ok(object data) => new(true, "Ok", JsonSerializer.Serialize(data), 0);
    private static RelayReply Error(string code) => new(false, code, "{}", 0);
}
