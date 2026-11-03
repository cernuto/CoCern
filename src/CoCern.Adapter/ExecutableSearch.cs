namespace CoCern.Adapter;

/// <summary>
/// Finds a program on PATH and returns its full path, for a caller that then starts exactly that file. Starting a bare name lets Windows look
/// in the current folder first, so a git.exe planted in the repository a chat works in would run. Here only absolute PATH folders are searched:
/// blank and relative entries, '.' among them, are skipped, and the current folder is never tried.
/// </summary>
internal static class ExecutableSearch
{
    /// <summary>The kinds of file a process can be started from on Windows, in the default PATHEXT order.</summary>
    private static readonly string[] WindowsKinds = [".COM", ".EXE", ".BAT", ".CMD"];

    /// <summary><paramref name="name"/> on this process's PATH, or null when no folder on it has one.</summary>
    public static string? Find(string name) =>
        Find(name, Environment.GetEnvironmentVariable("PATH"), Environment.GetEnvironmentVariable("PATHEXT"), OperatingSystem.IsWindows(), IsProgram);

    /// <summary>
    /// The first <c>folder/name</c> that <paramref name="isProgram"/> accepts, folder by folder in <paramref name="path"/> order. On Windows each
    /// folder is tried with each extension in <paramref name="pathExt"/> order (only <c>.com</c>, <c>.exe</c>, <c>.bat</c> and <c>.cmd</c>, the
    /// kinds a process starts from; the default order when it names none), as the shell would; elsewhere with the name alone.
    /// </summary>
    internal static string? Find(string name, string? path, string? pathExt, bool windows, Func<string, bool> isProgram)
    {
        var extensions = windows ? Extensions(pathExt) : [""];
        foreach (var entry in (path ?? "").Split(windows ? ';' : ':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Windows tolerates a quoted PATH entry (a folder with ';' in its name).
            var folder = windows ? entry.Trim('"') : entry;
            if (folder.Length == 0 || !Path.IsPathFullyQualified(folder)) continue;
            foreach (var extension in extensions)
            {
                string candidate;
                try { candidate = Path.Combine(folder, name + extension); }
                catch (ArgumentException) { break; }
                if (isProgram(candidate)) return candidate;
            }
        }
        return null;
    }

    /// <summary>The extensions to try, lower case (Windows file names match either way).</summary>
    private static string[] Extensions(string? pathExt)
    {
        var listed = (pathExt ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(extension => WindowsKinds.Contains(extension, StringComparer.OrdinalIgnoreCase)).Select(extension => extension.ToLowerInvariant()).Distinct().ToArray();
        return listed.Length > 0 ? listed : WindowsKinds.Select(extension => extension.ToLowerInvariant()).ToArray();
    }

    /// <summary>An existing file, which on Unix someone may also execute.</summary>
    private static bool IsProgram(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            return OperatingSystem.IsWindows() || (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }
}
