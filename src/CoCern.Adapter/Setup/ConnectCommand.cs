namespace CoCern.Adapter.Setup;

/// <summary>
/// <c>--connect</c>: on the hub's own PC, adds CoCern to Claude Code, the Claude desktop app (when installed) and Codex, using the hub's
/// local token file. No join code or node: the adapter talks to the loopback hub directly. Existing <c>cocern</c> entries are left alone.
/// Exit codes: 0 done, 1 no token file found, 5 a client settings file could not be parsed (nothing is written then).
/// </summary>
internal static class ConnectCommand
{
    private const string DefaultTokenFile = "data/local-client-token";
    private const string Usage = "Use --connect [--token-file <path>] [--hub <url> [--fingerprint <sha256>]] [--only claude-code|claude-desktop|codex] [--check] [--json].";

    /// <summary>One app CoCern can be added to. <c>connected</c>: it has a CoCern entry; <c>available</c>: it can get one; <c>added</c>: this run added it.</summary>
    internal sealed record ClientState(string Id, string Name, string State, string? Detail = null);

    public static int Run(IReadOnlyList<string> args, string profile, string? appData, string dotnetPath, string adapterDll, TextWriter output, TextWriter error, TimeProvider? clock = null)
    {
        string? tokenFile = null, hub = null, only = null, fingerprint = null;
        bool check = false, json = false;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--token-file" when tokenFile is null && i + 1 < args.Count: tokenFile = args[++i]; break;
                case "--only" when only is null && i + 1 < args.Count && args[i + 1] is "claude-code" or "claude-desktop" or "codex": only = args[++i]; break;
                case "--check" when !check: check = true; break;
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
        if (hub is null && fingerprint is null && LocalHub(Path.GetDirectoryName(tokenFile)!) is { } local) (hub, fingerprint) = local;
        if (fingerprint is not null && hub?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) != true)
            throw new AdapterConfigurationException("--fingerprint needs an https:// --hub.");
        var environment = new Dictionary<string, string> { ["COCERN_TOKEN_FILE"] = tokenFile };
        if (hub is not null) environment["COCERN_URL"] = hub;
        if (fingerprint is not null) environment["COCERN_FINGERPRINT"] = fingerprint;

        var clients = new List<(string Id, string Name, Func<ClientPlan> Plan)>
        {
            ("claude-code", "Claude Code", () => ClientSettings.PlanClaudeCode(Path.Combine(profile, ".claude.json"), dotnetPath, adapterDll, environment)),
            ("codex", "Codex", () => ClientSettings.PlanCodex(Path.Combine(profile, ".codex", "config.toml"), dotnetPath, adapterDll, environment))
        };
        // The desktop app is offered only where it is installed; its config file may not exist yet.
        if (appData is not null && Directory.Exists(Path.Combine(appData, "Claude")))
            clients.Insert(1, ("claude-desktop", "Claude desktop app", () => ClientSettings.PlanClaudeDesktop(Path.Combine(appData, "Claude", "claude_desktop_config.json"), dotnetPath, adapterDll, environment)));
        var results = new List<ClientState>();
        var code = 0;
        foreach (var client in clients.Where(client => only is null || client.Id == only))
        {
            try
            {
                var plan = client.Plan();
                var state = plan.IsUnchanged ? "connected" : check ? "available" : "added";
                if (state == "added") plan.Apply(clock ?? TimeProvider.System);
                results.Add(new(client.Id, client.Name, state));
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
                (result.State == "error" ? error : output).WriteLine($"{result.Name}: {result.State switch { "connected" => "already connected", "available" => "not connected yet", "added" => "connected", _ => result.Detail }}");
            if (!check && results.Any(result => result.State == "added")) output.WriteLine("Restart your chats (or the app) to load CoCern.");
        }
        return code;
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
            var candidate = Path.Combine(folder.FullName, DefaultTokenFile);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
