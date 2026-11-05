using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CoCern.Adapter;

/// <summary>
/// Eligibility for protected file scope (VerifiedNativeFileScope), and nothing else: <see cref="NativeCheckout.Inspect(string)"/>, existing bindings and
/// UNC native joins do not use it and are not limited by it. It proves where a checkout physically is, or refuses with an <see cref="IOException"/>.
/// Git's report of the root is one input to verify: this machine's filesystem decides, not the spelling and not Git.
/// </summary>
internal static class ProtectedCheckoutRoot
{
    private const int MaxPathLength = 32768;

    /// <summary>The proven physical root of the checkout holding <paramref name="cwd"/>. Git runs only after the directory is proven local.</summary>
    internal static string Prove(string cwd)
    {
        var start = Physical(cwd);
        return Prove(start, CoordinationTools.NativeGitTopLevel(start));
    }

    /// <summary>The same proof for a root Git has already reported, such as <see cref="NativeCheckout.Root"/>.</summary>
    internal static string Prove(string cwd, string? gitRoot)
    {
        var start = Physical(cwd);
        if (gitRoot is null) throw new IOException("Protected file scope needs a verified Git checkout.");
        var root = Physical(gitRoot);
        // core.worktree can name any folder at all, so a reported root proves nothing until the directory is found physically inside it.
        if (!Inside(root, start)) throw new IOException("Git reported a checkout that the directory is not physically inside.");
        return root;
    }

    /// <summary>Whether a file or directory is physically inside a root, however either is spelled. Anything that cannot be proven is not.</summary>
    internal static bool Contains(string root, string path)
    {
        try { return Inside(Physical(root), Resolve(path)); }
        catch (IOException) { return false; }
    }

    /// <summary>
    /// The one physical spelling of an existing local directory.
    /// <para>Windows: the final path of an opened handle, which resolves every component, restores the case the filesystem reports, expands short
    /// names and drops any <c>\\?\</c> prefix. A subst onto a local folder resolves to that folder. A UNC or device path is refused from its text, before
    /// anything opens it; the final path catches a mapped network drive, a subst onto a share and a link to one.</para>
    /// <para>Elsewhere: the C library's realpath. It resolves every link, but it does not restore case on a case-insensitive macOS volume, and it
    /// cannot tell a network mount from a local one.</para>
    /// </summary>
    internal static string Physical(string path)
    {
        var physical = Resolve(path);
        if (!Directory.Exists(physical)) throw new IOException("The path is not a directory.");
        return physical;
    }

    private static bool Inside(string physicalRoot, string physicalPath)
    {
        var prefix = Path.EndsInDirectorySeparator(physicalRoot) ? physicalRoot : physicalRoot + Path.DirectorySeparatorChar;
        return string.Equals(physicalRoot, physicalPath, StringComparison.Ordinal) || physicalPath.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static string Resolve(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Length > MaxPathLength || path.Any(char.IsControl)) throw new IOException("The path must be absolute and bounded.");
        return Path.TrimEndingDirectorySeparator(OperatingSystem.IsWindows() ? ResolveWindows(path) : ResolveUnix(path));
    }

    private static string ResolveWindows(string path)
    {
        // From the text alone, and before GetFullPath, which can itself reach a share to expand a short name.
        var separators = path.Replace('/', '\\');
        if (separators.StartsWith(@"\\", StringComparison.Ordinal) && !IsExtendedDrivePath(separators)) throw new IOException("A network or device path cannot prove a physical checkout.");
        // No access rights, every share mode, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS so that a directory opens. A dangling link fails here.
        using var handle = OpenExisting(Path.GetFullPath(path), 0, 7, nint.Zero, 3, 0x02000000, nint.Zero);
        if (handle.IsInvalid) throw new IOException("The path could not be opened, so its physical location is unknown.");
        var name = new StringBuilder(MaxPathLength);
        var length = FinalPath(handle, name, (uint)name.Capacity, 0);
        if (length == 0 || length >= name.Capacity) throw new IOException("The path could not be resolved to its physical location.");
        var final = name.ToString();
        if (final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) throw new IOException("A network path cannot prove a physical checkout.");
        if (!final.StartsWith(@"\\?\", StringComparison.Ordinal)) throw new IOException("The path resolved to an unexpected form.");
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
        if (resolved == nint.Zero) throw new IOException("The path could not be resolved to its physical location.");
        try { return Marshal.PtrToStringUTF8(resolved) ?? throw new IOException("The path could not be resolved to its physical location."); }
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
