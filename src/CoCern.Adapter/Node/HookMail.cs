using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;

namespace CoCern.Adapter.Node;

/// <summary>
/// Mail for a Claude chat during an ordinary turn, shared by <see cref="PromptHook"/> and <see cref="PostToolHook"/>. It polls the inbox's headers with
/// <c>knownInboxEtag</c>, so a chat with nothing new costs one tiny hub read and prints nothing, and it reads the text only of messages the printed ledger does not hold.
/// A message counts as delivered only once its full text has been written: then it is recorded in the ledger and reported seen. A pointer or a "more not shown" count
/// does neither, because marking a message seen removes its direct-message wake, so one the model never read would never wake the chat. Everything here is bounded and quiet:
/// a hub that is down or slow, or a PC not enrolled, ends with no output. Other providers have no ledger or waiter here, so only Claude chats get mail.
/// </summary>
internal static class HookMail
{
    /// <summary>How long the text read may take once the headers have shown that mail waits, on top of the poll's own limit.</summary>
    private static readonly TimeSpan ReadLimit = TimeSpan.FromMilliseconds(1_500);
    /// <summary>How long the seen report may take, after the text has been written.</summary>
    private static readonly TimeSpan SeenLimit = TimeSpan.FromMilliseconds(500);
    private const int MaxPages = 20;

    /// <summary>
    /// Polls and, when there is mail, hands the text to <paramref name="print"/> at most once. Never throws: a hook must not fail a turn.
    /// </summary>
    /// <param name="nodeFolder">This PC's node folder; <paramref name="findLocal"/> may name the chat's own local connection folder before it, as the Stop hook's does, so both use one ledger.</param>
    /// <param name="limit">The most characters the text may have.</param>
    /// <param name="timeout">The limit on the header poll.</param>
    /// <param name="readByModel">Messages the model has just read with its own check_inbox; recorded first, so they are not repeated.</param>
    internal static async Task DeliverAsync(string chat, string nodeFolder, Func<string, string?>? findLocal, int limit, TimeSpan timeout, HttpMessageHandler? handler, Func<string, Task> print, IReadOnlyList<string>? readByModel = null)
    {
        try { await RunAsync(chat, findLocal?.Invoke(chat) ?? nodeFolder, limit, timeout, handler, print, readByModel).WaitAsync(timeout + ReadLimit + SeenLimit); }
        catch (Exception) { /* A hook must never trouble the chat; the next hook asks again. */ }
    }

