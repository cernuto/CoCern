using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CoCern.Adapter.Node;

public static class NodeSupervisor
{
    public static Task<int> RunAsync(string nodeFolder, string bootstrapAdapterDll, string? dotnetPath, CancellationToken ct) =>
        RunCoreAsync(nodeFolder, bootstrapAdapterDll, () => NodePackage.ReadHostCapabilities(dotnetPath), StartOwnedChild, TimeProvider.System, ct);

    internal static Task<int> RunAsync(string nodeFolder, string bootstrapAdapterDll, NodeHostCapabilities host, Func<NodeLaunchSpec, INodeChild?> start, TimeProvider clock, CancellationToken ct) =>
        RunCoreAsync(nodeFolder, bootstrapAdapterDll, () => host, start, clock, ct);

    internal static INodeChild? StartOwnedChild(NodeLaunchSpec launch) => WindowsNodeChild.Start(launch);

    private static async Task<int> RunCoreAsync(string nodeFolder, string bootstrapAdapterDll, Func<NodeHostCapabilities> readHost, Func<NodeLaunchSpec, INodeChild?> start, TimeProvider clock, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var root = NodePackage.ValidatedUpdateRoot(Path.Combine(nodeFolder, "updates"), create: true);
            var originalFolder = Path.GetDirectoryName(root)!;
            var lockPath = Path.Combine(root, "owner.lock");
            using var owner = OpenOwner(lockPath);
            var read = NodeUpdateState.Read(root);
            if (read.Status == NodeUpdateReadStatus.Corrupt) { Console.Error.WriteLine("node-update:repair-needed"); return 1; }
            NodeUpdateRecord record;
            if (read.Status == NodeUpdateReadStatus.Missing)
            {
                var image = await NodePackage.ImportBootstrapAsync(bootstrapAdapterDll, root, ct);
                record = new(1, image, null, null, null, NodeUpdatePhase.Committed, null, null);
                NodeUpdateState.Write(root, record);
            }
            else record = read.Record!;
            if (record.Phase == NodeUpdatePhase.Starting)
            {
                record = Rollback(root, record, clock);
            }
            string? refusedRelease = null;
            DateTimeOffset? refusedAt = null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var candidate = record.Phase == NodeUpdatePhase.Starting;
                var image = candidate ? record.Pending! : record.Current;
                var readyClean = ClearHandoff(root, "ready.json");
                var requestClean = ClearHandoff(root, "request.json");
                if (candidate && (!readyClean || !requestClean)) { record = Rollback(root, record, clock); continue; }
                // Launch rehashes the immutable stored image on every attempt.
                var launch = NodePackage.Launch(image, originalFolder, record.ActivationNonce, readHost());
                if (launch is not null)
                {
                    var startup = new NodeWorkerStartupContext(record.ActivationNonce, Guid.NewGuid(), refusedRelease, refusedAt);
                    var arguments = launch.Arguments.ToList();
                    var contextIndex = arguments.IndexOf("--activation-nonce");
                    if (contextIndex >= 0) arguments[contextIndex + 1] = startup.ToArgument();
                    else arguments.AddRange(["--activation-nonce", startup.ToArgument()]);
                    launch = launch with { Arguments = arguments.ToArray(), StartupContext = startup };
                }
                var child = launch is null ? null : start(launch);
                if (child is null)
                {
                    if (candidate) { record = Rollback(root, record, clock); continue; }
                    Console.Error.WriteLine("node-update:current-unavailable"); return 1;
                }
                int exit;
                var startupFailed = false;
                await using (child)
                {
                    var ended = child.WaitForExitAsync(ct);
                    if (record.ActivationNonce is { } nonce)
                    {
                        while (!startupFailed && record.Phase != NodeUpdatePhase.Committed)
                        {
                            if (!await ReadyAsync(root, child, ended, nonce, image.ContentId, launch!.StartupContext!.LaunchId, candidate, clock, ct)) { startupFailed = true; break; }
                            else
                            {
                                var committed = record with
                                {
                                    Current = image,
                                    Previous = candidate ? record.Current : record.Previous,
                                    Pending = null,
                                    ActivationNonce = null,
                                    Phase = NodeUpdatePhase.Committed
                                };
                                try { NodeUpdateState.Write(root, committed); record = committed; }
                                catch (Exception exception) when (JournalFailure(exception))
                                {
                                    // The primary may already be committed even when refreshing its backup failed.
                                    var authoritative = NodeUpdateState.ReadPrimary(root);
                                    if (authoritative is { Phase: NodeUpdatePhase.Committed } durable && durable.Current.ContentId == image.ContentId) record = durable;
                                    else if (candidate) startupFailed = true;
                                    // A fully verified historical Current remains available even when
                                    // its authenticated recovery cannot yet clear durable evidence.
                                }
                            }
                            if (!candidate && record.Phase != NodeUpdatePhase.Committed)
                                await Task.WhenAny(ended, Task.Delay(TimeSpan.FromMilliseconds(100), clock, ct));
                        }
                    }
                    if (!startupFailed) exit = await ended;
                    else exit = 1;
                } // Only this owned child is stopped/waited before retained startup.
                if (startupFailed)
                {
                    if (candidate) { record = Rollback(root, record, clock); continue; }
                    return 1; // Retain Rollback evidence until an authenticated retained startup succeeds.
                }
                if (exit != 75) return exit;
                if (record.Phase != NodeUpdatePhase.Committed || !requestClean || !readyClean) continue;
                var request = NodeUpdateState.ReadRequest(root);
                if (request is null || request.Candidate.ContentId == record.Current.ContentId ||
                    request.Candidate.ReleaseId == record.LastFailedReleaseId && clock.GetUtcNow() - record.LastFailedAtUtc < TimeSpan.FromMinutes(15)) continue;
                var starting = record with { Phase = NodeUpdatePhase.Starting, Pending = request.Candidate, ActivationNonce = Guid.NewGuid(), LastFailedReleaseId = refusedRelease ?? record.LastFailedReleaseId, LastFailedAtUtc = refusedAt ?? record.LastFailedAtUtc };
                try { NodeUpdateState.Write(root, starting); record = starting; }
                catch (Exception exception) when (JournalFailure(exception))
                {
                    var authoritative = NodeUpdateState.Read(root).Record ?? throw new InvalidDataException("node-journal-invalid");
                    refusedRelease = request.Candidate.ReleaseId;
                    refusedAt = clock.GetUtcNow();
                    record = authoritative.Phase == NodeUpdatePhase.Starting ? Rollback(root, authoritative, clock) : authoritative;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return 0; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception or System.Security.SecurityException)
        {
            Console.Error.WriteLine("node-update:supervisor-unavailable");
            return 1;
        }
    }

