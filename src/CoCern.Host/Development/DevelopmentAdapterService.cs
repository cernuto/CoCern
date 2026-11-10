using System.Diagnostics;
using CoCern.Adapter.Development;
using CoCern.Host.Dashboard;

namespace CoCern.Host.Development;

/// <summary>
/// The opt-in Visual Studio profile publishes an adapter snapshot after the hub is ready, then (unless <c>Relay:DevelopmentAdapter:PublishNodeAdapter</c>
/// is false) keeps a self-contained node adapter for that source in <c>artifacts/node-adapter</c>; see <see cref="NodeAdapterBuilds"/>.
/// </summary>
internal sealed class DevelopmentAdapterService(IConfiguration configuration, IHostEnvironment environment, IHostApplicationLifetime lifetime, ILogger<DevelopmentAdapterService> logger) : BackgroundService
{
    /// <summary>How long after the hub starts old versions are pruned: well after startup, once the deployment and chats' reconnects are done.</summary>
    private static readonly TimeSpan PruneDelay = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!environment.IsDevelopment() || !Debugger.IsAttached || !configuration.GetValue<bool>("Relay:DevelopmentAdapter:Enabled")) return;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(() => ready.TrySetResult());
        var prune = Task.CompletedTask;
        try
        {
            await ready.Task.WaitAsync(stoppingToken);
            var deployment = Task.Run(Deploy, stoppingToken);
            if (configuration.GetValue("Relay:DevelopmentAdapter:PruneVersions", true)) prune = PruneLaterAsync(deployment, stoppingToken);
            var version = await deployment;
            // In the background of this service only: hub startup never waits for it, and stopping the hub stops the publish.
            if (configuration.GetValue("Relay:DevelopmentAdapter:PublishNodeAdapter", true) && DashboardConnect.CheckoutRoot(AppContext.BaseDirectory) is { } checkout)
                await new NodeAdapterPublisher(logger, configuration.GetValue("Relay:DevelopmentAdapter:PruneVersions", true)).PublishAsync(checkout, version, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Settings can contain credentials. The exception's text is deliberately not logged.
            logger.LogError("Development adapter deployment failed ({FailureType}). The previous connection remains available; check the source path and the existing Codex and Claude Code CoCern entries.", error.GetType().Name);
        }
        await prune;
    }

    /// <summary>
    /// Prunes old development and node adapter versions once, <see cref="PruneDelay"/> after the hub starts and after this start's deployment ends
    /// (successfully or not); see <see cref="DevelopmentAdapterVersions"/> and <see cref="NodeAdapterPublisher.Prune"/>. Never throws.
    /// </summary>
    private async Task PruneLaterAsync(Task deployment, CancellationToken ct)
    {
        try
        {
            await Task.Delay(PruneDelay, ct);
            await Task.WhenAny(deployment);
            var keep = configuration.GetValue("Relay:DevelopmentAdapter:KeepVersions", 5);
            await Task.Run(() => DevelopmentAdapterVersions.Prune(DeploymentRoot(), keep, ReferenceFiles(), ReferenceFolders(),
                [configuration["Relay:Connect:AdapterPath"], configuration["Relay:Connect:DistributionAdapterPath"]], logger), ct);
            if (DashboardConnect.CheckoutRoot(AppContext.BaseDirectory) is { } checkout) await Task.Run(() => NodeAdapterPublisher.Prune(checkout, logger), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            logger.LogWarning("Adapter version pruning failed ({FailureType}); old versions are kept.", error.GetType().Name);
        }
    }

    private string Full(string key, string fallback) => Path.GetFullPath(configuration[key] ?? fallback, environment.ContentRootPath);

    private string DeploymentRoot() => Full("Relay:DevelopmentAdapter:Root", "../../artifacts/development-adapter");

    private string CodexConfig() => Full("Relay:DevelopmentAdapter:CodexConfigPath", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml"));

    private string ClaudeConfig() => Full("Relay:DevelopmentAdapter:ClaudeConfigPath", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json"));

    /// <summary>Client files that can name a launcher or hook version: Codex and Claude Code settings and hooks, and the Claude desktop app's settings.</summary>
    private List<string> ReferenceFiles()
    {
        var codexFolder = Path.GetDirectoryName(CodexConfig())!;
        var claudeFolder = Path.Combine(Path.GetDirectoryName(ClaudeConfig())!, ".claude");
        List<string> files =
        [
            CodexConfig(), Path.Combine(codexFolder, "hooks.json"),
            ClaudeConfig(), Path.Combine(claudeFolder, "settings.json"), Path.Combine(claudeFolder, "settings.local.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json")
        ];
        if (Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } codexHome)
            files.AddRange([Path.Combine(codexHome, "config.toml"), Path.Combine(codexHome, "hooks.json")]);
        // Packaged Claude and Codex apps see their own AppData.
        files.AddRange(PackagedAppFolders().Select(package => Path.Combine(package, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json")));
        return files;
    }

    /// <summary>Folders of staged native candidates and wake state, whose hook command lines can name a version.</summary>
    private static List<string> ReferenceFolders()
    {
        var wake = Path.Combine("CoCern", "local-wake");
        return [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), wake), .. PackagedAppFolders().Select(package => Path.Combine(package, "LocalCache", "Local", wake))];
    }

    private static IEnumerable<string> PackagedAppFolders()
    {
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        if (!Directory.Exists(packages)) return [];
        string[] names = ["Claude", "Anthropic", "Codex", "OpenAI"];
        return Directory.EnumerateDirectories(packages).Where(folder => names.Any(name => Path.GetFileName(folder).Contains(name, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>Deploys the development adapter and returns its version (the content hash of the build output it copied).</summary>
    private string Deploy()
    {
        var builtSource = typeof(DevelopmentAdapterService).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>().Single(attribute => attribute.Key == "CoCern.DevelopmentAdapterSource").Value
            ?? throw new IOException("The adapter build output path is unavailable.");
        var source = Full("Relay:DevelopmentAdapter:SourcePath", builtSource);
        var root = DeploymentRoot();
        var config = configuration["Relay:DevelopmentAdapter:CodexConfigPath"]
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
        // Use the same token file the profile's administrator client reads; no secret is copied into a deployment.
        var tokenFile = configuration.GetSection("Relay:Clients").GetChildren()
            .FirstOrDefault(client => client.GetValue<bool>("IsAdministrator") && client["TokenFile"] is { Length: > 0 })?["TokenFile"]
            ?? throw new IOException("No local administrator token file is configured.");
        var manifest = AdapterDeployment.Publish(source, root);
        var changed = DevelopmentSetup.ConfigureCodex(Path.GetFullPath(config, environment.ContentRootPath), DashboardConnect.DotnetHost(), AdapterDeployment.AdapterPath(root, manifest), root,
            Path.GetFullPath(tokenFile, environment.ContentRootPath));
        configuration["Relay:Connect:AdapterPath"] = AdapterDeployment.AdapterPath(root, manifest);
        // Claude Code chats follow the same launcher once their user-scope entry points at it; a PC without one is left alone.
        var claudeConfig = configuration["Relay:DevelopmentAdapter:ClaudeConfigPath"]
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
        var claudeChanged = DevelopmentSetup.ConfigureClaudeCode(Path.GetFullPath(claudeConfig, environment.ContentRootPath), DashboardConnect.DotnetHost(), AdapterDeployment.AdapterPath(root, manifest), root,
            Path.GetFullPath(tokenFile, environment.ContentRootPath));
        logger.LogInformation("Development adapter deployed: {Version}; requested fresh MCP generation {Generation}. Codex launch entry changed: {Changed}; Claude Code launch entry changed: {ClaudeChanged}.",
            manifest.Version[..12], manifest.Generation, changed, claudeChanged);
        return manifest.Version;
    }
}