    private static async Task RunAsync(string chat, string nodeFolder, int limit, TimeSpan timeout, HttpMessageHandler? handler, Func<string, Task> print, IReadOnlyList<string>? readByModel)
    {
        if (!ChatLocators.IsUuid(chat)) return;
        var threadId = Guid.ParseExact(chat, "D").ToString("D");
        var folder = Path.GetFullPath(nodeFolder);
        if (ClaudeWaiter.ConnectionOptions(folder) is not { } options || ClaudeWaiter.FindHandle(folder, threadId) is not { } handle) return;
        var route = WakeState.Key(handle.RepositoryId, handle.SessionId);
        var ledger = ClaudeWaiter.StatePath(folder, threadId);
        if (readByModel is { Count: > 0 }) Record(ledger, route, readByModel);

        // Parallel tool calls run this hook side by side; one poll is enough, and two would print the same mail twice.
        var hooks = Path.Combine(folder, PromptHook.CacheFolder);
        Directory.CreateDirectory(hooks);
        using var turn = TryTakeTurn(Path.Combine(hooks, threadId + ".mail.lock"));
        if (turn is null) return;

        using var http = new HttpClient(handler ?? RelayForwarder.CreateHandler(options), disposeHandler: handler is null) { Timeout = TimeSpan.FromSeconds(5) };
        var forwarder = new RelayForwarder(http, options);
        // The hook's reads are never the model's, so none of them takes an attention offer.
        Task<AdapterReply> Call(JsonObject arguments, CancellationToken cancellationToken)
        {
            arguments["attention"] = false;
            arguments["repositoryId"] = handle.RepositoryId; arguments["sessionId"] = handle.SessionId; arguments["sessionToken"] = handle.SessionToken;
            return forwarder.ForwardAsync(RelayTools.CheckInbox, RelayForwarder.WithOrigin(JsonSerializer.SerializeToElement(arguments), background: true), cancellationToken);
        }

        var statePath = Path.Combine(hooks, threadId + ".mail.json");
        var remembered = ReadState(statePath, route);
        using var poll = new CancellationTokenSource(timeout);
        if (await ReadHeadersAsync(Call, remembered.Etag, poll.Token) is not { Unchanged: false } headers) return;
        var inbox = headers.Ids.ToHashSet(StringComparer.Ordinal);
        var noticed = remembered.Noticed.Where(inbox.Contains).ToHashSet(StringComparer.Ordinal);
        var printed = WakeState.ReadPrinted(ledger, route, headers.Ids);
        var waiting = headers.Ids.Where(id => !printed.Contains(id) && !noticed.Contains(id)).ToArray();
        if (waiting.Length == 0)
        {
            SaveState(statePath, route, headers.Complete ? headers.Etag : null, noticed);
            return;
        }

        using var read = new CancellationTokenSource(ReadLimit);
        var fresh = await ReadFreshAsync(Call, headers.Ids, waiting, limit, read.Token);
        if (fresh.Count == 0) return;
        var shown = new List<string>();
        var pointed = new List<string>();
        var text = ClaudeWaiter.FormatInbox(fresh, waiting.Length, 0, handle.RepositoryId, shown, limit: limit, pointed: pointed);
        await print(text);

        // Only now is the text on its way: a record that fails costs one repeat, while one made too early would lose the message.
        Record(ledger, route, shown);
        // The etag answers "nothing new" only while nothing is left to print; a pointer is said once, and a message that did not fit comes next time.
        noticed.UnionWith(pointed);
        SaveState(statePath, route, headers.Complete && shown.Count + pointed.Count == waiting.Length ? headers.Etag : null, noticed);
        await ReportSeenAsync(Call, shown);
    }

    /// <summary>The right to poll for this chat now, or null while another of its hooks holds it. The operating system releases it even if the process is killed.</summary>
    private static FileStream? TryTakeTurn(string path)
    {
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
    }

