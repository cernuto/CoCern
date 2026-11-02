using System.ComponentModel;
using System.Diagnostics;
using CoCern.Adapter.Node;

namespace CoCern.Adapter.Setup;

/// <summary>What <c>--setup</c> was asked to do.</summary>
internal sealed record SetupOptions(Uri Hub, bool SkipClientConfig, string? DataDir, bool PromptHook = false, string? JoinCode = null, string? Fingerprint = null)
{
    public const string JoinCodeVariable = "COCERN_JOIN_CODE";
    private const string Usage = "Use --setup --hub <https-url> --join-code <code> [--fingerprint <sha256>] [--skip-client-config] [--prompt-hook] [--data-dir <path>].";

    /// <summary>Parses the arguments after <c>--setup</c>. <c>--hub</c> is required; without <c>--join-code</c>, <paramref name="joinCodeFallback"/> (from <see cref="JoinCodeVariable"/>) is used.</summary>
    public static SetupOptions Parse(IReadOnlyList<string> args, string? joinCodeFallback = null)
    {
        Uri? hub = null;
        string? dataDir = null, joinCode = null, fingerprint = null;
        var skip = false;
        var hook = false;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--skip-client-config" when !skip: skip = true; break;
                case "--prompt-hook" when !hook: hook = true; break;
                case "--hub" when hub is null && i + 1 < args.Count && !string.IsNullOrWhiteSpace(args[i + 1]):
                    if (!AdapterOptions.TryParseOrigin(args[++i], out var typed)) throw new AdapterConfigurationException("--hub must be a root https:// address, or http:// to this PC's loopback, without credentials, query, or fragment.");
                    hub = typed;
                    break;
                case "--data-dir" when dataDir is null && i + 1 < args.Count && !string.IsNullOrWhiteSpace(args[i + 1]): dataDir = args[++i]; break;
                case "--join-code" when joinCode is null && i + 1 < args.Count && !string.IsNullOrWhiteSpace(args[i + 1]): joinCode = args[++i].Trim(); break;
                case "--fingerprint" when fingerprint is null && i + 1 < args.Count:
                    // Accepts the hub's logged form and the colon-separated form certificate viewers show.
                    fingerprint = args[++i].Replace(":", "").Trim().ToUpperInvariant();
                    if (!PinnedCertificate.IsFingerprint(fingerprint)) throw new AdapterConfigurationException("--fingerprint must be the hub certificate's SHA-256 fingerprint: 64 hexadecimal characters, as the hub logs it.");
                    break;
                default: throw new AdapterConfigurationException(Usage);
            }
        }
        if (hub is null) throw new AdapterConfigurationException(Usage);
        if (fingerprint is not null && hub.Scheme != Uri.UriSchemeHttps) throw new AdapterConfigurationException("--fingerprint applies only to an https:// hub.");
        return new(hub, skip, dataDir, hook, joinCode ?? (string.IsNullOrWhiteSpace(joinCodeFallback) ? null : joinCodeFallback.Trim()), fingerprint);
    }
}

/// <summary>Everything setup touches outside its own arguments, so tests can substitute all of it.</summary>
internal sealed class SetupEnvironment
{
    public required string Profile { get; init; }
    public required string DotnetPath { get; init; }
    public required string AdapterDll { get; init; }
    public required IRunKey RunKey { get; init; }
    public required NodeTransport Transport { get; set; }
    /// <summary>Starts <c>dotnet &lt;dll&gt; --node [--data-dir]</c> detached.</summary>
    public required Action<string, string, string?> StartNode { get; init; }
    public TimeSpan EnrollTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeProvider Clock { get; init; } = TimeProvider.System;
    public TextWriter Out { get; init; } = Console.Out;
    public TextWriter Error { get; init; } = Console.Error;
    /// <summary>Claude Code's own command line when this PC has one; null makes setup edit .claude.json itself. Tests leave it null or pass a fake, so a real installation is never touched.</summary>
    public ClaudeCli? ClaudeCli { get; init; }
    /// <summary>Whether Claude Code is running now. Only the .claude.json fallback asks, to say that a restart is needed; nothing waits on it.</summary>
    public Func<bool> ClaudeCodeRunning { get; init; } = () => false;