    private static bool JournalFailure(Exception exception) => exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException;

    private static NodeUpdateRecord Rollback(string root, NodeUpdateRecord record, TimeProvider clock)
    {
        var rollback = record with { Phase = NodeUpdatePhase.Rollback, LastFailedReleaseId = record.Pending!.ReleaseId, LastFailedAtUtc = record.Pending.ReleaseId is null ? null : clock.GetUtcNow() };
        try { NodeUpdateState.Write(root, rollback); return rollback; }
        catch (Exception exception) when (JournalFailure(exception))
        {
            var authoritative = NodeUpdateState.Read(root).Record ?? throw new InvalidDataException("node-journal-invalid");
            if (authoritative.Phase == NodeUpdatePhase.Committed) return authoritative;
            if (authoritative.Current.ContentId != record.Current.ContentId) throw new InvalidDataException("node-journal-invalid");
            // Only the launch decision is Rollback in memory. The durable Pending/nonce
            // remain untouched until authenticated retained recovery can commit.
            return authoritative with { Phase = NodeUpdatePhase.Rollback, LastFailedReleaseId = rollback.LastFailedReleaseId, LastFailedAtUtc = rollback.LastFailedAtUtc };
        }
    }

    private static async Task<bool> ReadyAsync(string root, INodeChild child, Task<int> ended, Guid nonce, string contentId, Guid launchId, bool candidate, TimeProvider clock, CancellationToken ct)
    {
        var started = clock.GetTimestamp();
        while (!candidate || clock.GetElapsedTime(started) < TimeSpan.FromSeconds(30))
        {
            ct.ThrowIfCancellationRequested();
            if (child.HasExited || ended.IsCompleted) return false;
            var ready = NodeUpdateState.ReadReady(root);
            if (ready is not null)
            {
                if (ready.ActivationNonce == nonce && ready.ContentId == contentId && ready.ProcessId == child.ProcessId && ready.LaunchId == launchId)
                    return !child.HasExited && !ended.IsCompleted;
                if (candidate) return false;
            }
            if (ended.IsCompleted) return false;
            await Task.WhenAny(ended, Task.Delay(TimeSpan.FromMilliseconds(100), clock, ct));
        }
        return false;
    }

