using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;

namespace CoCern.Adapter;

/// <summary>
/// <c>check_inbox</c> with <c>waitSeconds</c>: the adapter re-reads the inbox until mail, an eligible attention wake, or the end of the wait.
/// A chat that is mid-turn cannot be woken, so without this it polls, and every poll is a model call that re-reads its whole context.
/// The option never reaches the hub. Internal polls send <c>attention: false</c>; only the read returned for an attention wake takes an offer.
/// </summary>
public static class InboxWait
{
    /// <summary>Kept under Codex's default 60-second MCP tool timeout, so a full wait never turns into a failed call.</summary>
    public const int MaxSeconds = 50;
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan MinimumDelay = TimeSpan.FromMilliseconds(20);
    /// <summary>How far past the wait one poll may run: <see cref="MaxSeconds"/> plus this stays under Codex's 60 seconds.</summary>
    internal static readonly TimeSpan PollMargin = TimeSpan.FromSeconds(5);

    /// <summary>The property added to the advertised <c>check_inbox</c> schema.</summary>
    public static object Schema => new { type = "integer", minimum = 1, maximum = MaxSeconds, description = "Wait up to this many seconds for mail or an eligible attention wake instead of polling; attention:false opts out of attention wakes." };

    /// <summary>A number, or a quoted one (models send both); anything else waits for nothing rather than failing the read.</summary>
    internal static int WaitSeconds(JsonElement wait) => wait.ValueKind switch
    {
        JsonValueKind.Number when wait.TryGetInt32(out var number) => number,
        JsonValueKind.String when int.TryParse(wait.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var quoted) => quoted,
        _ => 0
    };

    public static async Task<AdapterReply> CheckAsync(RelayForwarder forwarder, JsonElement arguments, TimeSpan pollInterval, CancellationToken cancellationToken)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("waitSeconds", out var wait))
            return await forwarder.ForwardAsync(RelayTools.CheckInbox, arguments, cancellationToken);
        var forwarded = JsonNode.Parse(arguments.GetRawText())!.AsObject();
        forwarded.Remove("waitSeconds");
        var offeredRequest = JsonSerializer.SerializeToElement(forwarded);
        JsonElement? attentionRequest = null;
        if ((!arguments.TryGetProperty("attention", out var attention) || attention.ValueKind != JsonValueKind.False) &&
            arguments.TryGetProperty("sessionId", out var session) && session.ValueKind == JsonValueKind.String &&
            arguments.TryGetProperty("sessionToken", out var token) && token.ValueKind == JsonValueKind.String)
        {
            var status = new JsonObject { ["attentionDetail"] = true, ["backgroundCall"] = true };
            foreach (var key in new[] { "repositoryId", "boardId", "sessionId", "sessionToken" })
                if (forwarded.TryGetPropertyValue(key, out var value)) status[key] = value?.DeepClone();
            attentionRequest = JsonSerializer.SerializeToElement(status);
        }
        // Most polls are discarded unseen, so none of them takes an attention offer. The poll that returns
        // reaches the model, so the hub closes the info items of the messages on its page (seen); an empty poll lists none.
        forwarded["attention"] = false;
        forwarded["backgroundCall"] = true;
        forwarded["seen"] = true;
        var request = JsonSerializer.SerializeToElement(forwarded);
        var duration = TimeSpan.FromSeconds(Math.Clamp(WaitSeconds(wait), 0, MaxSeconds));
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
            try
            {
                reply = await forwarder.ForwardAsync(RelayTools.CheckInbox, request, poll.Token);
                if (!reply.Ok && poll.IsCancellationRequested && !cancellationToken.IsCancellationRequested) return last ?? reply;
                if (!reply.Ok || HasMail(reply) || duration - elapsed.Elapsed < MinimumDelay) return reply;
                last = reply;
                if (attentionRequest is { } statusRequest)
                {
                    // Status reads never consume offers. Only a hub-validated eligible wake ends the wait with one ordinary inbox read.
                    var status = await forwarder.ForwardAsync(RelayTools.GetStatus, statusRequest, poll.Token);
                    if (status.Ok && status.Data is { ValueKind: JsonValueKind.Object } data && WakeReconciler.Attention(data).Wake is not null)
                        return await forwarder.ForwardAsync(RelayTools.CheckInbox, offeredRequest, poll.Token);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return last ?? new(false, "hub_timeout"); }
            var remaining = duration - elapsed.Elapsed;
            if (remaining < MinimumDelay) return reply;
            await Task.Delay(remaining < pollInterval ? remaining : pollInterval, cancellationToken);
        }
    }

    private static bool HasMail(AdapterReply reply) =>
        reply.Data is { ValueKind: JsonValueKind.Object } data && data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0;
}
