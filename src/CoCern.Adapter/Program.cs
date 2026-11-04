using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CoCern.Adapter;
using CoCern.Adapter.Node;
using CoCern.Adapter.Setup;
using CoCern.Contracts;

[assembly: System.Reflection.AssemblyMetadata(NativeRegistration.ProtocolMetadata, NativeRegistration.ProtocolVersion)]

try
{
    if (args is ["--dev-setup", var deploymentRoot, var sourceDirectory, var developmentTokenFile])
    {
        deploymentRoot = Path.GetFullPath(deploymentRoot);
        var manifest = CoCern.Adapter.Development.AdapterDeployment.Publish(sourceDirectory, deploymentRoot);
        var profile = SetupEnvironment.ConnectFolders().Profile;
        var launcher = CoCern.Adapter.Development.AdapterDeployment.AdapterPath(deploymentRoot, manifest);
        var changed = CoCern.Adapter.Development.DevelopmentSetup.ConfigureCodex(Path.Combine(profile, ".codex", "config.toml"), SetupEnvironment.Host().Dotnet,
            launcher, deploymentRoot, Path.GetFullPath(developmentTokenFile), replaceLauncher: true);
        var claudeChanged = CoCern.Adapter.Development.DevelopmentSetup.ConfigureClaudeCode(Path.Combine(profile, ".claude.json"), SetupEnvironment.Host().Dotnet,
            launcher, deploymentRoot, Path.GetFullPath(developmentTokenFile), replaceLauncher: true);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { manifest.Version, manifest.Generation, ConfigurationChanged = changed, ClaudeConfigurationChanged = claudeChanged }));
        return 0;
    }
    if (args is ["--dev-launch", var developmentRoot])
        return await CoCern.Adapter.Development.DevelopmentLauncher.RunAsync(developmentRoot, SetupEnvironment.Host().Dotnet);
    if (args.Length > 0 && args[0] == "--dev-launch")
    {
        if (args.Length != 8 || !Path.IsPathFullyQualified(args[1]) || !NativeRegistration.ValidClientArguments(args[2..]))
            throw new AdapterConfigurationException(NativeRegistration.Refusal);
        return await CoCern.Adapter.Development.DevelopmentLauncher.RunNativeAsync(args[1], SetupEnvironment.Host().Dotnet, args[2..]);
    }
    if (args.Length > 0 && args[0] == "--native-hook")
    {
        const string usage = "Use --native-hook <codex|claude> --connection <absolute-private-folder> with optional --profile <absolute-profile> and --development-root <absolute-folder>.";
        if (args.Length is not (4 or 6 or 8) || args[1] is not ("codex" or "claude") || args[2] != "--connection" || !Path.IsPathFullyQualified(args[3]))
            throw new AdapterConfigurationException(usage);
        string? hookProfile = null; string? hookDevelopmentRoot = null;
        for (var i = 4; i < args.Length; i += 2)
        {
            if (!Path.IsPathFullyQualified(args[i + 1]) || args[i + 1].Length > 32768 || args[i + 1].Any(char.IsControl)) throw new AdapterConfigurationException(usage);
            if (args[i] == "--profile" && hookProfile is null) hookProfile = args[i + 1];
            else if (args[i] == "--development-root" && hookDevelopmentRoot is null) hookDevelopmentRoot = args[i + 1];
            else throw new AdapterConfigurationException(usage);
        }
        var connection = NativeJoinHook.ReadConnection(args[3]) ?? throw new AdapterConfigurationException("Native hooks need their configured private connection.");
        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, cancel) => { cancel.Cancel = true; stopping.Cancel(); };
        if (hookDevelopmentRoot is not null)
            return await NativeJoinHook.RunDevelopmentAsync(hookDevelopmentRoot, SetupEnvironment.Host().Dotnet, args[1], args[3], Console.In, Console.Out, Console.Error, stopping.Token, hookProfile);
        var hookRegistration = hookProfile is null ? null : NativeRegistration.FromClientArguments(["--native-client", args[1], "--connection", args[3], "--profile", hookProfile])
            ?? throw new AdapterConfigurationException(NativeRegistration.Refusal);
        using var nativeHttp = new HttpClient(RelayForwarder.CreateHandler(connection)) { Timeout = TimeSpan.FromSeconds(15) };
        await using var join = new NativeJoin(args[3], connection, new RelayForwarder(nativeHttp, connection));
        using var output = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true };
        using var error = new StreamWriter(Console.OpenStandardError(), new System.Text.UTF8Encoding(false)) { AutoFlush = true };
        return hookRegistration is null
            ? await NativeJoinHook.RunAsync(args[1], Console.In, output, error, join, stopping.Token)
            : await NativeJoinHook.RunRegisteredAsync(args[1], Console.In, output, error, join, hookRegistration, stopping.Token);
    }
    if (args.Length > 0 && args[0] == "--wake")
    {
        if (args.Length != 2) throw new AdapterConfigurationException("Use --wake with one absolute configuration path.");
        var configuration = WakeConfiguration.Load(args[1]);
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, cancel) => { cancel.Cancel = true; shutdown.Cancel(); };
        await WakeWatcher.RunAsync(configuration, shutdown.Token);
        return 0;
    }
    if (args.Length > 0 && args[0] == "--await-inbox")
    {
        // Exit codes tell the waiting session what happened: 0 new message, 2 timeout, 3 stopped.
        if (args.Length > 1 && args[1] == "--chat")
        {
            // A Claude chat's own waiter on this PC's node credentials (1 also when no relay session on this PC belongs to the chat).
            const string chatUsage = "Use --await-inbox --chat <claude-session-id> with optional --timeout-minutes 1-1440, --print-inbox and --ack <id,id,...>.";
            if (args.Length < 3) throw new AdapterConfigurationException(chatUsage);
            TimeSpan? limit = null;
            var printInbox = false;
            string[]? acknowledge = null;
            for (var i = 3; i < args.Length; i++)
            {
                if (args[i] == "--print-inbox" && !printInbox) printInbox = true;
                else if (args[i] == "--timeout-minutes" && limit is null && i + 1 < args.Length && int.TryParse(args[i + 1], out var minutes) && minutes is > 0 and <= 1440) { limit = TimeSpan.FromMinutes(minutes); i++; }
                else if (args[i] == "--ack" && acknowledge is null && i + 1 < args.Length &&
                         args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } ids && ids.Length <= RelayLimits.MaxAcknowledgedMessages && ids.All(id => id.Length <= RelayLimits.MaxIdentifierLength && id.All(char.IsAsciiLetterOrDigit)))
                { acknowledge = ids; i++; }
                else throw new AdapterConfigurationException(chatUsage);
            }
            var folder = NodeState.ConfiguredFolder(Environment.GetEnvironmentVariable);
            using var stopping = new CancellationTokenSource();
            Console.CancelKeyPress += (_, cancel) => { cancel.Cancel = true; stopping.Cancel(); };
            var waited = await ClaudeWaiter.RunAsync(folder, args[2], Console.Out.WriteLine, limit, stopping.Token, printInbox, acknowledge);
            return waited switch { "queued" => 0, "timeout" => 2, _ => 3 };
        }
        TimeSpan? timeout = args.Length switch
        {
            2 => null,
            4 when args[2] == "--timeout-minutes" && int.TryParse(args[3], out var minutes) && minutes is > 0 and <= 1440 => TimeSpan.FromMinutes(minutes),
            _ => throw new AdapterConfigurationException("Use --await-inbox with one absolute configuration path and optional --timeout-minutes 1-1440.")
        };
        var configuration = WakeConfiguration.LoadForAwait(args[1]);
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, cancel) => { cancel.Cancel = true; shutdown.Cancel(); };
        var status = await WakeWatcher.AwaitInboxAsync(configuration, Console.Out.WriteLine, timeout, shutdown.Token);
        return status switch { "queued" => 0, "timeout" => 2, _ => 3 };
    }
    if (args.Length > 0 && args[0] == "--prompt-hook")
    {
        // A UserPromptSubmit hook: one line on standard output, or nothing; it always exits 0 so it can never block a message.
        var folder = args.Length == 3 && args[1] == "--data-dir" && !string.IsNullOrWhiteSpace(args[2]) ? args[2]
            : NodeState.ConfiguredFolder(Environment.GetEnvironmentVariable);
        return await PromptHook.RunAsync(Console.In, Console.Out, folder);
    }
    if (args.Length > 0 && args[0] == "--stop-hook")
    {
        // A Claude Code Stop hook run with asyncRewake: exit 2 with the waiting messages on standard error wakes the idle chat; anything else exits 0.
        var folder = args.Length == 3 && args[1] == "--data-dir" && !string.IsNullOrWhiteSpace(args[2]) ? args[2]
            : NodeState.ConfiguredFolder(Environment.GetEnvironmentVariable);
        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, cancel) => { cancel.Cancel = true; stopping.Cancel(); };
        // Messages carry any text; the console's code page would turn non-ASCII into '?' before the chat reads it.
        using var error = new StreamWriter(Console.OpenStandardError(), new System.Text.UTF8Encoding(false)) { AutoFlush = true };
        return await StopHook.RunAsync(Console.In, error, folder, stopping.Token);
    }
    if (args.Length > 0 && args[0] == "--connect")
    {
        var (dotnet, dll) = SetupEnvironment.Host();
        var (profile, appData) = SetupEnvironment.ConnectFolders();
        return ConnectCommand.Run(args[1..], profile, appData,
            dotnet, dll, Console.Out, Console.Error);
    }
    if (args.Length > 0 && args[0] == "--setup")
    {
        // Exit codes: 0 done, 1 configuration problem or enrollment failure (the node's code, e.g. 4 for a changed hub certificate), 5 a client settings file could not be parsed.
        var setup = SetupOptions.Parse(args[1..], Environment.GetEnvironmentVariable(SetupOptions.JoinCodeVariable));
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, cancel) => { cancel.Cancel = true; shutdown.Cancel(); };
        return await SetupCommand.RunAsync(setup, SetupEnvironment.Create(), shutdown.Token);
    }
    if (args.Length > 0 && args[0] == "--node")
    {
        // Exit codes: 0 stopped, 1 configuration problem or revoked PC, 4 the hub presented a certificate other than the pinned one.
        const string usage = "Use --node [--data-dir <path>] [--hub <https-url>] [--join-code <code>].";
        string? dataDir = null;
        var joinCode = Environment.GetEnvironmentVariable(SetupOptions.JoinCodeVariable) is { Length: > 0 } fromEnvironment ? fromEnvironment.Trim() : null;
        var typedJoinCode = false;
        Uri? hub = null;
        for (var i = 1; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1])) throw new AdapterConfigurationException(usage);
            switch (args[i])
            {
                case "--data-dir" when dataDir is null: dataDir = args[i + 1]; break;
                case "--join-code" when !typedJoinCode: (joinCode, typedJoinCode) = (args[i + 1].Trim(), true); break;
                case "--hub" when hub is null:
                    if (!AdapterOptions.TryParseOrigin(args[i + 1], out var typed)) throw new AdapterConfigurationException("--hub must be a root https:// address, or http:// to this PC's loopback, without credentials, query, or fragment.");
                    hub = typed;
                    break;
                default: throw new AdapterConfigurationException(usage);
            }
        }
        using var state = NodeState.Open(dataDir);
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, cancel) => { cancel.Cancel = true; shutdown.Cancel(); };
        return await NodeRunner.RunAsync(state, hub, new NodeTransport { JoinCode = joinCode }, Console.Error, shutdown.Token);
    }
    NativeRegistration? registration = null;
    if (args.Any(NativeRegistration.HasPrivateOptionName))
    {
        registration = NativeRegistration.FromClientArguments(args) ?? throw new AdapterConfigurationException(NativeRegistration.Refusal);
        if (await registration.AcceptAsync(CancellationToken.None) is null) throw new AdapterConfigurationException(NativeRegistration.Refusal);
    }
    var options = registration?.Connection ?? AdapterOptions.FromEnvironment();
    // On node credentials the hub certificate is pinned; with explicit settings this is the plain handler, as before.
    using var http = new HttpClient(RelayForwarder.CreateHandler(options)) { Timeout = TimeSpan.FromSeconds(60) };
    await using var hints = new SessionHints(options);
    await using var localWake = registration is null ? LocalChatWake.TryCreate(options, Environment.GetEnvironmentVariable("COCERN_TOKEN_FILE")) : null;
    var forwarder = new RelayForwarder(http, options);
    await using var native = (registration?.Folder ?? options.NodeFolder ?? localWake?.Connection.Folder) is { } privateFolder ? new NativeJoin(privateFolder, options, forwarder) : null;
    var builder = Host.CreateApplicationBuilder(registration is null ? args : []);
    // Library request/exception logging can contain handles. Emit only our fixed stderr hints.
    builder.Logging.ClearProviders();
    builder.Services.AddMcpServer(server =>
    {
        server.ServerInstructions = registration is null ? AdapterInstructions.Server : AdapterInstructions.Native;
        if (registration is not null) server.ServerInfo = new()
        {
            Name = NativeRegistration.PrivateServerName,
            Version = typeof(NativeRegistration).Assembly.ManifestModule.ModuleVersionId.ToString("D")
        };
    }).WithStdioServerTransport().WithTools(registration is null
        ? CoordinationTools.Create(forwarder, options.Admin, hints, SessionHandoff.For(options), options.ChatOnly, options.ExportedChat, localWake: localWake, diagnostics: options.Diagnostics)
        : CoordinationTools.CreateNative(forwarder, native!, registration.Provider, options.Admin, options.Diagnostics));
    using var host = builder.Build();
    var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
    _ = localWake?.RunAsync(lifetime);
    _ = native?.ResumeAsync(lifetime);
    await host.RunAsync();
    return 0;
}
catch (AdapterConfigurationException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
catch (OperationCanceledException)
{
    return 0;
}
catch (Exception)
{
    Console.Error.WriteLine("CoCern adapter stopped unexpectedly. Check the configured hub and MCP client.");
    return 1;
}
