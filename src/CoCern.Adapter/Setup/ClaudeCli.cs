using System.ComponentModel;
using System.Diagnostics;

namespace CoCern.Adapter.Setup;

/// <summary>What one <c>claude</c> run came back with. A run that never finished (not startable, or over the time limit) has no result at all.</summary>
internal sealed record CliResult(int ExitCode, string Output, string Error);

/// <summary>
/// How registering through the command line went. <see cref="Unavailable"/> (it failed) and <see cref="Unsafe"/> (it was not run, see
/// <see cref="ClaudeCli.SafeForBatch"/>) both mean setup edits .claude.json itself instead.
/// </summary>
internal enum ClaudeRegistration { Existing, Added, Unavailable, Unsafe }

/// <summary>
/// Claude Code's own command line (<c>claude mcp ...</c>) on this PC. Setup prefers it to editing .claude.json, because a running Claude Code
/// rewrites that file itself and would drop an entry written behind its back. The run itself is injectable so tests never touch a real installation.
/// </summary>
internal sealed class ClaudeCli(string executable, Func<string, IReadOnlyList<string>, CliResult?> run)
{
    public const string Name = "cocern";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly string[] Launchers = ["claude.exe", "claude.cmd", "claude.bat", "claude"];

    public string Executable => executable;

    /// <summary>
    /// The real thing, or null when no <c>claude</c> launcher is on PATH or in its usual Windows locations. It runs in <paramref name="profile"/>, the
    /// user's own folder, so a project's <c>.mcp.json</c> or local entry in whatever folder setup was started from is never taken for the user's entry.
    /// </summary>
    public static ClaudeCli? Find(string profile) => Locate(Environment.GetEnvironmentVariable) is { } found ? new(found, (exe, arguments) => Run(exe, arguments, Timeout, profile)) : null;

    /// <summary>
    /// The launcher's path: each absolute PATH folder first (what <c>where.exe claude</c> would report, but never a relative entry, which would
    /// name a folder under wherever setup was started), then npm's global folder, the installer's folder and the native install under the profile.
    /// </summary>
    public static string? Locate(Func<string, string?> variable)
    {
        foreach (var entry in (variable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var folder = entry.Trim('"');
            if (!Path.IsPathFullyQualified(folder)) continue;
            foreach (var launcher in Launchers)
                if (Exists(Path.Combine(folder, launcher)) is { } found) return found;
        }
        var candidates = new[]
        {
            (variable("APPDATA"), Path.Combine("npm", "claude.cmd")),
            (variable("LOCALAPPDATA"), Path.Combine("Programs", "claude", "claude.exe")),
            (variable("USERPROFILE"), Path.Combine(".claude", "local", "claude.exe"))
        };
        foreach (var (root, tail) in candidates)
            if (!string.IsNullOrWhiteSpace(root) && Path.IsPathFullyQualified(root) && Exists(Path.Combine(root, tail)) is { } found) return found;
        return null;
    }

    private static string? Exists(string path)
    {
        try { return File.Exists(path) ? Path.GetFullPath(path) : null; }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

    /// <summary><c>claude mcp get</c> takes no scope, so its answer is read for the scope it reports (see <see cref="IsUserEntry"/>).</summary>
    public static IReadOnlyList<string> GetArguments => ["mcp", "get", Name];

    /// <summary>The same server the file plan writes: stdio transport, the adapter exe alone or the dotnet host with the adapter as its one argument (see <see cref="SetupEnvironment.AdapterLaunch"/>), and the same variables.</summary>
    public static IReadOnlyList<string> AddArguments(string dotnetPath, string adapterDll, IReadOnlyDictionary<string, string>? environment)
    {
        var arguments = new List<string> { "mcp", "add", "--scope", "user", "--transport", "stdio", Name };
        foreach (var pair in environment ?? new Dictionary<string, string>()) { arguments.Add("--env"); arguments.Add($"{pair.Key}={pair.Value}"); }
        arguments.Add("--");
        var (command, launchArguments) = SetupEnvironment.AdapterLaunch(dotnetPath, adapterDll);
        arguments.Add(command);
        arguments.AddRange(launchArguments);
        return arguments;
    }

    /// <summary>
    /// Leaves a user entry that <c>claude mcp get</c> reports alone (as the file plan does); otherwise adds one for the user. Any failure, including
    /// a run that never finishes, is "unavailable"; arguments a batch-file launcher would misread are "unsafe" and the add is never run.
    /// </summary>
    public ClaudeRegistration Register(string dotnetPath, string adapterDll, IReadOnlyDictionary<string, string>? environment)
    {
        if (IsUserEntry(run(executable, GetArguments))) return ClaudeRegistration.Existing;
        var add = AddArguments(dotnetPath, adapterDll, environment);
        if (IsBatchFile(executable) && !SafeForBatch(add)) return ClaudeRegistration.Unsafe;
        return run(executable, add) is { ExitCode: 0 } ? ClaudeRegistration.Added : ClaudeRegistration.Unavailable;
    }

    /// <summary>
    /// Whether <c>claude mcp get</c> found the entry in the user's own settings. It reports the scope it found (<c>Scope: User config ...</c>, and the
    /// <c>-s user</c> its remove hint names); a project (<c>.mcp.json</c>) or local entry is not the user's, so CoCern is still added for the user.
    /// An answer that names no scope counts as no user entry: the add then fails if there is one, and the file plan, which reads the user's entries
    /// directly, leaves it alone.
    /// </summary>
    internal static bool IsUserEntry(CliResult? result)
    {
        if (result is not { ExitCode: 0 } found) return false;
        var lines = found.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var scope = lines.FirstOrDefault(line => line.StartsWith("Scope:", StringComparison.OrdinalIgnoreCase));
        if (scope is not null) return scope["Scope:".Length..].TrimStart().StartsWith("user", StringComparison.OrdinalIgnoreCase);
        return lines.Any(line => line.Contains("-s user", StringComparison.OrdinalIgnoreCase) || line.Contains("--scope user", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>An npm install's <c>claude.cmd</c> (or a <c>.bat</c>): Windows runs it through cmd.exe, which parses its command line once more.</summary>
    internal static bool IsBatchFile(string path) =>
        Path.GetExtension(path) is var extension && (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether cmd.exe passes these arguments to a batch file as given. Process quoting only guards spaces: cmd.exe still acts on <c>&amp; | &lt; &gt; ^</c>
    /// outside quotes, expands <c>%</c> and <c>!</c> anywhere, ends a quoted argument at <c>"</c> and a command at a line break, so a path or value
    /// holding any of them could break the command or run another. Such arguments are refused, never escaped.
    /// </summary>
    internal static bool SafeForBatch(IEnumerable<string> arguments) =>
        arguments.All(argument => !argument.Any(c => c is '&' or '|' or '<' or '>' or '^' or '%' or '!' or '"' || char.IsControl(c)));

    /// <summary>
    /// Runs the launcher with no console of its own, in <paramref name="workingDirectory"/> when given; null when it cannot start or is still running
    /// after <paramref name="timeout"/> (it is then killed).
    /// </summary>
    public static CliResult? Run(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, string? workingDirectory = null)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        if (workingDirectory is not null) info.WorkingDirectory = workingDirectory;
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(info);
            if (process is null) return null;
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException) { }
                return null;
            }
            return new(process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return null; }
    }
}