    private static void Record(string ledger, string route, IReadOnlyCollection<string> ids)
    {
        if (ids.Count == 0) return;
        try { WakeState.RecordPrinted(ledger, route, ids); }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException) { /* One repeat at worst. */ }
    }

    /// <param name="Unchanged">The hub answered that the inbox is the one <see cref="ReadState"/> remembered.</param>
    /// <param name="Complete">Every page was read, so <paramref name="Etag"/> covers <paramref name="Ids"/>.</param>
    private sealed record Headers(bool Unchanged, string? Etag, IReadOnlyList<string> Ids, bool Complete);

    /// <summary>Every waiting message's ID, in inbox order; or null when the hub did not give a usable answer.</summary>
    private static async Task<Headers?> ReadHeadersAsync(Func<JsonObject, CancellationToken, Task<AdapterReply>> call, string? knownEtag, CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        string? etag = null;
        var cursor = 0;
        for (var page = 0; page < MaxPages; page++)
        {
            var arguments = new JsonObject { ["headersOnly"] = true, ["limit"] = RelayLimits.MaxPageSize, ["cursor"] = cursor };
            if (page == 0 && knownEtag is not null) arguments["knownInboxEtag"] = knownEtag;
            var reply = await call(arguments, cancellationToken);
            if (reply is not { Ok: true, Data: { ValueKind: JsonValueKind.Object } data }) return null;
            if (page == 0 && data.TryGetProperty("unchanged", out var unchanged) && unchanged.ValueKind == JsonValueKind.True) return new(true, knownEtag, [], true);
            if (!data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return null;
            if (page == 0 && data.TryGetProperty("etag", out var tag) && tag.ValueKind == JsonValueKind.String) etag = tag.GetString();
            foreach (var item in items.EnumerateArray())
                if (ClaudeWaiter.Field(item, "messageId") is { Length: > 0 } id) ids.Add(id);
            if (!data.TryGetProperty("nextCursor", out var next) || next.ValueKind != JsonValueKind.Number || !next.TryGetInt32(out var offset) || offset <= cursor) return new(false, etag, ids, true);
            cursor = offset;
        }
        return new(false, etag, ids, false);
    }

    /// <summary>The text of as many <paramref name="waiting"/> messages as may fit, starting at the first of them so printed messages ahead of it are not read again.</summary>
    private static async Task<List<JsonElement>> ReadFreshAsync(Func<JsonObject, CancellationToken, Task<AdapterReply>> call, IReadOnlyList<string> ids, string[] waiting, int limit, CancellationToken cancellationToken)
    {
        var wanted = waiting.ToHashSet(StringComparer.Ordinal);
        var fresh = new List<JsonElement>();
        var size = 0;
        var cursor = Math.Max(0, ids.ToList().IndexOf(waiting[0]));
        for (var page = 0; page < MaxPages && size <= limit && wanted.Count > 0; page++)
        {
            var reply = await call(new JsonObject { ["limit"] = RelayLimits.MaxPageSize, ["cursor"] = cursor }, cancellationToken);
            if (reply is not { Ok: true, Data: { ValueKind: JsonValueKind.Object } data } || !data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) break;
            foreach (var item in items.EnumerateArray())
            {
                if (!wanted.Remove(ClaudeWaiter.Field(item, "messageId"))) continue;
                fresh.Add(item.Clone());
                // One too long to print becomes a short pointer, so it must not end the reading.
                if (ClaudeWaiter.Field(item, "text").Length is var length && length <= limit) size += length;
            }
            if (!data.TryGetProperty("nextCursor", out var next) || next.ValueKind != JsonValueKind.Number || !next.TryGetInt32(out var offset) || offset <= cursor) break;
            cursor = offset;
        }
        return fresh;
    }

    /// <summary>Tells the hub these messages reached the model, which closes their info items. If it fails, a wake the hub still offers finds the ledger's record and stays silent.</summary>
    private static async Task ReportSeenAsync(Func<JsonObject, CancellationToken, Task<AdapterReply>> call, IReadOnlyList<string> shown)
    {
        if (shown.Count == 0) return;
        using var limit = new CancellationTokenSource(SeenLimit);
        foreach (var chunk in shown.Chunk(RelayLimits.MaxAcknowledgedMessages))
            await call(new JsonObject { ["limit"] = 1, ["seenMessageIds"] = new JsonArray(chunk.Select(id => (JsonNode)id).ToArray()) }, limit.Token);
    }

    /// <summary>What the last poll left for this route: the etag that means "nothing new", and the messages already pointed at, which are not said again.</summary>
    private sealed record MailState(string? Etag, IReadOnlySet<string> Noticed);

    private static MailState ReadState(string path, string route)
    {
        var empty = new MailState(null, new HashSet<string>(StringComparer.Ordinal));
        try
        {
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root || Text(root["route"]) != route) return empty;
            var noticed = root["noticed"] is JsonArray list ? list.Select(Text).OfType<string>().ToHashSet(StringComparer.Ordinal) : [];
            return new(Text(root["etag"]), noticed);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return empty; }
    }

    private static void SaveState(string path, string route, string? etag, IEnumerable<string> noticed)
    {
        try { NodeState.WritePrivately(path, System.Text.Encoding.UTF8.GetBytes(new JsonObject { ["route"] = route, ["etag"] = etag, ["noticed"] = new JsonArray(noticed.Select(id => (JsonNode)id).ToArray()) }.ToJsonString())); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* The next poll reads the headers again. */ }
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
