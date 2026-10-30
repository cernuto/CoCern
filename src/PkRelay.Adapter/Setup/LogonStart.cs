using System.Runtime.Versioning;

namespace PkRelay.Adapter.Setup;

/// <summary>The per-user Run key (or a test double): the programs Windows starts when this user signs in.</summary>
internal interface IRunKey
{
    string? Get(string name);
    void Set(string name, string value);
}

/// <summary><c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>: current user only, no administrator rights.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsRunKey : IRunKey
{
    private const string Path = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? Get(string name)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Path);
        return key?.GetValue(name) as string;
    }

    public void Set(string name, string value)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Path, writable: true);
        key.SetValue(name, value, Microsoft.Win32.RegistryValueKind.String);
    }
}

/// <summary>Starts the PkRelay node when the user signs in, by writing the one <c>PkRelayNode</c> Run value.</summary>
internal static class LogonStart
{
    public const string ValueName = "PkRelayNode";

    /// <summary>The command line the Run value holds: every path quoted, with the data folder when one was chosen.</summary>
    public static string Command(string dotnetPath, string adapterDll, string? dataDir = null) =>
        $"{Quote(dotnetPath)} {Quote(adapterDll)} --node" + (string.IsNullOrWhiteSpace(dataDir) ? "" : $" --data-dir {Quote(dataDir)}");

    internal static string Quote(string path) => path.Contains('"') ? throw new AdapterConfigurationException($"'{path}' contains a quote character and cannot be used in a start command.") : $"\"{path}\"";

    /// <summary>Idempotent: an identical value is not rewritten. Only the <c>PkRelayNode</c> value is ever touched.</summary>
    public static void Register(string dotnetPath, string adapterDll, string? dataDir = null, IRunKey? key = null)
    {
        key ??= OperatingSystem.IsWindows() ? new WindowsRunKey() : throw new AdapterConfigurationException("Starting the node at sign-in is supported on Windows only.");
        var command = Command(dotnetPath, adapterDll, dataDir);
        if (key.Get(ValueName) != command) key.Set(ValueName, command);
    }
}
