using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Adapter.Node;
using CoCern.Contracts;
using ModelContextProtocol.Server;

namespace CoCern.Adapter;

/// <summary>Tracks hub-authenticated settings and maintains the SDK tool collection.</summary>
/// <param name="memoryPath">
/// A native backend's private file beside its registration. Clients such as Codex list tools once and ignore <c>tools/list_changed</c>, so the
/// last hub selection seen through this registration is kept here and served by the next backend before its first <c>tools/list</c>
/// (<see cref="Restore"/>). It is a remembered hub answer, never authority: the hub still enforces the profile and the credential, and the
/// first hub answer for a session replaces what was remembered for it, whatever its revision.
/// </param>
public sealed class ToolSurface(McpServerPrimitiveCollection<McpServerTool> tools, bool native = false, string? memoryPath = null)
{
    private const int MaxRemembered = 64;
    private const int MaxMemoryBytes = 65536;
    private static readonly TimeSpan MemoryLifetime = TimeSpan.FromDays(14);

    private readonly Lock gate = new();
    private readonly Dictionary<string, (long Revision, string? Profile, bool Administrator)> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> lastSeen = new(StringComparer.Ordinal);
    // Native only: when the hub last answered for each session, and which entries came from the memory file without a hub answer in this process.
    private readonly Dictionary<string, DateTimeOffset> observedAt = new(StringComparer.Ordinal);
    private readonly HashSet<string> remembered = new(StringComparer.Ordinal);
    private Func<string?, bool, IEnumerable<McpServerTool>>? build;
    private Func<string?, bool, IReadOnlySet<string>>? onDemandNames;
    // The on-demand tools the current choices allow, beside the collection: reached through tool_call, never listed. Null until the first Compose.
    private IReadOnlySet<string>? onDemand;
    private long nextPoll;
    private bool selected;
    private string? activeSession;

    private sealed record RememberedSelection(string Session, long Revision, string? Profile, bool Administrator, DateTimeOffset ObservedAt);

    /// <param name="onDemand">For a profile and ceiling, the on-demand tool names the factory left out of its catalog but <c>tool_call</c> may reach; empty without on-demand tools.</param>
    internal void Configure(Func<string?, bool, IEnumerable<McpServerTool>> factory, Func<string?, bool, IReadOnlySet<string>> onDemand)
    {
        lock (gate) { build = factory; onDemandNames = onDemand; }
    }
    internal static AdapterReply StripRouting(AdapterReply reply)
    {
        if (reply.Data is not { ValueKind: JsonValueKind.Object } data ||
            (!data.TryGetProperty("toolSettingsSessionId", out _) && !data.TryGetProperty("toolSettingsAdministrator", out _))) return reply;
        var result = JsonNode.Parse(data.GetRawText())!.AsObject();
        result.Remove("toolSettingsSessionId");
        result.Remove("toolSettingsAdministrator");
        return reply with { Data = JsonSerializer.SerializeToElement(result) };
    }
    internal static AdapterReply WithRefreshGuidance(AdapterReply reply)
    {
        if (reply.Data is not { ValueKind: JsonValueKind.Object } data || !data.TryGetProperty("toolSettings", out var settings) ||
            settings.ValueKind != JsonValueKind.Object || !settings.TryGetProperty("profile", out var profile) || profile.ValueKind != JsonValueKind.String) return reply;
        var result = JsonNode.Parse(data.GetRawText())!.AsObject();
        result["toolSettingsRefresh"] = "The tool list may have changed. If your client retains its previous list, reconnect it.";
        return reply with { Data = JsonSerializer.SerializeToElement(result) };
    }
    internal bool? Allows(string name)
    {
        lock (gate) return selected ? tools.PrimitiveNames.Contains(name) || onDemand?.Contains(name) == true : null;
    }

    /// <summary>The on-demand tool names the current choices allow, or null before any hub selection was composed (the factory's default stands).</summary>
    internal IReadOnlySet<string>? OnDemandNames
    {
        get { lock (gate) return onDemand; }
    }
    internal bool ShouldPoll()
    {
        var now = Environment.TickCount64;
        lock (gate) { if (now < nextPoll) return false; nextPoll = now + 30000; return true; }
    }

    /// <summary>
    /// The catalog a native backend starts with: the hub selections remembered for this registration (<paramref name="memoryPath"/>), or null when
    /// none selects a profile, so the factory's default stands. Call after <see cref="Configure"/> and before the collection is served.
    /// </summary>
    internal IReadOnlyList<McpServerTool>? Restore()
    {
        if (!native || memoryPath is null) return null;
        var saved = ReadMemory(memoryPath);
        lock (gate)
        {
            if (build is null || saved.Count == 0) return null;
            var now = Environment.TickCount64;
            foreach (var entry in saved)
            {
                sessions[entry.Session] = (entry.Revision, entry.Profile, entry.Administrator);
                observedAt[entry.Session] = entry.ObservedAt;
                lastSeen[entry.Session] = now;
                remembered.Add(entry.Session);
            }
            selected = sessions.Values.Any(entry => entry.Profile is not null);
            return selected ? Compose([.. sessions.Values]) : null;
        }
    }

