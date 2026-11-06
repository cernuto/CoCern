using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;

namespace CoCern.Adapter;

/// <summary>
/// <c>check_inbox</c> with <c>waitSeconds</c>: the adapter re-reads the inbox until mail, an eligible attention wake, or the end of the wait.
/// A chat that is mid-turn cannot be woken, so without this it polls, and every poll is a model call that re-reads its whole context.
/// The option never reaches the hub. Internal polls never consume offers or mark mail seen; one final ordinary read supplies the model-visible page.
/// </summary>
public static class InboxWait
{
    /// <summary>Kept under Codex's default 60-second MCP tool timeout, so a full wait never turns into a failed call.</summary>
    public const int MaxSeconds = 50;
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan MinimumDelay = TimeSpan.FromMilliseconds(20);
    /// <summary>How far past the wait one poll may run: <see cref="MaxSeconds"/> plus this stays under Codex's 60 seconds.</summary>
    internal static readonly TimeSpan PollMargin = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FinalReadBudget = TimeSpan.FromSeconds(2);

    /// <summary>The property added to the advertised <c>check_inbox</c> schema.</summary>
    public static object Schema => new { type = "integer", minimum = 1, maximum = MaxSeconds, description = "Wait for mail or eligible attention instead of polling. The final read returns a current offer or pendingWork.reason; attention:false opts out of attention wakes." };

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
        // The ordinary result is shown to the model. Respect an explicit seen choice or a background reader's report.
        if (!forwarded.ContainsKey("seen") && (!forwarded.TryGetPropertyValue("backgroundCall", out var origin) || origin is not JsonValue originValue || !originValue.TryGetValue<bool>(out var background) || !background))
            forwarded["seen"] = true;
        var offeredRequest = JsonSerializer.SerializeToElement(forwarded);
        JsonObject? attentionRequest = null;
        if ((!arguments.TryGetProperty("attention", out var attention) || attention.ValueKind != JsonValueKind.False) &&
            arguments.TryGetProperty("sessionId", out var session) && session.ValueKind == JsonValueKind.String &&
            arguments.TryGetProperty("sessionToken", out var token) && token.ValueKind == JsonValueKind.String)
        {
            // These polls only look for a wake, so one that answers unchanged has none: a wake would have ended this wait, and a new one changes the tag.
            var status = new JsonObject { ["attentionDetail"] = true, ["backgroundCall"] = true, ["statusTag"] = "" };
            foreach (var key in new[] { "repositoryId", "boardId", "sessionId", "sessionToken" })
                if (forwarded.TryGetPropertyValue(key, out var value)) status[key] = value?.DeepClone();
            attentionRequest = status;
        }
        // Polls remain internal even when they discover mail. Only the final ordinary read may mark the returned page seen.
        forwarded["attention"] = false;
        forwarded["backgroundCall"] = true;
        forwarded["seen"] = false;
        forwarded.Remove("seenMessageIds");
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
                if (!reply.Ok && poll.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    return last is null ? reply : Unverified(last, "inbox_wait_timeout", stale: true);
                if (!reply.Ok) return reply;
                if (HasMail(reply) || duration - elapsed.Elapsed < MinimumDelay) return await FinishAsync(reply);
                last = reply;
                if (attentionRequest is { } statusRequest)
                {
                    // Status reads never consume offers. Only a hub-validated eligible wake ends the wait with one ordinary inbox read.
                    var status = await forwarder.ForwardAsync(RelayTools.GetStatus, JsonSerializer.SerializeToElement(statusRequest), poll.Token);
                    if (status.Ok && status.Data is { ValueKind: JsonValueKind.Object } data)
                    {
                        if (WakeReconciler.Attention(data).Wake is not null) return await FinishAsync(reply);
                        if (WakeReconciler.StatusTag(data) is { } tag) statusRequest["statusTag"] = tag;
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { return last is null ? new(false, "hub_timeout") : Unverified(last, "inbox_wait_timeout", stale: true); }
            var remaining = duration - elapsed.Elapsed;
            if (remaining < MinimumDelay) return await FinishAsync(reply);
            await Task.Delay(remaining < pollInterval ? remaining : pollInterval, cancellationToken);
        }

        async Task<AdapterReply> FinishAsync(AdapterReply polled)
        {
            using var completion = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // Fifty seconds waiting + five seconds poll margin + two seconds completion stays below the client's sixty-second timeout.
            completion.CancelAfter(FinalReadBudget);
            try
            {
                var final = await forwarder.ForwardAsync(RelayTools.CheckInbox, offeredRequest, completion.Token);
                return final.Ok ? final : Unverified(polled, final.Code);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { return Unverified(polled, "hub_timeout"); }
        }
    }

    private static AdapterReply Unverified(AdapterReply reply, string code, bool stale = false)
    {
        if (!reply.Ok || reply.Data is not { ValueKind: JsonValueKind.Object } data) return reply;
        var result = JsonNode.Parse(data.GetRawText())!.AsObject();
        result["pendingWork"] = new JsonObject { ["reason"] = "unavailable", ["code"] = code, ["retry"] = RelayTools.CheckInbox };
        if (stale) result["inboxStale"] = true;
        return reply with { Data = JsonSerializer.SerializeToElement(result) };
    }

    private static bool HasMail(AdapterReply reply) =>
        reply.Data is { ValueKind: JsonValueKind.Object } data && data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0;
}