    /// <summary>The real thing: this user's profile, the dotnet host running setup, the published adapter next to it, the Run key, the claude launcher if any.</summary>
    public static SetupEnvironment Create()
    {
        var (dotnet, dll) = Host();
        return new()
        {
            Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            DotnetPath = dotnet,
            AdapterDll = dll,
            RunKey = OperatingSystem.IsWindows() ? new WindowsRunKey() : throw new AdapterConfigurationException("Setup is supported on Windows only."),
            Transport = new NodeTransport(),
            StartNode = StartDetached,
            ClaudeCli = ClaudeCli.Find(),
            ClaudeCodeRunning = IsClaudeCodeRunning
        };
    }

    /// <summary>A process named claude or Claude belongs to this user's Claude Code (or desktop app); a process list this user cannot read counts as not running.</summary>
    private static bool IsClaudeCodeRunning()
    {
        foreach (var name in new[] { "claude", "Claude" })
        {
            try
            {
                var processes = Process.GetProcessesByName(name);
                var any = processes.Length > 0;
                foreach (var process in processes) process.Dispose();
                if (any) return true;
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or PlatformNotSupportedException) { }
        }
        return false;
    }

    /// <summary>The folders selected by the caller of <c>--connect</c>, including explicitly isolated process environments.</summary>
    public static (string Profile, string? AppData) ConnectFolders()
    {
        var profileVariables = OperatingSystem.IsWindows() ? new[] { "USERPROFILE", "HOME" } : new[] { "HOME", "USERPROFILE" };
        var profile = ConnectFolder(Environment.SpecialFolder.UserProfile, profileVariables)
            ?? throw new AdapterConfigurationException("The user profile folder could not be found. Set USERPROFILE or HOME to an absolute folder.");
        return (profile, ConnectFolder(Environment.SpecialFolder.ApplicationData, ["APPDATA"]));
    }

    private static string? ConnectFolder(Environment.SpecialFolder folder, string[] variables)
    {
        foreach (var variable in variables)
        {
            if (Environment.GetEnvironmentVariable(variable) is not { } value) continue;
            if (!string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value))
            {
                try { return Path.GetFullPath(value); }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { }
            }
            throw new AdapterConfigurationException($"{variable} must name a nonblank absolute folder for --connect.");
        }
        return Environment.GetFolderPath(folder) is { Length: > 0 } known ? known : null;
    }

    /// <summary>The dotnet host running this command and the adapter beside it: what client settings start.</summary>
    public static (string Dotnet, string Dll) Host()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "CoCern.Adapter.dll");
        if (!File.Exists(dll)) throw new AdapterConfigurationException($"'{dll}' was not found. Run it from the published adapter folder.");
        var host = Environment.ProcessPath;
        var dotnet = host is not null && string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase) ? host
            : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } hostPath && File.Exists(hostPath) ? hostPath
            // Started as CoCern.Adapter.exe (the self-contained publish, where there may be no dotnet at all): the exe is what every entry names, so the host is never used.
            : AdapterLaunch("dotnet", dll).Arguments.Count == 0 ? "dotnet"
            : throw new AdapterConfigurationException("Run it as 'CoCern.Adapter.exe ...' or 'dotnet CoCern.Adapter.dll ...' so the host to start CoCern with is known.");
        return (dotnet, dll);
    }

    /// <summary>
    /// What starts the adapter on this PC: <c>CoCern.Adapter.exe</c> alone when one is beside <paramref name="adapterDll"/> (the self-contained publish a joined PC
    /// downloads from the hub, which needs no .NET install), else <paramref name="dotnetPath"/> with the dll as its one argument. The Claude Code, Claude desktop and
    /// Codex entries, <c>claude mcp add</c>, the prompt hook, the Run key and the node start all go through here, so they agree.
    /// </summary>
    public static (string Command, IReadOnlyList<string> Arguments) AdapterLaunch(string dotnetPath, string adapterDll)
    {
        var exe = Path.Combine(Path.GetDirectoryName(adapterDll) ?? "", "CoCern.Adapter.exe");
        return File.Exists(exe) ? (exe, []) : (dotnetPath, [adapterDll]);
    }

    /// <summary>The start of the adapter's command line as one string, every path quoted (for the Run key and hook files): <c>"exe"</c>, or <c>"dotnet" "dll"</c>.</summary>
    public static string AdapterCommandLine(string dotnetPath, string adapterDll)
    {
        var (command, arguments) = AdapterLaunch(dotnetPath, adapterDll);
        return string.Join(' ', arguments.Prepend(command).Select(LogonStart.Quote));
    }

    /// <summary>Shell-launched with a hidden window, so the node outlives this console and shares none of its handles.</summary>
    private static void StartDetached(string dotnet, string dll, string? dataDir)
    {
        var (command, launchArguments) = AdapterLaunch(dotnet, dll);
        var arguments = string.Join(' ', launchArguments.Select(LogonStart.Quote).Append("--node")) + (dataDir is null ? "" : $" --data-dir {LogonStart.Quote(dataDir)}");
        using var _ = Process.Start(new ProcessStartInfo(command, arguments) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true });
    }
}

