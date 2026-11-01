using System.Text.Json.Nodes;
using CoCern.Contracts;

namespace CoCern.Core;

/// <summary>
/// How much request and reply text queued activity events hold for counting. A call reserves its texts before attaching them; when the
/// total would pass the cap the call attaches none, so its event is still recorded, only without token counts. The text is released when
/// the writer counts it, or when the event is dropped.
/// </summary>
public sealed class TokenTextBudget(long maxChars)
{
    /// <summary>The hub's one budget, <see cref="RelayLimits.MaxQueuedTokenTextChars"/>.</summary>
    public static TokenTextBudget Shared { get; } = new(RelayLimits.MaxQueuedTokenTextChars);

    private long held;

    public long Held => Interlocked.Read(ref held);

    /// <summary>Reserves room for both texts, or for neither: true when they may be attached.</summary>
    public bool TryHold(string? request, string? reply)
    {
        var size = (long)(request?.Length ?? 0) + (reply?.Length ?? 0);
        if (size == 0) return true;
        if (Interlocked.Add(ref held, size) <= maxChars) return true;
        Interlocked.Add(ref held, -size);
        return false;
    }

    /// <summary>Gives back the room an event's texts held.</summary>
    public void Release(ActivityEvent activity)
    {
        var size = (long)(activity.RequestText?.Length ?? 0) + (activity.ReplyText?.Length ?? 0);
        if (size > 0) Interlocked.Add(ref held, -size);
    }
}

/// <summary>
/// Counts a call's request and reply tokens, off the call's path: the activity writer runs it on each queued call event that carries its
/// texts, just before the event is written. A reply over its tool's token budget adds a <c>budget</c> event with code <c>over</c>
/// (measure-only: the reply went out unchanged), and so do an attention offer over <see cref="RelayLimits.AttentionOfferTokens"/>
/// (<c>over:attention</c>) and a next-work offer over <see cref="RelayLimits.NextWorkOfferTokens"/> (<c>over:nextWork</c>).
/// </summary>
public static class TokenMeter
{
    public static IEnumerable<ActivityEvent> Measure(ActivityEvent call, ITokenCounter tokens)
    {
        if (call.RequestText is null && call.ReplyText is null) return [call];
        TokenTextBudget.Shared.Release(call);
        var events = new List<ActivityEvent>(2);
        int? request = call.RequestTokens, reply = call.ReplyTokens;
        try
        {
            if (call.RequestText is { } sent) request = tokens.Count(sent);
            if (call.ReplyText is { } text)
            {
                reply = tokens.Count(text);
                if (call.Ok && RelayToolCatalog.TryGet(call.Tool ?? "", out var tool))
                {
                    ActivityEvent Over(string code, int counted) => new()
                    {
                        Timestamp = call.Timestamp, Kind = ActivityEvent.Budget, RepositoryId = call.RepositoryId, SessionId = call.SessionId, Role = call.Role,
                        Tool = call.Tool, Ok = true, Code = code, ReplyTokens = counted
                    };
                    if (reply > tool.TokenBudget) events.Add(Over("over", reply.Value));
                    // Only replies that carry an offer are parsed again.
                    if (text.Contains("\"attention\":{", StringComparison.Ordinal) || text.Contains("\"nextWork\":", StringComparison.Ordinal))
                    {
                        var parsed = JsonNode.Parse(text);
                        if (parsed?["attention"] is JsonObject offer && tokens.Count(offer.ToJsonString()) is var attention && attention > RelayLimits.AttentionOfferTokens) events.Add(Over("over:attention", attention));
                        if (parsed?["nextWork"]?["offer"] is JsonObject work && tokens.Count(work.ToJsonString()) is var next && next > RelayLimits.NextWorkOfferTokens) events.Add(Over("over:nextWork", next));
                    }
                }
            }
        }
        catch (Exception) { /* Telemetry: an event that cannot be counted is written without counts. */ }
        events.Insert(0, call with { RequestTokens = request, ReplyTokens = reply, RequestText = null, ReplyText = null });
        return events;
    }
}
