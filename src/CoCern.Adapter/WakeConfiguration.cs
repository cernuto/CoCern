using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoCern.Adapter;

public sealed record WakeRoute(string RepositoryId, string BoardId, string SessionFile, string ThreadId, string MachineId);
public sealed record WakeSession(string SessionId, string SessionToken);
/// <summary>The optional <c>wakeLimits</c> object of a wake configuration; any field left out takes its default.</summary>
public sealed record WakeLimitSettings(int? MinIntervalSeconds = null, int? MaxPerWindow = null, int? WindowMinutes = null);
public sealed record WakeConfiguration(string RelayUrl, string TokenFile, string CodexPath, string StatePath, string StopFile, WakeRoute[] Routes, [property: JsonPropertyName("wakeLimits")] WakeLimitSettings? Limits = null, string? Fingerprint = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    /// <summary>Loads a Codex wake configuration; the configured Codex executable must exist.</summary>
    public static WakeConfiguration Load(string path) => Load(path, requireCodex: true);

    /// <summary>Loads an inbox-waiter configuration: exactly one route, and no Codex executable is needed.</summary>
    public static WakeConfiguration LoadForAwait(string path)
    {
        var config = Load(path, requireCodex: false);
        return config.Routes.Length == 1 ? config : throw new AdapterConfigurationException("An inbox waiter enrolls exactly one route.");
    }

    private static WakeConfiguration Load(string path, bool requireCodex)
    {
        try
        {
            RequirePath(path);
            var config = JsonSerializer.Deserialize<WakeConfiguration>(ReadBounded(path, 65536), JsonOptions) ?? throw new JsonException();
            foreach (var file in new[] { config.TokenFile, config.StatePath, config.StopFile }) RequirePath(file);
            if (requireCodex && (!Path.IsPathFullyQualified(config.CodexPath ?? "") || !File.Exists(config.CodexPath))) throw new JsonException();
            if (config.StatePath == config.StopFile || config.Routes is not { Length: >= 1 and <= 16 }) throw new JsonException();
            _ = config.AdapterOptions();
            _ = config.ResolvedLimits();
            var threads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sessions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var route in config.Routes)
            {
                if (route is null || !ValidName(route.RepositoryId) || !ValidName(route.BoardId) ||
                    !string.Equals(route.MachineId, Environment.MachineName, StringComparison.OrdinalIgnoreCase) ||
                    !Guid.TryParseExact(route.ThreadId, "D", out var thread) || thread == Guid.Empty ||
                    !string.Equals(thread.ToString("D"), route.ThreadId, StringComparison.Ordinal) || !threads.Add(route.ThreadId)) throw new JsonException();
                RequirePath(route.SessionFile);
                if (!sessions.Add(ReadSession(route).SessionId)) throw new JsonException();
            }
            return config;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new AdapterConfigurationException("Invalid wake configuration or enrollment files.");
        }
    }

    /// <summary>The configured wake limits with defaults filled in; a value outside its allowed range makes the configuration invalid.</summary>
    public WakeLimits ResolvedLimits()
    {
        var defaults = WakeLimits.Default;
        if (Limits is null) return defaults;
        var interval = Limits.MinIntervalSeconds ?? (int)defaults.MinInterval.TotalSeconds;
        var count = Limits.MaxPerWindow ?? defaults.MaxPerWindow;
        var window = Limits.WindowMinutes ?? (int)defaults.Window.TotalMinutes;
        if (interval is < 0 or > 3600 || count is < 1 or > 1000 || window is < 1 or > 1440) throw new JsonException();
        return new(TimeSpan.FromSeconds(interval), count, TimeSpan.FromMinutes(window));
    }

    /// <summary>The configured pin takes precedence over <c>COCERN_FINGERPRINT</c>; an omitted pin uses the same environment setting as the MCP adapter.</summary>
    public AdapterOptions AdapterOptions() => new(RelayUrl, ReadBounded(TokenFile, 8192), signalR: true)
    {
        PinnedFingerprint = CoCern.Adapter.AdapterOptions.ParseFingerprint(RelayUrl, Fingerprint ?? Environment.GetEnvironmentVariable("COCERN_FINGERPRINT"))
    };
    public static WakeSession ReadSession(WakeRoute route)
    {
        try
        {
            var session = JsonSerializer.Deserialize<WakeSession>(ReadBounded(route.SessionFile, 16384), JsonOptions) ?? throw new JsonException();
            if (!ValidName(session.SessionId) || string.IsNullOrWhiteSpace(session.SessionToken) || session.SessionToken.Length > 8192 || session.SessionToken.Any(char.IsControl)) throw new JsonException();
            return session;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new AdapterConfigurationException("Invalid wake session enrollment.");
        }
    }
    private static bool ValidName(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && !value.Any(char.IsControl);
    private static void RequirePath(string? path) { if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path)) throw new ArgumentException(); }
    private static string ReadBounded(string path, int maximum)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > maximum) throw new IOException();
        using var reader = new StreamReader(stream);
        var value = reader.ReadToEnd();
        if (value.Length > maximum) throw new IOException();
        return value;
    }
}
