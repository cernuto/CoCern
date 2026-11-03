using CoCern.Adapter.Setup;

namespace CoCern.Adapter.Development;

public static class DevelopmentSetup
{
    public static bool ConfigureCodex(string configurationPath, string dotnet, string launcherDll, string deploymentRoot, string tokenFile, bool replaceLauncher = false)
    {
        try { return ClientSettings.PlanDevelopmentCodex(configurationPath, dotnet, launcherDll, deploymentRoot, tokenFile, replaceLauncher).Apply(TimeProvider.System) != ClientChange.Unchanged; }
        catch (ClientSettingsException error) { throw new IOException("The existing Codex CoCern entry could not be configured for development.", error); }
    }

    /// <summary>
    /// Points the Claude Code user-scope CoCern entry at the development launcher. A missing settings file or CoCern entry is left alone (false);
    /// an entry for another hub or an unreadable file fails, as for Codex.
    /// </summary>
    public static bool ConfigureClaudeCode(string configurationPath, string dotnet, string launcherDll, string deploymentRoot, string tokenFile, bool replaceLauncher = false)
    {
        try { return ClientSettings.PlanDevelopmentClaudeCode(configurationPath, dotnet, launcherDll, deploymentRoot, tokenFile, replaceLauncher).Apply(TimeProvider.System) != ClientChange.Unchanged; }
        catch (ClientSettingsException error) { throw new IOException("The existing Claude Code CoCern entry could not be configured for development.", error); }
    }
}
