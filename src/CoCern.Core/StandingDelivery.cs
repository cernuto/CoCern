using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain;
using CoCern.Domain.Messages;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Core;

/// <summary>
/// Delivers each board's standing to its sessions: once when a chat opens, then again only when it changed since that session last got
/// it. Reads are cached per board; see <see cref="cache"/> for why a write can never be undone by a read already in flight.
/// </summary>
internal sealed class StandingDelivery(Aggregates aggregates)
{
    /// <summary>
    /// Each board's last standing read (tick count, revision, delivered JSON), reused for <see cref="RelayLimits.StandingCacheSeconds"/>. An entry
    /// never moves to an older revision; a write leaves only its new revision (no JSON), so a read already in flight cannot restore stale standing.
    /// </summary>
    private readonly ConcurrentDictionary<string, (long At, long Revision, string? Json)> cache = new(StringComparer.Ordinal);

    /// <summary>A standing write through this hub: the next read goes to the board.</summary>
    public void Wrote(JsonObject args, long revision) => CacheStanding(Aggregates.BoardKey(args), 0, revision, null);

    /// <summary>A fresh chat receives the board's standing once, and its session records it so later replies leave it out.</summary>
    public async Task AttachAtOpenAsync(RelayCaller caller, JsonObject args, JsonObject data)
    {
        try
        {
            if (await ReadAsync(caller, args) is { } standing && Number(standing, "revision") > 0 && Attach(data, standing))
            {
                var session = args.DeepClone().AsObject(); session["sessionId"] = Text(data, "sessionId"); session["sessionToken"] = Text(data, "sessionToken");
                await MarkStandingSeenAsync(caller, session, Number(standing, "revision"));
            }
        }
        catch (Exception) { /* Standing is a convenience; the session is open either way. */ }
    }

    /// <summary>The board's current standing revision (for the prompt hook), or null when it cannot be read.</summary>
    public async Task<long?> RevisionAsync(RelayCaller caller, JsonObject args)
    {
        try { return await ReadAsync(caller, args) is { } standing ? Number(standing, "revision") : null; }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Calls whose reply carries changed standing. Not <c>check_inbox</c>: background readers discard those replies and cannot opt out, so
    /// standing could be recorded as received without reaching the model.
    /// </summary>
    public static bool Carries(string tool) => tool is RelayTools.TaskUpdate or RelayTools.ActionReport or RelayTools.TaskClaim;

    /// <summary>Attaches the board's standing when its revision is newer than the one this session last received. Never changes the committed result.</summary>
    public async Task<RelayReply> AttachIfChangedAsync(RelayCaller caller, JsonObject args, JsonObject session, RelayReply committed)
    {
        try
        {
            var seen = session["standingSeen"] is JsonObject boards ? Number(boards, Text(args, "boardId", "default")) : 0;
            if (await ReadAsync(caller, args) is not { } standing || Number(standing, "revision") <= seen) return committed;
            var data = Aggregates.Parse(committed);
            if (Attach(data, standing)) await MarkStandingSeenAsync(caller, args, Number(standing, "revision"));
            return Ok(data, committed.Version);
        }
        catch (Exception) { return committed; }
    }

    /// <summary>Attaches the full standing when the reply still fits; otherwise only its revision, and the text waits for a later, smaller reply.</summary>
    private static bool Attach(JsonObject data, JsonObject standing)
    {
        data["standing"] = standing.DeepClone();
        if (FitsWire(Ok(data, long.MaxValue))) return true;
        data["standing"] = new JsonObject { ["revision"] = Number(standing, "revision"), ["detailFetchRequired"] = true };
        return false;
    }

    private Task<RelayReply> MarkStandingSeenAsync(RelayCaller caller, JsonObject args, long revision) => aggregates.Session(caller, StandingSeen.Operation,
        new StandingSeen(Text(args, "repositoryId"), Text(args, "sessionId"), Text(args, "sessionToken"), Text(args, "boardId", "default"), revision).ToJson());

    /// <summary>The board's delivered standing, read at most once per <see cref="RelayLimits.StandingCacheSeconds"/>; a write through this hub clears it at once.</summary>
    private async Task<JsonObject?> ReadAsync(RelayCaller caller, JsonObject args)
    {
        var key = Aggregates.BoardKey(args);
        var now = Environment.TickCount64;
        if (cache.TryGetValue(key, out var cached) && cached.Json is not null && now - cached.At < RelayLimits.StandingCacheSeconds * 1000L) return JsonNode.Parse(cached.Json)!.AsObject();
        var read = await aggregates.Board(caller, StandingRead.Operation, new StandingRead(Text(args, "repositoryId"), Text(args, "boardId", "default")).ToJson());
        if (!read.Ok) return null;
        CacheStanding(key, now, read.Version, read.DataJson);
        return Aggregates.Parse(read);
    }

    private void CacheStanding(string key, long at, long revision, string? json)
    {
        if (cache.Count >= RelayLimits.StandingCacheBoards && !cache.ContainsKey(key)) cache.Clear();
        cache.AddOrUpdate(key, (at, revision, json), (_, old) => revision >= old.Revision ? (at, revision, json) : old);
    }
}
