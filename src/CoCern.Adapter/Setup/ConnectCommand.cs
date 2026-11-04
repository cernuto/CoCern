using CoCern.Contracts;
using CoCern.Contracts.Dashboard;

namespace CoCern.Adapter.Setup;

/// <summary>
/// <c>--connect</c>: on the hub's own PC, adds CoCern to Claude Code, the Claude desktop app (when installed) and Codex, using the hub's
/// local token file. No join code or node: the adapter talks to the loopback hub directly. An existing <c>cocern</c> entry is left alone,
/// unless it is this hub's and names an address or certificate other than the hub's current ones, or <c>--role</c> asks for other tools than it
/// gives (see <see cref="Refresh"/>): then those are updated.
/// Exit codes: 0 done, 1 no token file found, 5 a client settings file could not be parsed (nothing is written then).
/// </summary>
internal static class ConnectCommand
{
    private const string DefaultTokenFile = "data/local-client-token";
    private const string Usage = "Use --connect [--token-file <path>] [--hub <url> [--fingerprint <sha256>]] [--only claude-code|claude-desktop|codex] [--role admin|worker] [--check] [--json].";

    /// <summary>The variable that makes a chat's adapter list the administrator tools as well (see <see cref="AdapterOptions"/>).</summary>
    private const string RoleVariable = "COCERN_ROLE";

    /// <summary>The variables a Connect entry carries; an entry with none of them is a joined PC's node entry, which is never this hub's to change.</summary>
    private static readonly string[] HubVariables = ["COCERN_TOKEN_FILE", "COCERN_TOKEN", "COCERN_URL", "COCERN_FINGERPRINT"];
    private static readonly string[] ManagedVariables = [.. HubVariables, RoleVariable];

