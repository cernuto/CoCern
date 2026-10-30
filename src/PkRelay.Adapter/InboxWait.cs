using System.Text.Json;
using System.Text.Json.Nodes;
using PkRelay.Contracts;

namespace PkRelay.Adapter;

/// <summary>
/// <c>check_inbox</c> with <c>waitSeconds</c>: the adapter re-reads the inbox itself until mail arrives or the wait ends, inside one tool call.
/// A chat that is mid-turn cannot be woken, so without this it polls, and every poll is a model call that re-reads its whole context.
/// The option is the adapter's own and never reaches the hub.
/// </summary>
public static class InboxWait
{
    /// <summary>Kept under Codex's default 60-second MCP tool timeout, so a full wait never turns into a failed call.</summary>
    public const int MaxSeconds = 50;
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>The property added to the advertised <c>check_inbox</c> schema.</summary>
    public static object Schema => new { type = "integer", minimum = 1, maximum = MaxSeconds, description = "Wait up to this many seconds for mail instead of polling; returns as soon as any message arrives." };

    public static async Task<AdapterReply> CheckAsync(RelayForwarder forwarder, JsonElement arguments, TimeSpan pollInterval, CancellationToken cancellationToken)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("waitSeconds", out var wait))
            return await forwarder.ForwardAsync(RelayTools.CheckInbox, arguments, cancellationToken);
        var forwarded = JsonNode.Parse(arguments.GetRawText())!.AsObject();
        forwarded.Remove("waitSeconds");
        var request = JsonSerializer.SerializeToElement(forwarded);
        var seconds = wait.TryGetInt32(out var value) ? Math.Clamp(value, 0, MaxSeconds) : 0;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(seconds);
        while (true)
        {
            var reply = await forwarder.ForwardAsync(RelayTools.CheckInbox, request, cancellationToken);
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (!reply.Ok || HasMail(reply) || remaining <= TimeSpan.Zero) return reply;
            await Task.Delay(remaining < pollInterval ? remaining : pollInterval, cancellationToken);
        }
    }

    private static bool HasMail(AdapterReply reply) =>
        reply.Data is { ValueKind: JsonValueKind.Object } data && data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0;
}