    private static bool ClearHandoff(string root, string name)
    {
        var path = Path.Combine(root, name);
        try
        {
            _ = File.GetAttributes(path);
            NodePackage.ValidatePrivateFile(path);
            File.Delete(path);
            return true;
        }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (Exception exception) when (JournalFailure(exception)) { return false; }
    }

    private static FileStream OpenOwner(string path)
    {
        if (!File.Exists(path)) return NodePackage.CreatePrivateFile(path);
        NodePackage.ValidatePrivateFile(path);
        return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
}

internal interface INodeChild : IAsyncDisposable
{
    bool HasExited { get; }
    int ProcessId { get; }
    Task<int> WaitForExitAsync(CancellationToken ct);
}

/// <summary>Owns only the direct worker. Its descendants deliberately leave this job.</summary>
internal sealed class WindowsNodeChild : INodeChild
{
    private readonly SafeFileHandle job;
    private readonly SafeProcessHandle process;
    private readonly object disposalGate = new();
    private Task? disposal;
    public int ProcessId { get; }
    public bool HasExited => WaitForSingleObject(process, 0) switch
    {
        0 => true,
        258 => false,
        _ => throw new Win32Exception()
    };

    private WindowsNodeChild(SafeFileHandle job, SafeProcessHandle process, int processId)
    {
        this.job = job;
        this.process = process;
        ProcessId = processId;
    }

    internal static WindowsNodeChild? Start(NodeLaunchSpec launch)
    {
        if (!OperatingSystem.IsWindows()) return null;
        SafeFileHandle? job = null;
        SafeProcessHandle? process = null;
        SafeWaitHandle? thread = null;
        nint attributes = 0;
        nint jobList = 0;
        var initialized = false;
        var transferred = false;
        try
        {
            if (launch is null || !Path.IsPathFullyQualified(launch.FileName) || !File.Exists(launch.FileName) || launch.FileName.Contains('\0') || launch.Arguments is null || launch.Arguments.Length > 256) return null;
            var command = new StringBuilder();
            AppendArgument(command, launch.FileName);
            foreach (var argument in launch.Arguments)
            {
                command.Append(' ');
                AppendArgument(command, argument);
            }
            if (command.Length >= 32767) return null;
            command.EnsureCapacity(command.Length + 1);

            job = CreateJobObjectW(0, null);
            if (job.IsInvalid) throw new Win32Exception();
            // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK.
            // Only this direct worker belongs to the job; queued native model work must survive it.
            var limits = new ExtendedJobLimits { Basic = new BasicJobLimits { LimitFlags = 0x2000 | 0x1000 } };
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedJobLimits>())) throw new Win32Exception();

