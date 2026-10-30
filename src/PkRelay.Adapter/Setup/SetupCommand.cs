using System.ComponentModel;
using System.Diagnostics;
using PkRelay.Adapter.Node;

namespace PkRelay.Adapter.Setup;

/// <summary>What <c>--setup</c> was asked to do.</summary>
internal sealed record SetupOptions(Uri Hub, bool SkipClientConfig, string? DataDir)
{
    private const string Usage = "Use --setup --hub <https-url> [--skip-client-config] [--data-dir <path>].";

    /// <summary>Parses the arguments after <c>--setup</c>. <c>--hub</c> is required.</summary>
    public static SetupOptions Parse(IReadOnlyList<string> args)
    {
        Uri? hub = null;
        string? dataDir = null;
        var skip = false;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--skip-client-config" when !skip: skip = true; break;
                case "--hub" when hub is null && i + 1 < args.Count && !string.IsNullOrWhiteSpace(args[i + 1]):
                    if (!AdapterOptions.TryParseOrigin(args[++i], out var typed)) throw new AdapterConfigurationException("--hub must be a root https:// address, or http:// to this PC's loopback, without credentials, query, or fragment.");
                    hub = typed;
                    break;
                case "--data-dir" when dataDir is null && i + 1 < args.Count && !string.IsNullOrWhiteSpace(args[i + 1]): dataDir = args[++i]; break;
                default: throw new AdapterConfigurationException(Usage);
            }
        }
        return new(hub ?? throw new AdapterConfigurationException(Usage), skip, dataDir);
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

    /// <summary>The real thing: this user's profile, the dotnet host running setup, the published adapter next to it, the Run key.</summary>
    public static SetupEnvironment Create()
    {
        var host = Environment.ProcessPath;
        var dotnet = host is not null && string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase) ? host
            : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } hostPath && File.Exists(hostPath) ? hostPath
            : throw new AdapterConfigurationException("Run setup as 'dotnet PkRelay.Adapter.dll --setup --hub <https-url>' so the dotnet host to start the node with is known.");
        var dll = Path.Combine(AppContext.BaseDirectory, "PkRelay.Adapter.dll");
        if (!File.Exists(dll)) throw new AdapterConfigurationException($"'{dll}' was not found. Run setup from the copied published folder.");
        return new()
        {
            Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            DotnetPath = dotnet,
            AdapterDll = dll,
            RunKey = OperatingSystem.IsWindows() ? new WindowsRunKey() : throw new AdapterConfigurationException("Setup is supported on Windows only."),
            Transport = new NodeTransport(),
            StartNode = StartDetached
        };
    }

    /// <summary>Shell-launched with a hidden window, so the node outlives this console and shares none of its handles.</summary>
    private static void StartDetached(string dotnet, string dll, string? dataDir)
    {
        var arguments = $"{LogonStart.Quote(dll)} --node" + (dataDir is null ? "" : $" --data-dir {LogonStart.Quote(dataDir)}");
        using var _ = Process.Start(new ProcessStartInfo(dotnet, arguments) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true });
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
        var folder = Path.GetFullPath(string.IsNullOrWhiteSpace(options.DataDir) ? NodeState.DefaultFolder : options.DataDir);
        var plans = new List<ClientPlan>();
        try
        {
            if (!options.SkipClientConfig)
            {
                plans.Add(ClientSettings.PlanCodex(Path.Combine(env.Profile, ".codex", "config.toml"), env.DotnetPath, env.AdapterDll));
                plans.Add(ClientSettings.PlanClaudeCode(Path.Combine(env.Profile, ".claude.json"), env.DotnetPath, env.AdapterDll));
            }
        }
        catch (ClientSettingsException exception) { env.Error.WriteLine(exception.Message); return MalformedClientFileExitCode; }

        var enrolled = await EnrollAsync(options, env, folder, ct);
        if (enrolled.Code != 0) return enrolled.Code;

        try
        {
            foreach (var plan in plans)
                env.Out.WriteLine(plan.Apply(env.Clock) switch { ClientChange.Created => $"Created {plan.Path} with the PkRelay entry.", ClientChange.Updated => $"Added the PkRelay entry to {plan.Path} (backup kept next to it).", _ => $"{plan.Path} already has a PkRelay entry; left as it was." });
            LogonStart.Register(env.DotnetPath, env.AdapterDll, options.DataDir is null ? null : folder, env.RunKey);
            env.StartNode(env.DotnetPath, env.AdapterDll, options.DataDir is null ? null : folder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or System.Security.SecurityException or AdapterConfigurationException)
        {
            env.Error.WriteLine($"Setup enrolled this PC but could not finish: {exception.Message}");
            return 1;
        }
        env.Out.WriteLine($"PkRelay is set up. Machine ID: {enrolled.MachineId}");
        env.Out.WriteLine($"Hub: {enrolled.Hub}");
        env.Out.WriteLine("The node was started in the background and will start at every sign-in.");
        return 0;
    }

    private static async Task<(int Code, string MachineId, string Hub)> EnrollAsync(SetupOptions options, SetupEnvironment env, string folder, CancellationToken ct)
    {
        NodeState state;
        try { state = NodeState.Open(options.DataDir); }
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
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(env.EnrollTimeout);
            var code = await NodeRunner.RunAsync(state, options.Hub, env.Transport, env.Error, timeout.Token);
            if (code == 0) return (0, state.MachineId, state.HubUrl!);
            if (timeout.IsCancellationRequested && !ct.IsCancellationRequested) env.Error.WriteLine($"Setup could not enroll with the hub at {options.Hub} within {env.EnrollTimeout.TotalSeconds:0} seconds. Check the address and that the hub is running. Nothing on this PC was changed apart from the node folder '{folder}'.");
            return (code, "", "");
        }
    }
}
