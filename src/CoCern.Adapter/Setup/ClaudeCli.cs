using System.ComponentModel;
using System.Diagnostics;

namespace CoCern.Adapter.Setup;

/// <summary>What one <c>claude</c> run came back with. A run that never finished (not startable, or over the time limit) has no result at all.</summary>
internal sealed record CliResult(int ExitCode, string Output, string Error);

/// <summary>How registering through the command line went; <see cref="Unavailable"/> means setup edits .claude.json itself instead.</summary>
internal enum ClaudeRegistration { Existing, Added, Unavailable }

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

    /// <summary>The real thing, or null when no <c>claude</c> launcher is on PATH or in its usual Windows locations.</summary>
    public static ClaudeCli? Find() => Locate(Environment.GetEnvironmentVariable) is { } found ? new(found, (exe, arguments) => Run(exe, arguments, Timeout)) : null;

    /// <summary>The launcher's path: each PATH folder first (what <c>where.exe claude</c> would report), then npm's global folder, the installer's folder and the native install under the profile.</summary>
    public static string? Locate(Func<string, string?> variable)
    {
        foreach (var folder in (variable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
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
            if (!string.IsNullOrWhiteSpace(root) && Exists(Path.Combine(root, tail)) is { } found) return found;
        return null;
    }

    private static string? Exists(string path)
    {
        try { return File.Exists(path) ? Path.GetFullPath(path) : null; }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

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

    /// <summary>Leaves an entry <c>claude mcp get</c> reports alone (as the file plan does); otherwise adds one. Any failure, including a run that never finishes, is "unavailable".</summary>
    public ClaudeRegistration Register(string dotnetPath, string adapterDll, IReadOnlyDictionary<string, string>? environment)
    {
        if (run(executable, GetArguments) is { ExitCode: 0 }) return ClaudeRegistration.Existing;
        return run(executable, AddArguments(dotnetPath, adapterDll, environment)) is { ExitCode: 0 } ? ClaudeRegistration.Added : ClaudeRegistration.Unavailable;
    }

    /// <summary>Runs the launcher with no console of its own; null when it cannot start or is still running after <paramref name="timeout"/> (it is then killed).</summary>
    public static CliResult? Run(string executable, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
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
