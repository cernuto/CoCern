using System.Text.Json;
using CoCern.Adapter.Node;
using CoCern.Contracts;
using CoCern.Contracts.Dashboard;

namespace CoCern.Adapter.Setup;

/// <summary>Read-only diagnosis followed by explicitly requested repairs of owned setup, never enrollment or credential replacement.</summary>
internal static class RepairCommand
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    internal static async Task<int> RunAsync(IReadOnlyList<string> args, SetupEnvironment env, CancellationToken ct, string? nativeCacheRoot = null)
    {
        var check = false; var json = false; string? folder = null;
        var local = new List<string>();
        for (var i = 0; i < args.Count; i++)
            switch (args[i])
            {
                case "--check" when !check: check = true; break;
                case "--json" when !json: json = true; break;
                case "--data-dir" when folder is null && i + 1 < args.Count: folder = Path.GetFullPath(args[++i]); break;
                case "--token-file" or "--hub" or "--fingerprint" or "--repository" when i + 1 < args.Count:
                    local.Add(args[i]); local.Add(args[++i]); break;
                default: throw new AdapterConfigurationException("Use --repair [--check] [--json] [--data-dir <owned node folder>] or the hub's --token-file, --hub, --fingerprint and --repository options.");
            }
        DashboardRepairStatus result;
        try
        {
            var explicitToken = local.Contains("--token-file");
            var foundToken = explicitToken ? null : ConnectCommand.FindTokenFile(Path.GetDirectoryName(env.AdapterDll)!);
            if (!explicitToken && folder is null && foundToken is not null) local.AddRange(["--token-file", foundToken]);
            result = explicitToken || foundToken is not null && folder is null
                ? await LocalAsync(local, env, check, nativeCacheRoot, ct)
                : await NodeAsync(folder ?? NodeState.DefaultFolder, env, check, ct);
        }
        catch (Exception error) when (error is ClientSettingsException or IOException or UnauthorizedAccessException or AdapterConfigurationException or JsonException or System.Security.SecurityException)
        {
            result = new() { Unavailable = "CoCern setup could not be safely inspected or repaired. Existing credentials and unrelated configuration were retained.", NextAction = "Check file ownership and the current installation, then try again." };
        }
        if (json) env.Out.WriteLine(JsonSerializer.Serialize(result, Web));
        else
        {
            foreach (var item in result.Checks) env.Out.WriteLine($"{item.Name}: {item.State}. {item.Detail}");
            foreach (var change in result.PlannedChanges) env.Out.WriteLine("Planned: " + change);
            foreach (var change in result.AppliedChanges) env.Out.WriteLine("Repaired: " + change);
            env.Out.WriteLine(result.Unavailable ?? result.NextAction);
        }
        return result.Unavailable is null ? 0 : 1;
    }

    private static async Task<DashboardRepairStatus> LocalAsync(List<string> arguments, SetupEnvironment env, bool check, string? cacheRoot, CancellationToken ct)
    {
        var appData = SamePath(env.Profile, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) : null;
        DashboardConnectStatus Read(bool inspect, string? client = null)
        {
            using var output = new StringWriter(); using var errors = new StringWriter();
            var options = arguments.Concat(new[] { "--json" }).ToList();
            if (inspect) options.Add("--check");
            if (client is not null) options.AddRange(["--only", client]);
            var exit = ConnectCommand.Run(options, env.Profile, appData, env.DotnetPath, env.AdapterDll, output, errors, env.Clock, cacheRoot);
            using var document = JsonDocument.Parse(output.ToString());
            if (document.RootElement.TryGetProperty("error", out _)) throw new IOException("repair_connection_unavailable");
            var status = document.RootElement.Deserialize<DashboardConnectStatus>(Web) ?? throw new IOException("repair_connection_unavailable");
            if (exit != 0 && status.Clients.Length == 0) throw new IOException("repair_connection_unavailable");
            return status;
        }
        var before = Read(true); var checks = new List<DashboardRepairCheck>(); var plans = new List<(string Client, string Description)>();
        string? Argument(string name) => arguments.IndexOf(name) is var index && index >= 0 ? arguments[index + 1] : null;
        var localEnvironment = new Dictionary<string, string>();
        foreach (var mapping in new[] { ("--token-file", "COCERN_TOKEN_FILE"), ("--hub", "COCERN_URL"), ("--fingerprint", "COCERN_FINGERPRINT") })
            if (Argument(mapping.Item1) is { } value) localEnvironment[mapping.Item2] = value;
        var privateFolder = CodexNativeSetup.ConnectionFolder(localEnvironment, cacheRoot);
        var launchPlans = new Dictionary<string, ClientPlan>();
        checks.Add(Check("adapter", "Adapter executable", File.Exists(env.AdapterDll), "The running adapter is available.", "Repair from the downloaded CoCern package if executable files are missing."));
        foreach (var client in before.Clients)
        {
            var restored = client.State == ConnectStates.Available && client.Id == ConnectClients.Codex && privateFolder is not null && KnownNativeProfile(privateFolder, CodexNativeSetup.Profile(env.Profile));
            var owned = restored || client.State is ConnectStates.Connected or ConnectStates.Outdated;
            var needed = restored || owned && (client.State == ConnectStates.Outdated || client.Id == ConnectClients.Codex && client.Readiness is not { HooksInstalled: true, SkillInstalled: true });
            if (owned && client.Id != ConnectClients.Codex)
            {
                var path = client.Id == ConnectClients.ClaudeCode ? Path.Combine(env.Profile, ".claude.json") : Path.Combine(appData!, "Claude", "claude_desktop_config.json");
                var launch = ClientSettings.PlanOwnedJsonLaunch(path, env.DotnetPath, env.AdapterDll);
                if (!launch.IsUnchanged) { launchPlans[client.Id] = launch; needed = true; }
                if (client.Id == ConnectClients.ClaudeCode && !ClientSettings.PlanConnectStopHook(Path.Combine(env.Profile, ".claude", "settings.json"), path, env.DotnetPath, env.AdapterDll).IsUnchanged) needed = true;
            }
            checks.Add(new() { Id = client.Id, Name = client.Name + " connection", State = needed ? "needs_repair" : owned ? "passed" : "warning", Repairable = needed,
                Detail = owned ? needed ? "Owned settings need an update." : "Owned configuration is installed." : "Not owned by this hub or not yet connected; use Connect to choose this app." });
            if (needed) plans.Add((client.Id, restored ? "Restore the missing owned Codex connection with worker tools; administrator tools can be selected explicitly in Connect." : "Repair " + client.Name + " owned connection, hooks and workflow setup without changing its tool permissions."));
            if (client.Id == ConnectClients.Codex && owned) AddNativeChecks(checks, client.Readiness);
        }
        var repositoryIndex = arguments.IndexOf("--repository");
        {
            string? Value(string name) => arguments.IndexOf(name) is var index && index >= 0 ? arguments[index + 1] : null;
            var variables = new Dictionary<string, string?> { ["COCERN_TOKEN_FILE"] = Value("--token-file"), ["COCERN_URL"] = Value("--hub"), ["COCERN_FINGERPRINT"] = Value("--fingerprint") };
            if (variables["COCERN_URL"] is null && variables["COCERN_TOKEN_FILE"] is { } selectedToken && ConnectCommand.PreferredLocalHub(LocalHubRecord.Read(Path.GetDirectoryName(selectedToken)!)) is { } recorded)
            { variables["COCERN_URL"] = recorded.Url; variables["COCERN_FINGERPRINT"] = recorded.Fingerprint; }
            var connection = AdapterOptions.FromEnvironment(key => variables.GetValueOrDefault(key));
            using var http = new HttpClient(env.Transport.HubHandler?.Invoke() ?? RelayForwarder.CreateHandler(connection)) { Timeout = TimeSpan.FromSeconds(3) };
            if (repositoryIndex >= 0)
            {
                var forwarder = new RelayForwarder(http, connection);
                var reply = await forwarder.ForwardAsync(RelayTools.GetStatus, JsonSerializer.SerializeToElement(new { repositoryId = arguments[repositoryIndex + 1], backgroundCall = true }), ct);
                checks.Add(Check("hub-access", "Hub repository access", reply.Ok, "The configured credential reached the authorized repository.", "Hub access could not be verified. Inspect connection diagnostics for the failing phase."));
            }
            else
            {
                try { using var reply = await http.GetAsync(new Uri(connection.BaseUri, "health"), ct); checks.Add(Check("hub-transport", "Pinned hub connection", reply.IsSuccessStatusCode, "The configured hub answered. Repository authorization is checked separately after a genuine startup.", "The hub did not answer successfully; check its connection diagnostics.")); }
                catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException) { checks.Add(Check("hub-transport", "Pinned hub connection", false, "", "Hub is unavailable or its identity could not be verified. No repository or session was guessed.")); }
            }
        }
        var applied = new List<string>();
        if (!check)
        {
            foreach (var plan in plans)
            {
                var reply = Read(false, plan.Client);
                if (reply.Clients.Any(client => client.State == ConnectStates.Error)) throw new IOException("repair_client_refused");
                if (launchPlans.TryGetValue(plan.Client, out var launch)) ClientSettings.PlanOwnedJsonLaunch(launch.Path, env.DotnetPath, env.AdapterDll).Apply(env.Clock);
                applied.Add(plan.Description);
            }
            var refreshed = await LocalAsync(arguments, env, check: true, cacheRoot, ct);
            return refreshed with { AppliedChanges = applied.ToArray(), RepairPerformed = true,
                NextAction = applied.Count > 0 ? "Owned setup was repaired. Review changed hooks in Codex, resume a chat and refresh readiness." : refreshed.NextAction };
        }
        return new() { Checks = checks.ToArray(), PlannedChanges = plans.Select(x => x.Description).ToArray(), AppliedChanges = applied.ToArray(), RepairPerformed = !check,
            NextAction = applied.Count > 0 ? "Owned setup was repaired. Review changed hooks in Codex, resume a chat and refresh readiness." : "Use Connect for unconfigured apps. Review any pending hooks in Codex, then resume a chat to verify the session and wake route." };
    }

    private static async Task<DashboardRepairStatus> NodeAsync(string folder, SetupEnvironment env, bool check, CancellationToken ct)
    {
        folder = SetupCommand.NormalizeFolder(folder);
        if (NodeState.NativeCredentialIdentity(folder) is null || NativeJoinHook.ReadConnection(folder) is not { } connection)
            return new() { Unavailable = "No valid owned enrollment was found. Repair does not create or replace machine credentials.", NextAction = "Use Join in CoCern Setup to enroll this PC with its hub." };
        var checks = new List<DashboardRepairCheck> { Check("enrollment", "Machine enrollment", true, "Private enrollment and pinned hub identity are present.", "") };
        var plans = new List<(ClientPlan Plan, string Description)>();
        var nodeEnvironment = new Dictionary<string, string> { ["COCERN_NODE_DIR"] = folder };
        var codexProfile = CodexNativeSetup.Profile(env.Profile);
        bool codexOwned = false; bool claudeOwned = false; bool codexSeen = false;
        IReadOnlyDictionary<string, string?>? Capture(IReadOnlyDictionary<string, string> variables, bool codex)
        {
            var own = OwnsNodeEntry(variables, folder);
            if (codex) { codexOwned = own; codexSeen = true; } else claudeOwned = own;
            return null;
        }
        var codexPlan = ClientSettings.PlanCodex(Path.Combine(codexProfile, "config.toml"), env.DotnetPath, env.AdapterDll, nodeEnvironment, variables => Capture(variables, true));
        if (!codexSeen && KnownNativeProfile(folder, codexProfile)) { codexOwned = true; plans.Add((codexPlan, "Restore the missing owned Codex connection with worker tools; choose administrator tools explicitly if needed.")); }
        _ = ClientSettings.PlanClaudeCode(Path.Combine(env.Profile, ".claude.json"), env.DotnetPath, env.AdapterDll, nodeEnvironment, variables => Capture(variables, false));
        var native = codexOwned ? CodexNativeSetup.Inspect(env.Profile, codexProfile, nodeEnvironment) : null;
        checks.Add(new() { Id = "codex", Name = "Codex connection", State = codexOwned ? "passed" : "warning", Detail = codexOwned ? "Configuration belongs to this enrollment." : "Missing or another connection; choose Connect/Join to configure it explicitly." });
        checks.Add(new() { Id = "claude", Name = "Claude Code connection", State = claudeOwned ? "passed" : "warning", Detail = claudeOwned ? "Configuration belongs to this enrollment." : "Missing or another connection; unrelated settings are preserved." });
        var nativeNeeded = codexOwned && native is not { HooksInstalled: true, SkillInstalled: true };
        if (codexOwned) AddNativeChecks(checks, native);
        if (claudeOwned)
        {
            var launch = ClientSettings.PlanOwnedJsonLaunch(Path.Combine(env.Profile, ".claude.json"), env.DotnetPath, env.AdapterDll);
            if (!launch.IsUnchanged) plans.Add((launch, "Update Claude Code's owned adapter executable without changing its permissions."));
            var command = SetupEnvironment.AdapterCommandLine(env.DotnetPath, env.AdapterDll) + " --stop-hook --data-dir " + LogonStart.Quote(folder);
            var plan = ClientSettings.PlanStopHook(Path.Combine(env.Profile, ".claude", "settings.json"), command);
            checks.Add(new() { Id = "claude-wake", Name = "Claude wake hook", State = plan.IsUnchanged ? "passed" : "needs_repair", Repairable = !plan.IsUnchanged, Detail = plan.IsUnchanged ? "Owned wake hook is installed." : "Owned wake hook needs updating." });
            if (!plan.IsUnchanged) plans.Add((plan, "Repair Claude Code's owned wake hook."));
        }
        var startup = LogonStart.Command(env.DotnetPath, env.AdapterDll, folder);
        var currentStartup = env.RunKey.Get(LogonStart.ValueName);
        var canRepairStartup = currentStartup is null || OwnsStartup(currentStartup, folder, env.AdapterDll, env.DotnetPath);
        checks.Add(new() { Id = "startup", Name = "Node sign-in startup", State = currentStartup == startup ? "passed" : canRepairStartup ? "needs_repair" : "warning", Repairable = currentStartup != startup && canRepairStartup, Detail = currentStartup == startup ? "The node starts at sign-in." : canRepairStartup ? "The owned node startup registration needs updating." : "A different startup registration was retained." });
        using (var http = new HttpClient(env.Transport.HubHandler?.Invoke() ?? RelayForwarder.CreateHandler(connection)) { Timeout = TimeSpan.FromSeconds(3) })
        {
            try { using var reply = await http.GetAsync(new Uri(connection.BaseUri, "health"), ct); checks.Add(Check("hub", "Pinned hub connection", reply.IsSuccessStatusCode, "Hub answered over the configured connection.", "Hub did not answer successfully. Start the hub and check the network.")); }
            catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException) { checks.Add(Check("hub", "Pinned hub connection", false, "", "Hub is unavailable or its identity could not be verified. Existing credentials were kept.")); }
        }
        var proposed = plans.Select(x => x.Description).ToList();
        if (nativeNeeded) proposed.Add("Repair Codex's owned native hooks, workflow skill and private launch binding.");
        if (currentStartup != startup && canRepairStartup) proposed.Add("Repair this node's sign-in startup registration.");
        var ensureNode = native?.WakeReady != true;
        if (ensureNode) proposed.Add("Ensure this enrollment's background node is running; its existing exclusive owner prevents a duplicate worker.");
        var applied = new List<string>();
        if (!check)
        {
            foreach (var plan in plans) { plan.Plan.Apply(env.Clock); applied.Add(plan.Description); }
            if (nativeNeeded) { CodexNativeSetup.Prepare(env.Profile, codexProfile, env.DotnetPath, env.AdapterDll, nodeEnvironment, env.Clock); applied.Add("Repaired Codex's owned native setup."); }
            if (currentStartup != startup && canRepairStartup) { LogonStart.Register(env.DotnetPath, env.AdapterDll, folder, env.RunKey); applied.Add("Repaired node sign-in startup."); }
            if (ensureNode) { env.StartNode(env.DotnetPath, env.AdapterDll, folder); applied.Add("Ensured the enrolled background node is running."); }
            var refreshed = await NodeAsync(folder, env, check: true, ct);
            return refreshed with { AppliedChanges = applied.ToArray(), RepairPerformed = true,
                NextAction = "Owned setup was repaired. Review any changed hooks in Codex, resume a chat in an authorized repository, and refresh readiness. Enrollment credentials and existing sessions were preserved." };
        }
        return new() { Checks = checks.ToArray(), PlannedChanges = proposed.ToArray(), AppliedChanges = applied.ToArray(), RepairPerformed = !check,
            NextAction = "Review any changed hooks in Codex, resume a chat in an authorized repository, and refresh readiness. Enrollment credentials and existing sessions were preserved." };
    }

    internal static bool OwnsNodeEntry(IReadOnlyDictionary<string, string> variables, string folder) =>
        !variables.ContainsKey("COCERN_TOKEN") && !variables.ContainsKey("COCERN_TOKEN_FILE") && !variables.ContainsKey("COCERN_URL") &&
        (variables.TryGetValue("COCERN_NODE_DIR", out var selected) ? SamePath(selected, folder) : SamePath(folder, NodeState.DefaultFolder));

    internal static bool OwnsStartup(string command, string folder, string adapter, string dotnet)
    {
        if (command == LogonStart.Command(dotnet, adapter, folder)) return true;
        var suffix = " --node --data-dir " + LogonStart.Quote(folder);
        if (!command.EndsWith(suffix, StringComparison.Ordinal)) return false;
        var launch = command[..^suffix.Length];
        var match = System.Text.RegularExpressions.Regex.Match(launch, """^(?:"[^"]*dotnet(?:\.exe)?" |dotnet )?"(?<image>[^"]+CoCern\.Adapter\.(?:exe|dll))"$""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success || !Path.IsPathFullyQualified(match.Groups["image"].Value)) return false;
        var image = Path.GetFullPath(match.Groups["image"].Value);
        var root = Path.GetDirectoryName(folder)!;
        var packages = new DirectoryInfo(Path.GetDirectoryName(adapter)!);
        while (packages.Parent is not null && packages.Name != "packages" && packages.Name != "versions") packages = packages.Parent;
        var permittedRoot = packages.Name is "packages" or "versions" ? packages.FullName : root;
        return image.StartsWith(Path.TrimEndingDirectorySeparator(permittedRoot) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    internal static bool OwnsCodexNode(string profile, string folder, string dotnet, string adapter)
    {
        var owns = false;
        _ = ClientSettings.PlanCodex(Path.Combine(profile, "config.toml"), dotnet, adapter, refresh: variables => { owns = OwnsNodeEntry(variables, folder); return null; });
        return owns;
    }

    private static bool KnownNativeProfile(string folder, string profile)
    {
        var path = Path.Combine(folder, "native-client-codex.candidate.json");
        if (!File.Exists(path) || !NodeState.IsPrivateFile(path) || new FileInfo(path).Length > 65536) return false;
        using var data = JsonDocument.Parse(File.ReadAllBytes(path));
        return data.RootElement.TryGetProperty("helperVersion", out var version) && version.ValueKind == JsonValueKind.String &&
            NativeRegistration.Read(path, "codex", folder, profile, version.GetString()) is not null;
    }

    private static bool SamePath(string left, string right) => Path.IsPathFullyQualified(left) && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static DashboardRepairCheck Check(string id, string name, bool passed, string success, string failure) => new() { Id = id, Name = name, State = passed ? "passed" : "warning", Detail = passed ? success : failure };
    private static void AddNativeChecks(List<DashboardRepairCheck> checks, DashboardCodexReadiness? native)
    {
        checks.Add(new() { Id = "native-setup", Name = "Codex native setup", State = native is { HooksInstalled: true, SkillInstalled: true } ? "passed" : "needs_repair", Repairable = native is not { HooksInstalled: true, SkillInstalled: true }, Detail = native?.Summary ?? "Native hooks and workflow skill need setup." });
        checks.Add(Check("native-startup", "Trusted native startup", native?.StartupObserved == true, "A genuine native startup was observed.", "Review CoCern hooks in Codex and start or resume a chat; Repair cannot approve hook trust."));
        checks.Add(Check("session", "Native session", native?.SessionVerified == true, "The exact private session was verified with the hub.", "A genuine startup and a reachable hub are required; no replacement session was created."));
        checks.Add(Check("wake", "Wake route", native?.WakeReady == true, "The verified session has a live wake route.", "Keep the node and client running, then refresh readiness."));
    }
}
