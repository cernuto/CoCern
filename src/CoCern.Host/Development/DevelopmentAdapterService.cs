using System.Diagnostics;
using CoCern.Adapter.Development;
using CoCern.Host.Dashboard;

namespace CoCern.Host.Development;

/// <summary>The opt-in Visual Studio profile publishes an adapter snapshot after the hub is ready.</summary>
internal sealed class DevelopmentAdapterService(IConfiguration configuration, IHostEnvironment environment, IHostApplicationLifetime lifetime, ILogger<DevelopmentAdapterService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!environment.IsDevelopment() || !Debugger.IsAttached || !configuration.GetValue<bool>("Relay:DevelopmentAdapter:Enabled")) return;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(() => ready.TrySetResult());
        try
        {
            await ready.Task.WaitAsync(stoppingToken);
            await Task.Run(() => Deploy(), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Settings can contain credentials. The exception's text is deliberately not logged.
            logger.LogError("Development adapter deployment failed ({FailureType}). The previous connection remains available; check the source path and the existing Codex and Claude Code CoCern entries.", error.GetType().Name);
        }
    }

    private void Deploy()
    {
        string Full(string key, string fallback) => Path.GetFullPath(configuration[key] ?? fallback, environment.ContentRootPath);
        var builtSource = typeof(DevelopmentAdapterService).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>().Single(attribute => attribute.Key == "CoCern.DevelopmentAdapterSource").Value
            ?? throw new IOException("The adapter build output path is unavailable.");
        var source = Full("Relay:DevelopmentAdapter:SourcePath", builtSource);
        var root = Full("Relay:DevelopmentAdapter:Root", "../../artifacts/development-adapter");
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
    }
}
