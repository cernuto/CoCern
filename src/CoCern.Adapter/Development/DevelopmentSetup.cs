using CoCern.Adapter.Setup;

namespace CoCern.Adapter.Development;

public static class DevelopmentSetup
{
    public static bool ConfigureCodex(string configurationPath, string dotnet, string launcherDll, string deploymentRoot, string tokenFile, bool replaceLauncher = false)
    {
        try { return ClientSettings.PlanDevelopmentCodex(configurationPath, dotnet, launcherDll, deploymentRoot, tokenFile, replaceLauncher).Apply(TimeProvider.System) != ClientChange.Unchanged; }
        catch (ClientSettingsException error) { throw new IOException("The existing Codex CoCern entry could not be configured for development.", error); }
    }
}
