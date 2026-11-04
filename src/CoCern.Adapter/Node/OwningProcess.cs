using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CoCern.Adapter.Node;

/// <summary>What the process table says about one process. The start time tells it apart from a later process that reuses its ID.</summary>
internal sealed record ProcessFacts(int Id, int ParentId, string Name, DateTime StartedUtc);

/// <summary>
/// The processes a Stop hook's waiter depends on. Closing a client does not end the processes it started, so the waiter would outlive an
/// app restart, block the new app's hooks (one waiter per chat) and spend its next wake on an app that is gone. The waiter watches the
/// chain of processes above it and ends when any of them does. Where parents can't be read (neither Windows nor Linux) the chain is
/// empty and the waiter runs as it would unwatched.
/// </summary>
internal static class OwningProcess
{
    private const int MaxDepth = 32;
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(1);

    /// <summary>Waits until a process above this one is gone. Never completes while the chain is alive, or when it is empty.</summary>
    public static Task WaitUntilGoneAsync(CancellationToken cancellationToken) =>
        WaitUntilGoneAsync(Chain(Environment.ProcessId, Read), IsAlive, CheckEvery, cancellationToken);

    /// <summary>
    /// The ancestors of <paramref name="processId"/>, nearest first, up to and including the client that ran the hook. Above the client are
    /// the desktop's own processes, which can restart without touching the chat. Without a known client it is every ancestor that can be read.
    /// </summary>
    internal static IReadOnlyList<ProcessFacts> Chain(int processId, Func<int, ProcessFacts?> read)
    {
        var chain = new List<ProcessFacts>();
        var child = read(processId);
        while (child is not null && chain.Count < MaxDepth)
        {
            // A parent younger than its child, or one already listed, is a reused ID and not an ancestor.
            if (read(child.ParentId) is not { } parent || parent.StartedUtc > child.StartedUtc || parent.Id == processId || chain.Any(process => process.Id == parent.Id)) break;
            chain.Add(parent);
            if (IsClient(parent.Name)) break;
            child = parent;
        }
        return chain;
    }

    internal static async Task WaitUntilGoneAsync(IReadOnlyList<ProcessFacts> chain, Func<ProcessFacts, bool> isAlive, TimeSpan checkEvery, CancellationToken cancellationToken)
    {
        if (chain.Count == 0) await Task.Delay(Timeout.Infinite, cancellationToken);
        while (chain.All(isAlive)) await Task.Delay(checkEvery, cancellationToken);
    }

    internal static bool IsAlive(ProcessFacts process)
    {
        try
        {
            using var current = Process.GetProcessById(process.Id);
            return !current.HasExited && (current.StartTime.ToUniversalTime() - process.StartedUtc).Duration() < StartTolerance;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) { return false; }
        // A process this user may no longer inspect: no proof that it is gone, and a false exit would leave the chat unreachable.
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or NotSupportedException) { return true; }
    }

    internal static ProcessFacts? Read(int processId)
    {
        try
        {
            if (ParentOf(processId) is not { } parent) return null;
            using var process = Process.GetProcessById(processId);
            return new ProcessFacts(processId, parent, process.ProcessName, process.StartTime.ToUniversalTime());
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool IsClient(string name) =>
        name.StartsWith("claude", StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFileNameWithoutExtension(name), "node", StringComparison.OrdinalIgnoreCase);

    private static int? ParentOf(int processId)
    {
        if (OperatingSystem.IsWindows()) return WindowsParent(processId);
        if (!OperatingSystem.IsLinux()) return null;
        // /proc/<pid>/stat: pid (name) state ppid ...; the name may hold spaces and parentheses, so read after the last ')'.
        var stat = File.ReadAllText($"/proc/{processId}/stat");
        var fields = stat[(stat.LastIndexOf(')') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length > 1 && int.TryParse(fields[1], out var parent) ? parent : null;
    }

    private static int? WindowsParent(int processId)
    {
        const uint QueryLimitedInformation = 0x1000;
        var handle = OpenProcess(QueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var information = default(ProcessBasicInformation);
            return NtQueryInformationProcess(handle, 0, ref information, Marshal.SizeOf<ProcessBasicInformation>(), out _) == 0 ? (int)information.InheritedFromUniqueProcessId : null;
        }
        finally { CloseHandle(handle); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, ref ProcessBasicInformation information, int length, out int returned);
}
