using System.ComponentModel;
using System.Diagnostics;

namespace CoCern.Adapter.Node;

/// <summary>Finds the Codex desktop app's command-line executable, which <c>codex queue</c> wakes run.</summary>
public static class CodexLocator
{
    private static readonly Lock DesktopGate = new();
    private static long nextDesktopCheck;
    private static string? desktopExecutable;
    private static int? desktopProcessId;

    /// <summary>Prefer the CLI running from the desktop's installed Windows package. Older installations use the per-user CLI cache.</summary>
    public static string? Find()
    {
        if (OperatingSystem.IsWindows())
        {
            lock (DesktopGate)
            {
                var now = Environment.TickCount64;
                if (now >= nextDesktopCheck)
                {
                    desktopExecutable = RunningDesktopExecutable();
                    desktopProcessId = desktopExecutable is null || !SupportsTargetedWake(desktopExecutable) ? null
                        : SessionImages("ChatGPT").FirstOrDefault(process => string.Equals(process.Image, DesktopImage(desktopExecutable), StringComparison.OrdinalIgnoreCase)).Id;
                    nextDesktopCheck = now + 30_000;
                }
                if (desktopExecutable is not null && File.Exists(desktopExecutable)) return desktopExecutable;
            }
        }
        return Find(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    }

    internal static int? DesktopOwner(string executable)
    {
        if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(executable) || !SupportsTargetedWake(executable)) return null;
        _ = Find();
        lock (DesktopGate)
        {
            return desktopProcessId > 0 && string.Equals(executable, desktopExecutable, StringComparison.OrdinalIgnoreCase) ? desktopProcessId : null;
        }
    }

    internal static string DesktopImage(string executable) => Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(executable))!, "ChatGPT.exe");

    // These desktop packages have been verified to handle the targeted wake URI.
    internal static bool SupportsTargetedWake(string executable)
    {
        var package = new FileInfo(executable).Directory?.Parent?.Parent?.Name;
        return string.Equals(package, "OpenAI.Codex_26.1111.2228.0_x64__2p2nqsd0c76g0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(package, "OpenAI.Codex_26.1111.9664.0_x64__2p2nqsd0c76g0", StringComparison.OrdinalIgnoreCase);
    }

    private static List<(string Image, int Id)> SessionImages(string name)
    {
        using var current = Process.GetCurrentProcess();
        var images = new List<(string Image, int Id)>();
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId == current.SessionId && process.MainModule?.FileName is { } image)
                        images.Add((image, process.Id));
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // A process can exit or be inaccessible while taking this snapshot.
                }
            }
        }
        return images;
    }

    private static string? RunningDesktopExecutable()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (programFiles.Length == 0) return null;
        var windowsApps = Path.Combine(programFiles, "WindowsApps");
        var paths = SessionImages("codex").Select(process => process.Image).Where(path => IsDesktopExecutable(windowsApps, path))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        return paths.Length == 1 ? paths[0] : null;
    }

    internal static bool IsDesktopExecutable(string windowsApps, string path)
    {
        // A same-named executable on PATH or in a user-writable folder cannot supply a wake binary.
        var parts = Path.GetRelativePath(windowsApps, path).Split(Path.DirectorySeparatorChar);
        return parts.Length == 4 && parts[0].StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase)
            && parts[0].EndsWith("__2p2nqsd0c76g0", StringComparison.OrdinalIgnoreCase)
            && parts[1].Equals("app", StringComparison.OrdinalIgnoreCase) && parts[2].Equals("resources", StringComparison.OrdinalIgnoreCase)
            && parts[3].Equals("codex.exe", StringComparison.OrdinalIgnoreCase);
    }

    internal static string? Find(string localAppData)
    {
        if (string.IsNullOrEmpty(localAppData)) return null;
        var bin = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
        try
        {
            if (!Directory.Exists(bin)) return null;
            return Directory.EnumerateDirectories(bin).Select(folder => new FileInfo(Path.Combine(folder, "codex.exe"))).Where(file => file.Exists)
                .OrderByDescending(file => file.LastWriteTimeUtc).Select(file => file.FullName).FirstOrDefault();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }
}
