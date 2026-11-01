namespace CoCern.Adapter.Node;

/// <summary>Finds the Codex desktop app's command-line executable, which <c>codex queue</c> wakes run.</summary>
public static class CodexLocator
{
    /// <summary>The newest <c>%LOCALAPPDATA%\OpenAI\Codex\bin\*\codex.exe</c>; null when none is installed, and Codex routes are then announced unwakeable.</summary>
    public static string? Find() => Find(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

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
