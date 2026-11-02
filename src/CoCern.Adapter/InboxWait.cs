using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;

namespace CoCern.Adapter;

/// <summary>
/// <c>check_inbox</c> with <c>waitSeconds</c>: the adapter re-reads the inbox itself until mail arrives or the wait ends, inside one tool call.
/// A chat that is mid-turn cannot be woken, so without this it polls, and every poll is a model call that re-reads its whole context.
/// The option is the adapter's own and never reaches the hub. Its polls send <c>attention: false</c>, so a waiting read never takes an offer.
/// </summary>
public static class InboxWait
{
    /// <summary>Kept under Codex's default 60-second MCP tool timeout, so a full wait never turns into a failed call.</summary>
    public const int MaxSeconds = 50;
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MinimumDelay = TimeSpan.FromMilliseconds(20);
    /// <summary>How far past the wait one poll may run: <see cref="MaxSeconds"/> plus this stays under Codex's 60 seconds.</summary>
    internal static readonly TimeSpan PollMargin = TimeSpan.FromSeconds(5);

    /// <summary>The property added to the advertised <c>check_inbox</c> schema.</summary>
    public static object Schema => new { type = "integer", minimum = 1, maximum = MaxSeconds, description = "Wait up to this many seconds for mail instead of polling; returns as soon as any message arrives." };

    public static async Task<AdapterReply> CheckAsync(RelayForwarder forwarder, JsonElement arguments, TimeSpan pollInterval, CancellationToken cancellationToken)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("waitSeconds", out var wait))
            return await forwarder.ForwardAsync(RelayTools.CheckInbox, arguments, cancellationToken);
        var forwarded = JsonNode.Parse(arguments.GetRawText())!.AsObject();
        forwarded.Remove("waitSeconds");
        // Most polls are discarded unseen, so none of them takes an attention offer: a waiting read carries none. The poll that returns
        // reaches the model, so the hub closes the info items of the messages on its page (seen); an empty poll lists none.
        forwarded["attention"] = false;
        forwarded["seen"] = true;
        var request = JsonSerializer.SerializeToElement(forwarded);
        var duration = TimeSpan.FromSeconds(wait.TryGetInt32(out var value) ? Math.Clamp(value, 0, MaxSeconds) : 0);
        // A monotonic clock, and no read for a remainder shorter than a timer tick: the wall clock and the delay timer disagree by a
        // millisecond or so, which otherwise left a sliver that looped through near-zero delays, reading the hub each time.
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        AdapterReply? last = null;
        while (true)
        {
            // A poll may itself take up to the client's timeout; bound it by the wait, answering with the last empty inbox if it overruns.
            using var poll = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var left = duration - elapsed.Elapsed;
            poll.CancelAfter((left > TimeSpan.Zero ? left : TimeSpan.Zero) + PollMargin);
            AdapterReply reply;
            try { reply = await forwarder.ForwardAsync(RelayTools.CheckInbox, request, poll.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return last ?? new(false, "hub_timeout"); }
            if (!reply.Ok && poll.IsCancellationRequested && !cancellationToken.IsCancellationRequested) return last ?? reply;
            var remaining = duration - elapsed.Elapsed;
            if (!reply.Ok || HasMail(reply) || remaining < MinimumDelay) return reply;
            last = reply;
            await Task.Delay(remaining < pollInterval ? remaining : pollInterval, cancellationToken);
        }
    }

    private static bool HasMail(AdapterReply reply) =>
        reply.Data is { ValueKind: JsonValueKind.Object } data && data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0;
}
