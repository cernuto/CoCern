using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;

namespace CoCern.Adapter;

/// <summary>
/// Conditional reads for chats that re-read state on a schedule: a successful read reply carries a short <c>etag</c> over its data, and a read
/// that passes it back as <c>ifNoneMatch</c> gets only <c>{"unchanged":true,"etag":…}</c> while the data is the same, instead of the whole
/// payload again in its context. The adapter answers it itself; the hub sees only the etag, as <c>knownEtag</c>. check_inbox has its own wait and is excluded.
/// </summary>
public static class ConditionalRead
{
    private static readonly HashSet<string> Reads = [RelayTools.TaskGet, RelayTools.ChatDirectory, RelayTools.WorkerList, RelayTools.GetStatus, RelayTools.ContextGet, RelayTools.Peers, RelayTools.SessionContextGet];

    public static bool Applies(string tool) => Reads.Contains(tool);

    /// <summary>The unadvertised field that carries the agent's <c>ifNoneMatch</c> etag to the hub, which removes it before any aggregate.</summary>
    public const string KnownEtagField = "knownEtag";

    /// <summary>The property added to the advertised schema of each read it applies to.</summary>
    public static object Schema => new { type = "string", description = "The etag of your previous identical read; an unchanged result returns only {unchanged, etag}." };

    public static async Task<AdapterReply> ForwardAsync(RelayForwarder forwarder, string tool, JsonElement arguments, CancellationToken cancellationToken, Action<AdapterReply>? originalReply = null)
    {
        if (!Applies(tool) || arguments.ValueKind != JsonValueKind.Object) return await forwarder.ForwardAsync(tool, arguments, cancellationToken);
        string? ifNoneMatch = null;
        var request = arguments;
        if (arguments.TryGetProperty("ifNoneMatch", out var given))
        {
            ifNoneMatch = given.ValueKind == JsonValueKind.String ? given.GetString() : null;
            var forwarded = JsonNode.Parse(arguments.GetRawText())!.AsObject();
            forwarded.Remove("ifNoneMatch");
            // The hub's feedback rules leave a read that comes back unchanged out of their reread counts; they need only the etag.
            if (ifNoneMatch is { Length: > 0 and <= RelayLimits.MaxKnownEtagLength }) forwarded[KnownEtagField] = ifNoneMatch;
            request = JsonSerializer.SerializeToElement(forwarded);
        }
        var reply = await forwarder.ForwardAsync(tool, request, cancellationToken);
        originalReply?.Invoke(reply);
        if (!reply.Ok || reply.Data is not { ValueKind: JsonValueKind.Object } data) return reply;
        var etag = RelayEtag.Of(data.GetRawText());
        if (etag == ifNoneMatch) return reply with { Data = JsonSerializer.SerializeToElement(new { unchanged = true, etag }) };
        var tagged = JsonNode.Parse(data.GetRawText())!.AsObject();
        tagged["etag"] = etag;
        return reply with { Data = JsonSerializer.SerializeToElement(tagged) };
    }
}