/// <summary>
/// The one-time per-PC setup. Order matters so that a failure leaves the PC as it was (apart from the node folder):
/// 1 check both client files can be edited (read only); 2 enroll and announce once; 3 edit client files; 4 write the sign-in value; 5 start the node.
/// Exit codes: 0 done, 1 configuration problem or enrollment failure (the node's own code, e.g. 4 for a changed hub certificate), 5 a client settings file could not be parsed.
/// </summary>
internal static class SetupCommand
{
    public const int MalformedClientFileExitCode = 5;

    public static async Task<int> RunAsync(SetupOptions options, SetupEnvironment env, CancellationToken ct)
    {
        var folder = NormalizeFolder(options.DataDir);
        // Planned once now only to refuse a file we cannot edit before enrolling; each file is planned again from its fresh bytes just before it is written.
        // Claude Code's file is the exception when its own command line is on this PC: that registers the entry, and the file is only read if the command line fails.
        var planners = new List<(Func<ClientPlan> Plan, bool ClaudeCode)>();
        if (!options.SkipClientConfig)
        {
            planners.Add((() => ClientSettings.PlanCodex(Path.Combine(env.Profile, ".codex", "config.toml"), env.DotnetPath, env.AdapterDll), false));
            planners.Add((() => ClientSettings.PlanClaudeCode(Path.Combine(env.Profile, ".claude.json"), env.DotnetPath, env.AdapterDll), true));
            // The prompt hook is opt-in: one status line per human message, at no tool call for the model.
            if (options.PromptHook)
            {
                var command = $"{SetupEnvironment.AdapterCommandLine(env.DotnetPath, env.AdapterDll)} --prompt-hook" + (options.DataDir is null ? "" : $" --data-dir {LogonStart.Quote(folder)}");
                planners.Add((() => ClientSettings.PlanPromptHook(Path.Combine(env.Profile, ".claude", "settings.json"), command), false));
                planners.Add((() => ClientSettings.PlanPromptHook(Path.Combine(env.Profile, ".codex", "hooks.json"), command), false));
            }
        }
        try { foreach (var (planner, claudeCode) in planners) if (!claudeCode || env.ClaudeCli is null) planner(); }
        catch (ClientSettingsException exception) { env.Error.WriteLine(exception.Message); return MalformedClientFileExitCode; }

        var enrolled = await EnrollAsync(options, env, folder, ct);
        if (enrolled.Code != 0) return enrolled.Code;

        try
        {
            // Planned again from the files as they are now, so anything written during enrollment survives; all are checked before any is written.
            var fresh = planners.Select(entry => (Plan: entry.ClaudeCode && env.ClaudeCli is not null ? null : entry.Plan(), entry.Plan, entry.ClaudeCode)).ToList();
            foreach (var (planned, planner, claudeCode) in fresh)
            {
                if (claudeCode && env.ClaudeCli is { } cli && RegisterWithClaudeCli(cli, env)) continue;
                var plan = planned ?? planner();
                env.Out.WriteLine(plan.Apply(env.Clock) switch { ClientChange.Created => $"Created {plan.Path} with the CoCern entry.", ClientChange.Updated => $"Added the CoCern entry to {plan.Path} (backup kept next to it).", _ => $"{plan.Path} already has a CoCern entry; left as it was." });
                if (claudeCode && !plan.IsUnchanged && env.ClaudeCodeRunning()) env.Out.WriteLine("Claude Code appears to be running: restart it to pick up the CoCern entry.");
            }
            LogonStart.Register(env.DotnetPath, env.AdapterDll, options.DataDir is null ? null : folder, env.RunKey);
            env.StartNode(env.DotnetPath, env.AdapterDll, options.DataDir is null ? null : folder);
        }
        catch (ClientSettingsException exception) { env.Error.WriteLine(exception.Message); return MalformedClientFileExitCode; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or System.Security.SecurityException or AdapterConfigurationException)
        {
            env.Error.WriteLine($"Setup enrolled this PC but could not finish: {exception.Message}");
            return 1;
        }
        env.Out.WriteLine($"CoCern is set up. Machine ID: {enrolled.MachineId}");
        env.Out.WriteLine($"Hub: {enrolled.Hub}");
        env.Out.WriteLine("The node was started in the background and will start at every sign-in.");
        return 0;
    }

