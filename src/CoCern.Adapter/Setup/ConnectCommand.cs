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

    /// <summary>
    /// One app CoCern can be added to. <c>connected</c>: it has a CoCern entry; <c>available</c>: it can get one; <c>added</c>: this run added it;
    /// <c>outdated</c>: its entry is this hub's but names an old address or certificate, or other tools than <c>--role</c> asks for (<c>--check</c>);
    /// <c>updated</c>: this run updated that entry; <c>error</c>: see the detail. <see cref="Admin"/> says whether its entry gives chats the
    /// administrator tools, once there is an entry; null when there is none or it could not be read.
    /// </summary>
    internal sealed record ClientState(string Id, string Name, string State, string? Detail = null, bool? Admin = null);

    /// <summary>The variables a Connect entry carries; an entry with none of them is a joined PC's node entry, which is never this hub's to change.</summary>
    private static readonly string[] HubVariables = ["COCERN_TOKEN_FILE", "COCERN_TOKEN", "COCERN_URL", "COCERN_FINGERPRINT"];

    public static int Run(IReadOnlyList<string> args, string profile, string? appData, string dotnetPath, string adapterDll, TextWriter output, TextWriter error, TimeProvider? clock = null)
    {
        string? tokenFile = null, hub = null, only = null, fingerprint = null;
        bool check = false, json = false;
        bool? admin = null;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--token-file" when tokenFile is null && i + 1 < args.Count: tokenFile = args[++i]; break;
                case "--only" when only is null && i + 1 < args.Count && args[i + 1] is "claude-code" or "claude-desktop" or "codex": only = args[++i]; break;
                case "--check" when !check: check = true; break;
                case "--role" when admin is null && i + 1 < args.Count && args[i + 1] is "admin" or "worker": admin = args[++i] == "admin"; break;
                case "--json" when !json: json = true; break;
                case "--fingerprint" when fingerprint is null && i + 1 < args.Count:
                    fingerprint = args[++i].Replace(":", "").Trim().ToUpperInvariant();
                    if (!Node.PinnedCertificate.IsFingerprint(fingerprint)) throw new AdapterConfigurationException("--fingerprint must be the hub certificate's SHA-256 fingerprint: 64 hexadecimal characters.");
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
            var message = $"No token file found{(tokenFile is null ? $" ({DefaultTokenFile} above this folder)" : $" at '{tokenFile}'")}. Start the hub once (it creates one), or pass --token-file <path>.";
            if (json) output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { error = message })); else error.WriteLine(message);
            return 1;
        }
        // Without an address given, the hub's own record beside the token: its loopback HTTPS and certificate, which the apps then pin.
        var localHub = LocalHub(Path.GetDirectoryName(tokenFile)!);
        if (hub is null && fingerprint is null && localHub is { } local) (hub, fingerprint) = local;
        if (fingerprint is not null && hub?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) != true)
            throw new AdapterConfigurationException("--fingerprint needs an https:// --hub.");
        var environment = new Dictionary<string, string> { ["COCERN_TOKEN_FILE"] = tokenFile };
        if (hub is not null) environment["COCERN_URL"] = hub;
        if (fingerprint is not null) environment["COCERN_FINGERPRINT"] = fingerprint;
        if (admin == true) environment[RoleVariable] = "admin";
        // Per app: whether its existing entry gives the administrator tools (for the reply) and what this run changes in it (for the detail).
        // Without a current address (an older hub, no --hub) the address and certificate are left as they are; without --role, the tools.
        var seen = new Dictionary<string, (bool Admin, IReadOnlyDictionary<string, string?>? Changes)>(StringComparer.Ordinal);
        EntryRefresh RefreshFor(string id) => variables =>
        {
            var changes = Refresh(variables, tokenFile, hub, fingerprint, localHub?.Url, admin);
            seen[id] = (IsAdmin(variables), changes);
            return changes;
        };

        var clients = new List<(string Id, string Name, Func<ClientPlan> Plan)>
        {
            ("claude-code", "Claude Code", () => ClientSettings.PlanClaudeCode(Path.Combine(profile, ".claude.json"), dotnetPath, adapterDll, environment, RefreshFor("claude-code"))),
            ("codex", "Codex", () => ClientSettings.PlanCodex(Path.Combine(profile, ".codex", "config.toml"), dotnetPath, adapterDll, environment, RefreshFor("codex")))
        };
        // The desktop app is offered only where it is installed; its config file may not exist yet.
        if (appData is not null && Directory.Exists(Path.Combine(appData, "Claude")))
            clients.Insert(1, ("claude-desktop", "Claude desktop app", () => ClientSettings.PlanClaudeDesktop(Path.Combine(appData, "Claude", "claude_desktop_config.json"), dotnetPath, adapterDll, environment, RefreshFor("claude-desktop"))));
        var results = new List<ClientState>();
        var code = 0;
        foreach (var client in clients.Where(client => only is null || client.Id == only))
        {
            try
            {
                var plan = client.Plan();
                var state = plan.IsUnchanged ? "connected" : plan.Refreshes ? check ? "outdated" : "updated" : check ? "available" : "added";
                if (state is "added" or "updated") plan.Apply(clock ?? TimeProvider.System);
                var entry = seen.TryGetValue(client.Id, out var found) ? found : ((bool Admin, IReadOnlyDictionary<string, string?>? Changes)?)null;
                var changes = plan.Refreshes ? entry?.Changes : null;
                bool? gives = state switch
                {
                    "added" => admin == true,
                    "updated" when changes?.TryGetValue(RoleVariable, out var role) == true => role is not null,
                    "available" => null,
                    _ => entry?.Admin
                };
                results.Add(new(client.Id, client.Name, state, changes is null ? null : Detail(check, changes), gives));
            }
            catch (Exception exception) when (exception is ClientSettingsException or IOException or UnauthorizedAccessException)
            {
                results.Add(new(client.Id, client.Name, "error", exception is ClientSettingsException ? exception.Message.Replace(" Fix or move the file, or use --skip-client-config.", " Fix or move the file, then try again.") : "Its settings file could not be written."));
                code = SetupCommand.MalformedClientFileExitCode;
            }
        }
        if (json) output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { clients = results }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        else
        {
            foreach (var result in results)
                (result.State == "error" ? error : output).WriteLine($"{result.Name}: {result.State switch { "connected" => "already connected", "available" => "not connected yet", "added" => "connected", "outdated" => "needs an update. " + result.Detail, "updated" => "updated (backup kept next to its settings file). " + result.Detail, _ => result.Detail }}{(result.Admin == true && result.State is "connected" or "added" ? ", with administrator tools" : "")}");
            if (!check && results.Any(result => result.State is "added" or "updated")) output.WriteLine("Restart your chats (or the app) to load CoCern.");
        }
        return code;
    }

    /// <summary>What an entry for this hub needs (<paramref name="check"/>) or just got: its address and certificate, its tools, or both.</summary>
    private static string Detail(bool check, IReadOnlyDictionary<string, string?> changes)
    {
        var parts = new List<string>();
        if (changes.ContainsKey("COCERN_URL") || changes.ContainsKey("COCERN_FINGERPRINT"))
            parts.Add(check ? "names an old address or certificate, so it cannot connect" : "now names this hub's current address and certificate");
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
    /// stops connecting. An entry counts as this hub's when its token file is in this hub's data folder (beside <paramref name="tokenFile"/>);
    /// without a token file, when its address is this hub's (<paramref name="hub"/>, or the hub's own record <paramref name="localUrl"/>), any
    /// plain-HTTP address on this PC (as before the hub had loopback HTTPS), or it pins this hub's current certificate. A token file anywhere else
    /// is another hub's, and an entry with none of the CoCern variables is a node's; both are left alone, as is everything else in the entry.
    /// </summary>
    internal static IReadOnlyDictionary<string, string?>? Refresh(IReadOnlyDictionary<string, string> variables, string tokenFile, string? hub, string? fingerprint, string? localUrl, bool? admin = null)
    {
        if (!HubVariables.Any(variables.ContainsKey)) return null;
        var url = variables.GetValueOrDefault("COCERN_URL")?.Trim() is { Length: > 0 } given ? given : AdapterOptions.DefaultUrl;
        var pinned = variables.GetValueOrDefault("COCERN_FINGERPRINT")?.Replace(":", "").Trim().ToUpperInvariant() is { Length: > 0 } pin ? pin : null;
        var ours = variables.GetValueOrDefault("COCERN_TOKEN_FILE")?.Trim() is { Length: > 0 } file
            ? InFolder(file, Path.GetDirectoryName(Path.GetFullPath(tokenFile))!)
            : AdapterOptions.TryParseOrigin(url, out var origin) && (origin.Scheme == Uri.UriSchemeHttp && origin.IsLoopback || hub is not null && SameOrigin(origin, hub) || localUrl is not null && SameOrigin(origin, localUrl))
              || fingerprint is not null && pinned == fingerprint;
        if (!ours) return null;
        var changes = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (hub is not null)
        {
            if (!(AdapterOptions.TryParseOrigin(url, out var current) && SameOrigin(current, hub))) changes["COCERN_URL"] = hub;
            if (pinned != fingerprint) changes["COCERN_FINGERPRINT"] = fingerprint;
        }
        if (admin is { } wanted && IsAdmin(variables) != wanted) changes[RoleVariable] = wanted ? "admin" : null;
        return changes.Count == 0 ? null : changes;
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
    internal static (string Url, string Fingerprint)? LocalHub(string folder)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "local-hub.json")));
            var url = document.RootElement.GetProperty("url").GetString();
            var fingerprint = document.RootElement.GetProperty("fingerprint").GetString()?.ToUpperInvariant();
            return url is not null && AdapterOptions.TryParseOrigin(url, out var origin) && origin.Scheme == Uri.UriSchemeHttps && fingerprint is not null && Node.PinnedCertificate.IsFingerprint(fingerprint)
                ? (origin.ToString(), fingerprint) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

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
