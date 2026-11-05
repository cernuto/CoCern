using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CoCern.Adapter.Node;

internal sealed record EditClaimTarget(string IoPath, string RelativeKey);
internal sealed record EditClaimResolution(bool Supported, string Reason, IReadOnlyList<EditClaimTarget> Targets);

// Tests-first internal consumer contract, not a native input/authentication API.
// Only a future verified-scope guard may supply root/cwd. No hook is registered.
internal static class EditClaimResolver
{
    // Initial support is deliberately local NTFS, ASCII names, and directories
    // whose case-insensitive behavior is positively queried. Unknown semantics,
    // reparse points and multiply-linked files refuse; no target is ever created.
    public static EditClaimResolution Resolve(string root, string cwd, IReadOnlyList<string> targets)
    {
        if (!OperatingSystem.IsWindows()) return Refused("unsupported-platform");
        try
        {
            if (targets.Count is < 1 or > 128 || !SafeAbsolute(root) || !SafeAbsolute(cwd)) return Refused("invalid-input");
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var fullCwd = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd));
            var drive = new DriveInfo(Path.GetPathRoot(fullRoot)!);
            if (drive.DriveType != DriveType.Fixed || drive.DriveFormat != "NTFS") return Refused("unsupported-filesystem");
            var rootInfo = Probe(fullRoot);
            if (rootInfo is not { Directory: true } || rootInfo.Path != fullRoot) return Refused("noncanonical-root");
            if (!Contained(fullRoot, fullCwd)) return Refused("outside-root");
            var checkedPaths = new Dictionary<string, Identity>(StringComparer.OrdinalIgnoreCase) { [fullRoot] = rootInfo };
            var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (fullCwd != fullRoot)
            {
                var cwdInfo = ResolvePath(fullRoot, fullCwd, checkedPaths, missing);
                if (cwdInfo is null || missing.Count != 0 || Probe(cwdInfo) is not { Directory: true }) return Refused("invalid-cwd");
            }
            var result = new Dictionary<string, EditClaimTarget>(StringComparer.Ordinal);
            foreach (var input in targets)
            {
                if (!SafeTarget(input)) return Refused("invalid-path");
                var absolute = Path.GetFullPath(input.Replace('/', '\\'), fullCwd);
                if (!Contained(fullRoot, absolute) || absolute.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)) return Refused("outside-root");
                var physical = ResolvePath(fullRoot, absolute, checkedPaths, missing);
                if (physical is null) return Refused("invalid-path");
                var last = Probe(physical);
                if (last?.Directory == true) return Refused("directory-target");
                var relative = Path.GetRelativePath(fullRoot, physical).Replace('\\', '/');
                if (!relative.Split('/').All(ValidName)) return Refused("unsupported-name");
                var key = relative.ToLowerInvariant(); // ASCII + positively known insensitive directories only
                result.TryAdd(key, new(physical, key));
            }
            // Detect replacement/retargeting before returning. This advisory check
            // cannot make a later external filesystem write atomic with this hook.
            foreach (var (path, identity) in checkedPaths)
                if (Probe(path) is not { } current || current.Volume != identity.Volume || current.Index != identity.Index || current.Path != identity.Path)
                    return Refused("path-changed");
            foreach (var path in missing)
                if (Probe(path) is not null) return Refused("path-changed");
            return new(true, "supported", result.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Value).ToArray());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return Refused("filesystem-unavailable"); }
    }

    private static string? ResolvePath(string root, string absolute, Dictionary<string, Identity> checkedPaths, HashSet<string> missing)
    {
        var current = root;
        var absent = false;
        var parts = Path.GetRelativePath(root, absolute).Split(Path.DirectorySeparatorChar);
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            if (!ValidName(part)) return null;
            current = Path.Combine(current, part);
            if (!absent && Probe(current) is { } info)
            {
                if (!Contained(root, info.Path)) return null;
                if (!info.Directory && index < parts.Length - 1) return null;
                checkedPaths[current] = info;
                current = info.Path;
                // A file cannot be an ancestor; subsequent metadata opens fail closed.
            }
            else { absent = true; missing.Add(current); }
        }
        return current;
    }

    private static bool SafeAbsolute(string path) => !string.IsNullOrWhiteSpace(path) && path.Length <= 4096 &&
        path.All(c => c is >= ' ' and < (char)127) && !path.StartsWith("\\\\", StringComparison.Ordinal) && Path.IsPathFullyQualified(path);
    private static bool SafeTarget(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.StartsWith("\\\\", StringComparison.Ordinal)) return false;
        var normalized = path.Replace('/', '\\');
        if (Path.IsPathRooted(normalized) && !Path.IsPathFullyQualified(normalized)) return false;
        var relative = Path.IsPathFullyQualified(normalized) ? normalized[Path.GetPathRoot(normalized)!.Length..] : normalized;
        return relative.Split('\\').All(ValidName);
    }
    private static bool ValidName(string name)
    {
        if (name.Length is < 1 or > 255 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ') ||
            name.Any(c => c is < ' ' or >= (char)127 || "<>:\"/\\|?*".Contains(c))) return false;
        var stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL") &&
            !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9');
    }
    private static bool Contained(string root, string path) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static EditClaimResolution Refused(string reason) => new(false, reason, []);
    private sealed record Identity(string Path, bool Directory, uint Volume, ulong Index);
    internal static bool SingleRegularLink(SafeFileHandle handle) => GetFileInformationByHandle(handle, out var info) &&
        info.Links == 1 && (info.Attributes & (0x400u | 0x10u)) == 0;

    private static Identity? Probe(string path)
    {
        using var handle = CreateFileW(path, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 2 or 3) return null;
            throw new IOException("Metadata unavailable");
        }
        if (!GetFileInformationByHandle(handle, out var info) || (info.Attributes & 0x400) != 0) throw new IOException("Unsupported file identity");
        var directory = (info.Attributes & 0x10) != 0;
        if (directory)
        {
            if (!GetFileInformationByHandleEx(handle, 23, out var flags, 4) || flags != 0) throw new IOException("Unsupported case semantics");
        }
        else if (info.Links != 1) throw new IOException("Unsupported hard link");
        var buffer = new StringBuilder(4096);
        var size = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (size == 0 || size >= buffer.Capacity || !buffer.ToString().StartsWith("\\\\?\\", StringComparison.Ordinal)) throw new IOException("Canonical name unavailable");
        var canonical = Path.TrimEndingDirectorySeparator(buffer.ToString()[4..]);
        if (!SafeAbsolute(canonical)) throw new IOException("Unsupported name");
        return new(canonical, directory, info.Volume, ((ulong)info.IndexHigh << 32) | info.IndexLow);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfo
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfo info);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out uint info, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
}