    /// <summary>
    /// Registers the same server the file plan would write (no variables for an enrolled node) through <c>claude mcp add</c>, after <c>claude mcp get</c> says there is none.
    /// False when the command line did not succeed, so the caller edits .claude.json instead. One line says which way was taken; the join code is never printed.
    /// </summary>
    private static bool RegisterWithClaudeCli(ClaudeCli cli, SetupEnvironment env)
    {
        switch (cli.Register(env.DotnetPath, env.AdapterDll, environment: null))
        {
            case ClaudeRegistration.Existing: env.Out.WriteLine("Claude Code already has a CoCern entry ('claude mcp get'); left as it was."); return true;
            case ClaudeRegistration.Added: env.Out.WriteLine($"Registered the CoCern entry with Claude Code through 'claude mcp add' ({cli.Executable})."); return true;
            default: env.Out.WriteLine($"'claude mcp add' ({cli.Executable}) did not succeed or timed out; writing .claude.json instead."); return false;
        }
    }

    /// <summary>The full data folder without a trailing separator (a drive root such as D:\ keeps its one).</summary>
    internal static string NormalizeFolder(string? dataDir)
    {
        var full = Path.GetFullPath(string.IsNullOrWhiteSpace(dataDir) ? NodeState.DefaultFolder : dataDir);
        return full.Length > (Path.GetPathRoot(full)?.Length ?? 0) ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }

    private static async Task<(int Code, string MachineId, string Hub)> EnrollAsync(SetupOptions options, SetupEnvironment env, string folder, CancellationToken ct)
    {
        NodeState state;
        try { state = NodeState.Open(folder); }
        catch (AdapterConfigurationException exception)
        {
            // A node already runs on this folder (it holds the lock): setup run again on an enrolled PC changes nothing there.
            if (NodeState.ReadIdentity(folder) is { } identity && new Uri(identity.HubUrl).GetLeftPart(UriPartial.Authority).Equals(options.Hub.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
                return (0, identity.MachineId, identity.HubUrl);
            env.Error.WriteLine(exception.Message);
            return (1, "", "");
        }
        using (state)
        {
            env.Transport.SingleCycle = true;
            if (options.JoinCode is not null) { env.Transport.JoinCode = options.JoinCode; state.SaveJoinCode(options.JoinCode); }
            if (options.Fingerprint is not null) env.Transport.ExpectedFingerprint = options.Fingerprint;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(env.EnrollTimeout);
            var code = await NodeRunner.RunAsync(state, options.Hub, env.Transport, env.Error, timeout.Token);
            if (code == 0) return (0, state.MachineId, state.HubUrl!);
            if (timeout.IsCancellationRequested && !ct.IsCancellationRequested) env.Error.WriteLine($"Setup could not enroll with the hub at {options.Hub} within {env.EnrollTimeout.TotalSeconds:0} seconds. Check the address and that the hub is running. Nothing on this PC was changed apart from the node folder '{folder}'.");
            return (code, "", "");
        }
    }
}