    public static int Run(IReadOnlyList<string> args, string profile, string? appData, string dotnetPath, string adapterDll, TextWriter output, TextWriter error, TimeProvider? clock = null, string? nativeCacheRoot = null)
    {
        string? tokenFile = null, hub = null, only = null, fingerprint = null;
        bool check = false, json = false;
        bool? admin = null;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--token-file" when tokenFile is null && i + 1 < args.Count: tokenFile = args[++i]; break;
                case "--only" when only is null && i + 1 < args.Count && ConnectClients.All.Contains(args[i + 1]): only = args[++i]; break;
                case "--check" when !check: check = true; break;
                case "--role" when admin is null && i + 1 < args.Count && args[i + 1] is "admin" or "worker": admin = args[++i] == "admin"; break;
                case "--json" when !json: json = true; break;
                case "--fingerprint" when fingerprint is null && i + 1 < args.Count:
                    fingerprint = CertificateFingerprint.Normalize(args[++i])!;
                    if (!CertificateFingerprint.IsValid(fingerprint)) throw new AdapterConfigurationException("--fingerprint must be the hub certificate's SHA-256 fingerprint: 64 hexadecimal characters.");
                    break;
                case "--hub" when hub is null && i + 1 < args.Count:
                    if (!AdapterOptions.TryParseOrigin(args[++i], out var origin)) throw new AdapterConfigurationException("--hub must be a root https:// address, or http:// to this PC's loopback.");
                    hub = origin.ToString();
                    break;
                default: throw new AdapterConfigurationException(Usage);
            }
        }
        tokenFile = tokenFile is null ? FindTokenFile(Path.GetDirectoryName(adapterDll)!) : Path.GetFullPath(tokenFile);
        if (tokenFile is null || !File.Exists(tokenFile))
        {
            const string message = "CoCern isn't ready on this PC. Start the hub, then try Connect again.";
            if (json) output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { error = message })); else error.WriteLine(message);
            return 1;
        }
        var record = LocalHubRecord.Read(Path.GetDirectoryName(tokenFile)!);
        var localHub = PinnedLocalHub(record); // Older entries can still name this hub's HTTPS origin.
        if (hub is null && fingerprint is null && PreferredLocalHub(record) is { } local) (hub, fingerprint) = local;
        if (fingerprint is not null && hub?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) != true)
            throw new AdapterConfigurationException("--fingerprint needs an https:// --hub.");
        var environment = new Dictionary<string, string> { ["COCERN_TOKEN_FILE"] = tokenFile };
        if (hub is not null) environment["COCERN_URL"] = hub;
        if (fingerprint is not null) environment["COCERN_FINGERPRINT"] = fingerprint;
        if (admin == true) environment[RoleVariable] = "admin";
        // Per app: whether its existing entry gives the administrator tools (for the reply) and what this run changes in it (for the detail).
        // Without a current address (an older hub, no --hub) the address and certificate are left as they are; without --role, the tools.
        var seen = new Dictionary<string, (bool Admin, IReadOnlyDictionary<string, string?>? Changes)>(StringComparer.Ordinal);
        var owned = new HashSet<string>(StringComparer.Ordinal);
        var nativeEnvironments = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        EntryRefresh RefreshFor(string id) => variables =>
        {
            if (BelongsToHub(variables, tokenFile, hub, localHub?.Url)) owned.Add(id);
            var changes = Refresh(variables, tokenFile, hub, fingerprint, localHub?.Url, admin);
            if (owned.Contains(id))
            {
                var effective = variables.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
                if (changes is not null) foreach (var (key, value) in changes) { if (value is null) effective.Remove(key); else effective[key] = value; }
                nativeEnvironments[id] = effective;
            }
            seen[id] = (IsAdmin(variables), changes);
            return changes;
        };

        var clients = new List<(string Id, string Name, Func<ClientPlan> Plan)>
        {
            (ConnectClients.ClaudeCode, "Claude Code", () => ClientSettings.PlanClaudeCode(Path.Combine(profile, ".claude.json"), dotnetPath, adapterDll, environment, RefreshFor(ConnectClients.ClaudeCode))),
            (ConnectClients.Codex, "Codex", () => ClientSettings.PlanCodex(Path.Combine(profile, ".codex", "config.toml"), dotnetPath, adapterDll, environment, RefreshFor(ConnectClients.Codex)))
        };
        // The desktop app is offered only where it is installed; its config file may not exist yet.
        if (appData is not null && Directory.Exists(Path.Combine(appData, "Claude")))
            clients.Insert(1, (ConnectClients.ClaudeDesktop, "Claude desktop app", () => ClientSettings.PlanClaudeDesktop(Path.Combine(appData, "Claude", "claude_desktop_config.json"), dotnetPath, adapterDll, environment, RefreshFor(ConnectClients.ClaudeDesktop))));
        var results = new List<DashboardConnectClient>();
        var code = 0;
        foreach (var client in clients.Where(client => only is null || client.Id == only))
        {
            try
            {
                var plan = client.Plan();
                var hook = client.Id == ConnectClients.ClaudeCode && (!plan.IsUnchanged || owned.Contains(client.Id))
                    ? ClientSettings.PlanStopHook(Path.Combine(profile, ".claude", "settings.json"), SetupEnvironment.AdapterCommandLine(dotnetPath, adapterDll) + " --stop-hook")
                    : null;
                var state = plan.IsUnchanged ? ConnectStates.Connected : plan.Refreshes ? check ? ConnectStates.Outdated : ConnectStates.Updated : check ? ConnectStates.Available : ConnectStates.Added;
                if (state is ConnectStates.Added or ConnectStates.Updated)
                {
                    plan.Apply(clock ?? TimeProvider.System);
                }
                if (!check) hook?.Apply(clock ?? TimeProvider.System);
                if (!check && client.Id != ConnectClients.ClaudeDesktop && (!plan.IsUnchanged || owned.Contains(client.Id)))
                    ClientSettings.StageLocalNativeCandidate(client.Id == ConnectClients.Codex ? "codex" : "claude", dotnetPath, adapterDll,
                        nativeEnvironments.GetValueOrDefault(client.Id) ?? environment, nativeCacheRoot);
                var entry = seen.TryGetValue(client.Id, out var found) ? found : ((bool Admin, IReadOnlyDictionary<string, string?>? Changes)?)null;
                var changes = plan.Refreshes ? entry?.Changes : null;
                bool? gives = state switch
                {
                    ConnectStates.Added => admin == true,
                    ConnectStates.Updated when changes?.TryGetValue(RoleVariable, out var role) == true => role is not null,
                    ConnectStates.Available => null,
                    _ => entry?.Admin
                };
                results.Add(new() { Id = client.Id, Name = client.Name, State = state, Detail = changes is null ? null : Detail(check, changes), Admin = gives });
            }
            catch (Exception exception) when (exception is ClientSettingsException or IOException or UnauthorizedAccessException)
            {
                results.Add(new()
                {
                    Id = client.Id, Name = client.Name, State = ConnectStates.Error,
                    Detail = exception is ClientSettingsException ? exception.Message.Replace(" Fix or move the file, or use --skip-client-config.", " Fix or move the file, then try again.") : "Its settings file could not be written."
                });
                code = SetupCommand.MalformedClientFileExitCode;
            }
        }
        if (json) output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { clients = results }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        else
        {
            foreach (var result in results)
                (result.State == ConnectStates.Error ? error : output).WriteLine($"{result.Name}: {result.State switch { ConnectStates.Connected => "already connected", ConnectStates.Available => "not connected yet", ConnectStates.Added => "connected", ConnectStates.Outdated => "needs an update. " + result.Detail, ConnectStates.Updated => "updated (backup kept next to its settings file). " + result.Detail, _ => result.Detail }}{(result.Admin == true && result.State is ConnectStates.Connected or ConnectStates.Added ? ", with administrator tools" : "")}");
            if (!check && results.Any(result => result.State is ConnectStates.Added or ConnectStates.Updated)) output.WriteLine("Restart your chats (or the app) to load CoCern.");
        }
        return code;
    }

    /// <summary>What an entry for this hub needs (<paramref name="check"/>) or just got: its address and certificate, its tools, or both.</summary>
    private static string Detail(bool check, IReadOnlyDictionary<string, string?> changes)
    {
        var parts = new List<string>();
        if (changes.ContainsKey("COCERN_URL") || changes.ContainsKey("COCERN_FINGERPRINT"))
            parts.Add(check ? "needs an update to connect" : "now uses the current connection");
        if (changes.TryGetValue(RoleVariable, out var role))
            parts.Add((check, role is not null) switch
            {
                (true, true) => "doesn't give its chats the administrator tools",
                (true, false) => "gives its chats the administrator tools",
                (false, true) => "now gives its chats the administrator tools",
                (false, false) => "no longer gives its chats the administrator tools"
            });
        return $"Its CoCern entry for this hub {string.Join(" and ", parts)}. {(check ? "Connect updates it." : "Restart the app to use it.")}";
    }

    /// <summary>Whether an entry's variables make its chats list the administrator tools, as the adapter reads <c>COCERN_ROLE</c>.</summary>
    private static bool IsAdmin(IReadOnlyDictionary<string, string> variables) =>
        string.Equals(variables.GetValueOrDefault(RoleVariable), "admin", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What an existing <c>cocern</c> entry needs so it reaches this hub again: <c>COCERN_URL</c> set to <paramref name="hub"/> and
    /// <c>COCERN_FINGERPRINT</c> to <paramref name="fingerprint"/> (removed when the hub has none) wherever they differ, when the hub's address is
    /// known; and, when <paramref name="admin"/> is given, <c>COCERN_ROLE</c> set to <c>admin</c> or removed to match. Null when nothing differs
    /// or the entry is not this hub's. The hub's certificate can change (a new one, or a new place for it), and an entry still pinning the old one
    /// stops connecting. A local refresh never claims a supplied non-loopback or invalid origin. An eligible entry counts as this hub's when its token file is in this hub's data folder (beside <paramref name="tokenFile"/>);
    /// without a token file, when its address matches the requested destination, the recorded TLS origin, or the legacy default HTTP origin. A token file anywhere else
    /// is another hub's, and an entry with none of the CoCern variables is a node's; both are left alone, as is everything else in the entry.
    /// </summary>
    internal static IReadOnlyDictionary<string, string?>? Refresh(IReadOnlyDictionary<string, string> variables, string tokenFile, string? hub, string? fingerprint, string? localUrl, bool? admin = null)
    {
        if (!BelongsToHub(variables, tokenFile, hub, localUrl)) return null;
        var url = variables.GetValueOrDefault("COCERN_URL")?.Trim() is { Length: > 0 } given ? given : HubAddresses.LocalUrl;
        var pinned = CertificateFingerprint.Normalize(variables.GetValueOrDefault("COCERN_FINGERPRINT")) is { Length: > 0 } pin ? pin : null;
        var changes = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (hub is not null)
        {
            if (!(AdapterOptions.TryParseOrigin(url, out var current) && SameOrigin(current, hub))) changes["COCERN_URL"] = hub;
            if (pinned != fingerprint) changes["COCERN_FINGERPRINT"] = fingerprint;
        }
        if (admin is { } wanted && IsAdmin(variables) != wanted) changes[RoleVariable] = wanted ? "admin" : null;
        return changes.Count == 0 ? null : changes;
    }

    private static bool BelongsToHub(IReadOnlyDictionary<string, string> variables, string tokenFile, string? hub, string? localUrl)
    {
        // Windows will treat alternate spellings as the same variable when the client launches the adapter.
        if (OperatingSystem.IsWindows() && variables.Keys.Any(key => ManagedVariables.Contains(key, StringComparer.OrdinalIgnoreCase) && !ManagedVariables.Contains(key, StringComparer.Ordinal))) return false;
        if (!HubVariables.Any(variables.ContainsKey)) return false;
        if ((hub is null || HubAddresses.TryParseLoopbackOrigin(hub, out _)) &&
            variables.TryGetValue("COCERN_URL", out var supplied) && !HubAddresses.TryParseLoopbackOrigin(supplied, out _))
            return false; // A shared credential path cannot turn a remote or malformed entry into a local one.
        if (variables.GetValueOrDefault("COCERN_TOKEN_FILE")?.Trim() is { Length: > 0 } file)
            return InFolder(file, Path.GetDirectoryName(Path.GetFullPath(tokenFile))!);

        var url = variables.GetValueOrDefault("COCERN_URL")?.Trim() is { Length: > 0 } given ? given : HubAddresses.LocalUrl;
        return AdapterOptions.TryParseOrigin(url, out var origin) && (SameOrigin(origin, HubAddresses.LocalUrl)
            || hub is not null && SameOrigin(origin, hub) || localUrl is not null && SameOrigin(origin, localUrl));
    }

    private static bool SameOrigin(Uri origin, string other) => AdapterOptions.TryParseOrigin(other, out var parsed) && Uri.Compare(origin, parsed, UriComponents.AbsoluteUri, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0;

    /// <summary>Whether <paramref name="path"/>, an absolute file path, lies directly in <paramref name="folder"/>; a relative one names no folder for certain and never does.</summary>
    private static bool InFolder(string path, string folder)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path)) return false;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), comparison);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>The hub's loopback HTTPS address and certificate fingerprint from its <c>local-hub.json</c>, or null when it has none (an older hub).</summary>
    internal static (string Url, string Fingerprint)? LocalHub(string folder) => PinnedLocalHub(LocalHubRecord.Read(folder));

    internal static (string Url, string? Fingerprint)? PreferredLocalHub(LocalHubRecord? record)
    {
        if (PinnedLocalHub(record) is not { } pinned) return null;
        return record?.HttpUrl is { } http && HubAddresses.SelectLoopbackHttp([http]) is { } url
            ? (url, null) : pinned;
    }

    private static (string Url, string Fingerprint)? PinnedLocalHub(LocalHubRecord? record) =>
        record is not null && HubAddresses.TryParseLoopbackOrigin(record.Url, out var origin) && origin.Scheme == Uri.UriSchemeHttps
            && CertificateFingerprint.Normalize(record.Fingerprint) is { } fingerprint && CertificateFingerprint.IsValid(fingerprint)
            ? (origin.ToString(), fingerprint) : null;

    /// <summary>The hub's default token file, in the first folder at or above the adapter's that has one.</summary>
    internal static string? FindTokenFile(string start)
    {
        for (var folder = new DirectoryInfo(start); folder is not null; folder = folder.Parent)
        {
            var candidate = Path.GetFullPath(Path.Combine(folder.FullName, DefaultTokenFile));
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
