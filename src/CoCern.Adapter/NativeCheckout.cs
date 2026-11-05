using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoCern.Contracts;
using Microsoft.Win32.SafeHandles;

namespace CoCern.Adapter;

/// <summary>
/// A native event's checkout. <see cref="Root"/> is where the files physically are: every junction and symlink on the way there is resolved by the
/// operating system, and a path it cannot resolve yields no checkout rather than a lexical one. Git's own output is only a report to verify. The
/// binding key in <see cref="Node.NativeJoinBinding"/> already comes from the same Windows final path, so a local binding keeps its key.
/// </summary>
internal sealed record NativeCheckout(string Root, string? Common, string? Origin)
{
    private const int MaxPathLength = 32768;

    internal static NativeCheckout Inspect(string cwd) => Inspect(cwd, CoordinationTools.NativeGitTopLevel);

    /// <param name="cwd">The native event's directory.</param>
    /// <param name="gitTopLevel">What reports the top level for a physical directory; a parameter so a test can stand in for a Git that does not resolve links.</param>
    internal static NativeCheckout Inspect(string cwd, Func<string, string?> gitTopLevel)
    {
        var start = Physical(cwd);
        if (gitTopLevel(start) is not { } reported) throw new IOException("The native event needs a verified Git checkout.");
        var root = Physical(reported);
        // core.worktree can name any folder at all, so a reported root proves nothing until the event's directory is found physically inside it.
        if (!Inside(root, start)) throw new IOException("Git reported a checkout that the native event directory is not physically inside.");
        var common = CoordinationTools.NativeGitRead(root, "rev-parse", "--path-format=absolute", "--git-common-dir");
        var origin = NativeGitOrigin.Canonical(CoordinationTools.NativeGitRead(root, "config", "--local", "--get-all", "remote.origin.url"));
        return new(root, PhysicalOrNull(common), origin);
    }

    internal JsonElement Preflight(bool local)
    {
        var evidence = new Dictionary<string,string>();
        if (local)
        {
            evidence["checkout"] = Hash(Root);
            if (Common is not null) evidence["common"] = Hash(Common);
        }
        if (Origin is not null) evidence["origin"] = Origin;
        if (evidence.Count == 0) throw new IOException("The remote checkout needs a supported Git origin.");
        return JsonSerializer.SerializeToElement(new { nativePreflight = evidence });
    }

    internal static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Whether a file or directory is physically inside this checkout, however it is spelled. One that cannot be resolved is not.</summary>
    internal bool PhysicallyContains(string path)
    {
        try { return Inside(Root, Resolve(path)); }
        catch (IOException) { return false; }
    }

    /// <summary>
    /// The one physical spelling of an existing directory, or an <see cref="IOException"/>; never the path as given.
    /// <para>Windows: the final path of an opened handle, which resolves every component, restores the case the filesystem reports, expands short
    /// names and drops any <c>\\?\</c> prefix. A subst drive resolves to the local folder behind it. Anything on a network fails closed, because
    /// another machine's folder cannot be proved: a UNC path is refused before it is opened, and the final path catches a mapped drive, a subst onto
    /// a share and a link to one. A <c>\\.\</c> device path is refused too.</para>
    /// <para>Elsewhere: the C library's realpath. It resolves every link, but it does not restore case on a case-insensitive macOS volume.</para>
    /// </summary>
    internal static string Physical(string path)
    {
        var physical = Resolve(path);
        if (!Directory.Exists(physical)) throw new IOException("The native path is not a directory.");
        return physical;
    }

    // Optional evidence: a common directory that cannot be resolved is left out, never sent in a lexical form.
    private static string? PhysicalOrNull(string? path)
    {
        if (path is null) return null;
        try { return Physical(path); }
        catch (IOException) { return null; }
    }

    private static bool Inside(string physicalRoot, string physicalPath)
    {
        var prefix = Path.EndsInDirectorySeparator(physicalRoot) ? physicalRoot : physicalRoot + Path.DirectorySeparatorChar;
        return string.Equals(physicalRoot, physicalPath, StringComparison.Ordinal) || physicalPath.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static string Resolve(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Length > MaxPathLength || path.Any(char.IsControl)) throw new IOException("The native path must be absolute and bounded.");
        return Path.TrimEndingDirectorySeparator(OperatingSystem.IsWindows() ? ResolveWindows(path) : ResolveUnix(path));
    }

    private static string ResolveWindows(string path)
    {
        // From the text alone, and before GetFullPath, which can itself reach a share to expand a short name.
        var separators = path.Replace('/', '\\');
        if (separators.StartsWith(@"\\", StringComparison.Ordinal) && !IsExtendedDrivePath(separators)) throw new IOException("A network or device path cannot prove a physical checkout.");
        // No access rights, every share mode, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS so that a directory opens. A dangling link fails here.
        using var handle = OpenExisting(Path.GetFullPath(path), 0, 7, nint.Zero, 3, 0x02000000, nint.Zero);
        if (handle.IsInvalid) throw new IOException("The native path could not be opened, so its physical location is unknown.");
        var name = new StringBuilder(MaxPathLength);
        var length = FinalPath(handle, name, (uint)name.Capacity, 0);
        if (length == 0 || length >= name.Capacity) throw new IOException("The native path could not be resolved to its physical location.");
        var final = name.ToString();
        if (final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) throw new IOException("A network path cannot prove a physical checkout.");
        if (!final.StartsWith(@"\\?\", StringComparison.Ordinal)) throw new IOException("The native path resolved to an unexpected form.");
        return final[4..];
    }

    private static bool IsExtendedDrivePath(string path) =>
        path.Length >= 7 && path.StartsWith(@"\\?\", StringComparison.Ordinal) && char.IsAsciiLetter(path[4]) && path[5] == ':' && path[6] == '\\';

    private static string ResolveUnix(string path)
    {
        nint resolved;
        try { resolved = RealPath(path, nint.Zero); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        { throw new IOException("The platform's path resolver is unavailable.", error); }
        if (resolved == nint.Zero) throw new IOException("The native path could not be resolved to its physical location.");
        try { return Marshal.PtrToStringUTF8(resolved) ?? throw new IOException("The native path could not be resolved to its physical location."); }
        finally { Free(resolved); }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle OpenExisting(string path, uint access, uint share, nint security, uint mode, uint flags, nint template);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint FinalPath(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    private static extern nint RealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, nint resolved);
    [DllImport("libc", EntryPoint = "free")]
    private static extern void Free(nint pointer);
}
