using CoCern.Adapter.Setup;
using System.Text.Json.Nodes;

namespace CoCern.Adapter.Development;

public static class DevelopmentSetup
{
    public static bool ConfigureCodex(string configurationPath, string dotnet, string launcherDll, string deploymentRoot, string tokenFile, bool replaceLauncher = false, string? nativeCacheRoot = null)
    {
        try
        {
            var changed = ClientSettings.PlanDevelopmentCodex(configurationPath, dotnet, launcherDll, deploymentRoot, tokenFile, replaceLauncher).Apply(TimeProvider.System) != ClientChange.Unchanged;
            ClientSettings.StageDevelopmentNativeCandidate(configurationPath, "codex", dotnet, launcherDll, deploymentRoot, nativeCacheRoot);
            return changed;
        }
        catch (ClientSettingsException error) { throw new IOException("The existing Codex CoCern entry could not be configured for development.", error); }
    }

    /// <summary>
    /// Points the Claude Code user-scope CoCern entry at the development launcher. A missing settings file or CoCern entry is left alone (false);
    /// an entry for another hub or an unreadable file fails, as for Codex.
    /// </summary>
    public static bool ConfigureClaudeCode(string configurationPath, string dotnet, string launcherDll, string deploymentRoot, string tokenFile, bool replaceLauncher = false, string? nativeCacheRoot = null)
    {
        try
        {
            var client = ClientSettings.PlanDevelopmentClaudeCode(configurationPath, dotnet, launcherDll, deploymentRoot, tokenFile, replaceLauncher);
            if (!File.Exists(configurationPath) || JsonNode.Parse(File.ReadAllText(configurationPath))?["mcpServers"]?["cocern"] is not JsonObject) return false;
            var hookPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configurationPath))!, ".claude", "settings.json");
            var hook = ClientSettings.PlanStopHook(hookPath, SetupEnvironment.AdapterCommandLine(dotnet, launcherDll) + " --stop-hook");
            // Plan both files first: malformed hook settings must not leave the MCP entry half-updated.
            var changed = client.Apply(TimeProvider.System) != ClientChange.Unchanged;
            var hookChanged = hook.Apply(TimeProvider.System) != ClientChange.Unchanged;
            ClientSettings.StageDevelopmentNativeCandidate(configurationPath, "claude", dotnet, launcherDll, deploymentRoot, nativeCacheRoot);
            return hookChanged || changed;
        }
        catch (ClientSettingsException error) { throw new IOException("The existing Claude Code CoCern entry could not be configured for development.", error); }
    }
}