    internal void Observe(AdapterReply reply, string? expectedSession = null)
    {
        if (reply.Data is not { ValueKind: JsonValueKind.Object } data ||
            !(data.TryGetProperty("toolSettingsSessionId", out var id) || data.TryGetProperty("sessionId", out id)) || id.ValueKind != JsonValueKind.String ||
            !data.TryGetProperty("toolSettingsAdministrator", out var ceiling) || ceiling.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !data.TryGetProperty("toolSettings", out var settings) || settings.ValueKind != JsonValueKind.Object ||
            !settings.TryGetProperty("revision", out var version) || !version.TryGetInt64(out var next) || next < 0 ||
            !settings.TryGetProperty("profile", out var value) || value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) return;
        var session = id.GetString();
        if (string.IsNullOrEmpty(session) || (expectedSession is not null && session != expectedSession)) return;
        var profile = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (!KnownProfile(profile)) return;
        lock (gate)
        {
            if (build is null) return;
            if (!native && activeSession != session)
            {
                sessions.Clear();
                lastSeen.Clear();
                observedAt.Clear();
                remembered.Clear();
            }
            // A remembered selection is only the last answer seen; the hub's current answer replaces it even at a lower revision (a reset hub).
            var wasRemembered = remembered.Remove(session);
            if (!wasRemembered && sessions.TryGetValue(session, out var previous) && next < previous.Revision) return;
            lastSeen[session] = Environment.TickCount64;
            if (!sessions.ContainsKey(session) && sessions.Count >= 1024)
            {
                var oldest = sessions.Keys.OrderBy(key => lastSeen.GetValueOrDefault(key)).ThenBy(key => key, StringComparer.Ordinal).First();
                sessions.Remove(oldest);
                lastSeen.Remove(oldest);
                observedAt.Remove(oldest);
                remembered.Remove(oldest);
            }
            var updated = (next, profile, ceiling.GetBoolean());
            var unchanged = sessions.TryGetValue(session, out previous) && previous == updated && (native || activeSession == session);
            sessions[session] = updated;
            activeSession = session;
            if (native && (wasRemembered || !unchanged)) observedAt[session] = DateTimeOffset.UtcNow;
            if (unchanged)
            {
                if (wasRemembered) Remember();
                return;
            }
            // Null selection still respects the legacy options in the factory. An explicit hub selection replaces them.
            selected = native ? sessions.Values.Any(entry => entry.Profile is not null) : profile is not null;
            var replacement = Compose(native ? sessions.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => entry.Value).ToArray() : [updated]);
            // Change only what differs, so a call racing the rebuild never finds a tool both lists share missing.
            var wanted = replacement.Select(tool => tool.ProtocolTool.Name).ToHashSet(StringComparer.Ordinal);
            using (tools.DeferChangedEvents())
            {
                foreach (var stale in tools.ToArray().Where(tool => !wanted.Contains(tool.ProtocolTool.Name))) tools.Remove(stale);
                foreach (var tool in replacement) tools.TryAdd(tool);
            }
            Remember();
        }
    }

    private static bool KnownProfile(string? profile) => profile is null or "worker" or "chat" or "diagnostics" or "admin";

    /// <summary>
    /// The union of the factory's catalogs for <paramref name="choices"/>, one tool per name, and beside it the union of their on-demand names.
    /// Called under the gate.
    /// </summary>
    private McpServerTool[] Compose(IReadOnlyList<(long Revision, string? Profile, bool Administrator)> choices)
    {
        onDemand = choices.SelectMany(entry => onDemandNames!(entry.Profile, entry.Administrator)).ToHashSet(StringComparer.Ordinal);
        return choices.SelectMany(entry => build!(entry.Profile, entry.Administrator))
            .GroupBy(tool => tool.ProtocolTool.Name, StringComparer.Ordinal).Select(group => group.First()).OrderBy(tool => tool.ProtocolTool.Name, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Writes the native selections the hub answered for, newest first and bounded. Advisory: a failed write leaves the next start on the default. Called under the gate.</summary>
    private void Remember()
    {
        if (!native || memoryPath is null) return;
        var entries = sessions.Where(entry => observedAt.ContainsKey(entry.Key))
            .Select(entry => new RememberedSelection(entry.Key, entry.Value.Revision, entry.Value.Profile, entry.Value.Administrator, observedAt[entry.Key]))
            .OrderByDescending(entry => entry.ObservedAt).ThenBy(entry => entry.Session, StringComparer.Ordinal).Take(MaxRemembered).ToArray();
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(entries);
            if (bytes.Length <= MaxMemoryBytes) NodeState.WritePrivately(memoryPath, bytes);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The remembered selections, or none when the file is absent, not private, too large, malformed or out of date.</summary>
    private static IReadOnlyList<RememberedSelection> ReadMemory(string path)
    {
        try
        {
            if (!NodeState.IsPrivateFile(path) || new FileInfo(path).Length > MaxMemoryBytes) return [];
            var bytes = File.ReadAllBytes(path);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Array || !NativeJoinHook.Unique(document.RootElement)) return [];
            var saved = JsonSerializer.Deserialize<RememberedSelection[]>(document.RootElement) ?? [];
            var now = DateTimeOffset.UtcNow;
            var valid = saved.Where(entry => entry is not null && entry.Session is { Length: > 0 and <= RelayLimits.MaxIdentifierLength } &&
                    !entry.Session.Any(char.IsControl) && entry.Revision >= 0 && KnownProfile(entry.Profile) &&
                    entry.ObservedAt > now - MemoryLifetime && entry.ObservedAt <= now.AddMinutes(5))
                .ToArray();
            if (valid.Select(entry => entry.Session).Distinct(StringComparer.Ordinal).Count() != valid.Length) return [];
            return valid.OrderByDescending(entry => entry.ObservedAt).Take(MaxRemembered).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return []; }
    }
}
