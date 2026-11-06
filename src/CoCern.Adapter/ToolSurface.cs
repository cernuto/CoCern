using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace CoCern.Adapter;

/// <summary>Tracks hub-authenticated settings and maintains the SDK tool collection.</summary>
public sealed class ToolSurface(McpServerPrimitiveCollection<McpServerTool> tools, bool native = false)
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, (long Revision, string? Profile, bool Administrator)> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> lastSeen = new(StringComparer.Ordinal);
    private Func<string?, bool, IEnumerable<McpServerTool>>? build;
    private long nextPoll;
    private bool selected;
    private string? activeSession;

    internal void Configure(Func<string?, bool, IEnumerable<McpServerTool>> factory) { lock (gate) build = factory; }
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
        lock (gate) return selected ? tools.PrimitiveNames.Contains(name) : null;
    }
    internal bool ShouldPoll()
    {
        var now = Environment.TickCount64;
        lock (gate) { if (now < nextPoll) return false; nextPoll = now + 30000; return true; }
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
        if (profile is not (null or "worker" or "chat" or "diagnostics" or "admin")) return;
        lock (gate)
        {
            if (build is null) return;
            if (!native && activeSession != session)
            {
                sessions.Clear();
                lastSeen.Clear();
            }
            if (sessions.TryGetValue(session, out var previous) && next < previous.Revision) return;
            lastSeen[session] = Environment.TickCount64;
            if (!sessions.ContainsKey(session) && sessions.Count >= 1024)
            {
                var oldest = sessions.Keys.OrderBy(key => lastSeen.GetValueOrDefault(key)).ThenBy(key => key, StringComparer.Ordinal).First();
                sessions.Remove(oldest);
                lastSeen.Remove(oldest);
            }
            var updated = (next, profile, ceiling.GetBoolean());
            if (sessions.TryGetValue(session, out previous) && previous == updated && (native || activeSession == session)) return;
            sessions[session] = updated;
            activeSession = session;
            // Null selection still respects the legacy options in the factory. An explicit hub selection replaces them.
            selected = native ? sessions.Values.Any(entry => entry.Profile is not null) : profile is not null;
            var choices = native ? sessions.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => entry.Value) : [updated];
            var replacement = choices.SelectMany(entry => build(entry.Profile, entry.Administrator))
                .GroupBy(tool => tool.ProtocolTool.Name, StringComparer.Ordinal).Select(group => group.First()).OrderBy(tool => tool.ProtocolTool.Name, StringComparer.Ordinal).ToArray();
            using (tools.DeferChangedEvents())
            {
                tools.Clear();
                foreach (var tool in replacement) tools.Add(tool);
            }
        }
    }
}