            nuint bytes = 0;
            _ = InitializeProcThreadAttributeList(0, 1, 0, ref bytes); // The sizing call intentionally fails.
            if (bytes is 0 or > 65536) throw new InvalidOperationException();
            attributes = Marshal.AllocHGlobal(checked((nint)bytes));
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref bytes)) throw new Win32Exception();
            initialized = true;
            jobList = Marshal.AllocHGlobal(nint.Size);
            Marshal.WriteIntPtr(jobList, job.DangerousGetHandle());
            // PROC_THREAD_ATTRIBUTE_JOB_LIST assigns the job as part of creation, before user code.
            // https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute
            if (!UpdateProcThreadAttribute(attributes, 0, 0x0002000D, jobList, (nuint)nint.Size, 0, 0)) throw new Win32Exception();
            var startup = new ExtendedStartupInfo { Startup = new StartupInfo { Size = (uint)Marshal.SizeOf<ExtendedStartupInfo>() }, Attributes = attributes };
            const uint suspendedHiddenExtended = 0x00000004 | 0x08000000 | 0x00080000;
            if (!CreateProcessW(launch.FileName, command, 0, 0, false, suspendedHiddenExtended, 0, null, ref startup, out var created)) throw new Win32Exception();
            process = new SafeProcessHandle(created.Process, ownsHandle: true);
            thread = new SafeWaitHandle(created.Thread, ownsHandle: true);
            if (!IsProcessInJob(process, job, out var member) || !member) throw new Win32Exception();
            if (ResumeThread(thread) != 1) throw new Win32Exception();
            var child = new WindowsNodeChild(job, process, checked((int)created.ProcessId));
            transferred = true;
            return child;
        }
        catch (Exception exception) when (exception is Win32Exception or ArgumentException or InvalidOperationException or NotSupportedException or OverflowException or IOException) { return null; }
        finally
        {
            thread?.Dispose();
            if (initialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != 0) Marshal.FreeHGlobal(attributes);
            if (jobList != 0) Marshal.FreeHGlobal(jobList);
            if (!transferred)
            {
                job?.Dispose();
                if (process is { IsInvalid: false })
                {
                    // A failed membership/resume check must not leave even a suspended owned process.
                    _ = TerminateProcess(process, 1);
                    _ = WaitForSingleObject(process, uint.MaxValue);
                }
                process?.Dispose();
            }
        }
    }

    private static void AppendArgument(StringBuilder command, string argument)
    {
        if (argument is null || argument.Contains('\0') || argument.Length >= 32767) throw new ArgumentException();
        command.Append('"');
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { backslashes++; continue; }
            if (character == '"') command.Append('\\', checked(backslashes * 2 + 1));
            else command.Append('\\', backslashes);
            command.Append(character);
            backslashes = 0;
        }
        command.Append('\\', checked(backslashes * 2));
        command.Append('"');
        if (command.Length >= 32767) throw new ArgumentException();
    }

    public async Task<int> WaitForExitAsync(CancellationToken ct)
    {
        while (true)
        {
            var status = WaitForSingleObject(process, 0);
            if (status == 0)
            {
                if (!GetExitCodeProcess(process, out var exitCode)) throw new Win32Exception();
                return unchecked((int)exitCode);
            }
            if (status != 258) throw new Win32Exception();
            await Task.Delay(25, ct);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (disposalGate) return new(disposal ??= StopAsync());
    }

    private async Task StopAsync()
    {
        job.Dispose();
        try { _ = await WaitForExitAsync(CancellationToken.None); }
        finally { process.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicJobLimits
    {
        public long ProcessUserTime, JobUserTime;
        public uint LimitFlags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedJobLimits
    {
        public BasicJobLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint Size;
        public nint Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XChars, YChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize;
        public nint ReservedBytes, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedStartupInfo
    {
        public StartupInfo Startup;
        public nint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateJobObjectW(nint security, string? name);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedJobLimits information, uint length);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(nint attributes, uint count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(nint attributes, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returnedSize);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void DeleteProcThreadAttributeList(nint attributes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string application, [In, Out] StringBuilder command, nint processSecurity, nint threadSecurity, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment, string? directory, ref ExtendedStartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(SafeProcessHandle process, SafeFileHandle job, [MarshalAs(UnmanagedType.Bool)] out bool member);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern uint ResumeThread(SafeWaitHandle thread);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
}
