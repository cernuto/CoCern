using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CoCern.Adapter.Node;
using CoCern.Host.Dashboard;

namespace CoCern.Host.Development;

/// <summary>
/// Keeps a fresh self-contained adapter for node updates in <c>artifacts/node-adapter</c> of the hub's checkout, so enrolled nodes (which run
/// self-contained and have no known .NET host) are offered a build they can take. After the development adapter is deployed, it runs the same
/// <c>dotnet publish</c> as <c>scripts/Build-CoCern.ps1 -Publish</c> (Release, win-x64, self-contained) into a new folder, with its own
/// <c>--artifacts-path</c> so Visual Studio's bin and obj folders are never written, then moves the finished folder into <c>versions/&lt;version&gt;</c>
/// and swaps <c>current.json</c> to it. A version is published at most once; a completed version folder is never written again.
/// </summary>
internal static class NodeAdapterBuilds
{
    private const string CurrentFile = "current.json";

    /// <summary><c>artifacts/node-adapter</c> in <paramref name="checkoutRoot"/>.</summary>
    public static string Root(string checkoutRoot) => Path.Combine(checkoutRoot, "artifacts", "node-adapter");

    /// <summary>The current published node adapter's dll, when the pointer names a well-formed version whose dll exists; it never leaves <c>versions</c>.</summary>
    public static string? CurrentDll(string checkoutRoot)
    {
        try
        {
            var root = Root(checkoutRoot);
            var pointer = Path.Combine(root, CurrentFile);
            if (!File.Exists(pointer) || new FileInfo(pointer).Length > 4096) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(pointer));
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("version", out var value) || value.GetString() is not { } version || !IsVersion(version)) return null;
            var dll = Path.Combine(root, "versions", version, "CoCern.Adapter.dll");
            return File.Exists(dll) ? dll : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return null; }
    }

    internal static bool IsVersion(string? version) => version is { Length: 64 } && version.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    /// <summary>Points <c>current.json</c> at <paramref name="version"/> in one rename, so a reader sees the old version or the new one, never a partial file.</summary>
    internal static void WriteCurrent(string root, string version)
    {
        var temporary = Path.Combine(root, ".current-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { version }));
            File.Move(temporary, Path.Combine(root, CurrentFile), overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

/// <summary>Runs one debounced node adapter publish; see <see cref="NodeAdapterBuilds"/>. Every failure is logged without its text and never stops the hub.</summary>
internal sealed class NodeAdapterPublisher(ILogger logger)
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Stale = TimeSpan.FromHours(2);
    private const int KeptVersions = 3;

    /// <summary>
    /// Environment the hub's own process carries that a child build must not see: its configuration (which can hold tokens), the debugger's startup
    /// hooks, and MSBuild locations of whatever started the hub.
    /// </summary>
    private static readonly string[] HiddenPrefixes = ["Relay__", "Relay:", "COCERN_", "ASPNETCORE_", "DOTNET_STARTUP_HOOKS", "DOTNET_MODIFIABLE_ASSEMBLIES", "DOTNET_WATCH", "MSBuild", "MSBUILD"];

    public async Task PublishAsync(string checkoutRoot, string version, CancellationToken ct)
    {
        version = version.ToLowerInvariant();
        if (!NodeAdapterBuilds.IsVersion(version)) return;
        // A hub that Visual Studio restarts again within the debounce never starts a publish.
        await Task.Delay(Debounce, ct);
        var root = NodeAdapterBuilds.Root(checkoutRoot);
        var final = Path.Combine(root, "versions", version);
        string? work = null;
        string? output = null;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "versions"));
            if (File.Exists(Path.Combine(final, "CoCern.Adapter.dll")))
            {
                if (NodeAdapterBuilds.CurrentDll(checkoutRoot) is not { } current || !string.Equals(Path.GetDirectoryName(current), final, StringComparison.OrdinalIgnoreCase)) NodeAdapterBuilds.WriteCurrent(root, version);
                return;
            }
            // One publish at a time across hub processes; a hub that finds it held leaves this version to the next start.
            using var owner = TryLock(Path.Combine(root, "publish.lock"));
            if (owner is null) { logger.LogInformation("Node adapter publish skipped: another publish is running."); return; }
            RemoveStale(root);
            var id = version[..12] + "-" + Guid.NewGuid().ToString("N")[..8];
            work = Path.Combine(root, "work", id);
            output = Path.Combine(root, "versions", ".staging-" + id);
            Directory.CreateDirectory(work);
            Directory.CreateDirectory(Path.Combine(root, "logs"));
            var log = Path.Combine(root, "logs", version[..12] + ".log");
            var started = Stopwatch.StartNew();
            var exit = await RunAsync(checkoutRoot, work, output, log, ct);
            if (exit != 0)
            {
                logger.LogWarning("Node adapter publish {Version} failed with exit code {ExitCode} after {Seconds}s; see artifacts/node-adapter/logs. Nodes keep the newest self-contained build found.", version[..12], exit, (int)started.Elapsed.TotalSeconds);
                return;
            }
            if (NodePackageMetadataReader.Read(output) is not { SelfContained: true })
            {
                logger.LogWarning("Node adapter publish {Version} produced no self-contained package; it is not offered to nodes.", version[..12]);
                return;
            }
            if (!Directory.Exists(final)) Directory.Move(output, final);
            output = null;
            NodeAdapterBuilds.WriteCurrent(root, version);
            PruneVersions(root, version);
            logger.LogInformation("Node adapter published: {Version} in {Seconds}s. Enrolled nodes are offered it on their next announcement.", version[..12], (int)started.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or ArgumentException)
        {
            // Settings and paths can be in exception text; only the type is logged.
            logger.LogWarning("Node adapter publish failed ({FailureType}). Nodes keep the newest self-contained build found.", exception.GetType().Name);
        }
        finally
        {
            if (output is not null) TryDelete(output);
            if (work is not null) TryDelete(work);
        }
    }

    /// <summary>The <c>Build-CoCern.ps1 -Publish</c> adapter command, isolated to <paramref name="work"/>; its output goes to the log file only. Stopping the hub stops it.</summary>
    private static async Task<int> RunAsync(string checkoutRoot, string work, string output, string log, CancellationToken ct)
    {
        var start = new ProcessStartInfo(DashboardConnect.DotnetHost())
        {
            WorkingDirectory = checkoutRoot, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            "publish", Path.Combine(checkoutRoot, "src", "CoCern.Adapter", "CoCern.Adapter.csproj"), "-c", "Release", "-r", "win-x64", "--self-contained", "true",
            "-o", output, "--artifacts-path", work, "--disable-build-servers", "--nologo"
        }) start.ArgumentList.Add(argument);
        foreach (var name in start.Environment.Keys.Where(name => HiddenPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).ToArray()) start.Environment.Remove(name);
        using var writer = new StreamWriter(log, append: false, new UTF8Encoding(false));
        var gate = new object();
        var closed = false;
        // Output events run on pool threads and may arrive late; an exception there would end the hub, so the log only ever loses lines.
        void Write(string? line)
        {
            if (line is null) return;
            lock (gate)
            {
                if (closed) return;
                try { writer.WriteLine(line); }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException) { closed = true; }
            }
        }
        try
        {
            return await RunProcessAsync(start, Write, ct);
        }
        finally { lock (gate) closed = true; }
    }

    private static async Task<int> RunProcessAsync(ProcessStartInfo start, Action<string?> write, CancellationToken ct)
    {
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => write(e.Data);
        process.ErrorDataReceived += (_, e) => write(e.Data);
        if (!process.Start()) return -1;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Timeout);
        try { await process.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (Win32Exception) { }
            await process.WaitForExitAsync(CancellationToken.None);
            process.WaitForExit();
            ct.ThrowIfCancellationRequested();
            return -2; // Timed out.
        }
        process.WaitForExit(); // Drains the redirected output.
        return process.ExitCode;
    }

    private static FileStream? TryLock(string path)
    {
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
    }

    /// <summary>Work and staging folders a stopped hub left behind; anything recent may still belong to a running publish.</summary>
    private static void RemoveStale(string root)
    {
        var cutoff = DateTime.UtcNow - Stale;
        var candidates = new List<DirectoryInfo>();
        if (Directory.Exists(Path.Combine(root, "work"))) candidates.AddRange(new DirectoryInfo(Path.Combine(root, "work")).EnumerateDirectories());
        candidates.AddRange(new DirectoryInfo(Path.Combine(root, "versions")).EnumerateDirectories(".staging-*"));
        foreach (var folder in candidates.Where(folder => folder.LastWriteTimeUtc < cutoff)) TryDelete(folder.FullName);
    }

    /// <summary>Keeps the current version and the newest others; the hub's zip cache serves archives, not these folders, so a node mid-download is unaffected.</summary>
    private static void PruneVersions(string root, string current)
    {
        var versions = new DirectoryInfo(Path.Combine(root, "versions")).EnumerateDirectories().Where(folder => NodeAdapterBuilds.IsVersion(folder.Name) && folder.Name != current)
            .OrderByDescending(folder => folder.CreationTimeUtc).Skip(KeptVersions - 1);
        foreach (var folder in versions) TryDelete(folder.FullName);
    }

    private static void TryDelete(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
