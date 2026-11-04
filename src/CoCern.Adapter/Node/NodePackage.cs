using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using CoCern.Adapter.Development;
using CoCern.Contracts;

namespace CoCern.Adapter.Node;

public sealed record StagedNode(NodeRelease Release, NodeImage Image, string Directory);
public sealed record NodeHostCapabilities(string? DotnetPath, NodeFramework[] InstalledFrameworks, string WindowsRid);
public sealed record NodeLaunchSpec(string FileName, string[] Arguments);

public static class NodePackage
{
    /// <summary>Imports only the explicitly supplied installed package; it neither selects nor executes an adapter.</summary>
    public static async Task<NodeImage> ImportBootstrapAsync(string bootstrapAdapterDll, string updateRoot, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string? root = null;
        string? scratch = null;
        try
        {
            var dll = Canonical(bootstrapAdapterDll);
            if (!Path.GetFileName(dll).Equals("CoCern.Adapter.dll", StringComparison.OrdinalIgnoreCase) || !File.Exists(dll)) throw new InvalidDataException();
            var source = Path.GetDirectoryName(dll)!;
            root = UpdateRoot(updateRoot);
            if (root.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
            var files = BootstrapFiles(source, ct);
            var metadata = NodePackageMetadataReader.Read(source);
            if (metadata is null || !ValidMetadata(metadata)) throw new InvalidDataException();
            var image = new NodeImage(AdapterDeployment.ContentHash(source), metadata, DllHash(source), null);
            PrivateDirectory(root);
            scratch = Path.Combine(root, ".staging-" + Guid.NewGuid().ToString("N"));
            PrivateDirectory(scratch, requireNew: true);
            var content = Path.Combine(scratch, "content");
            PrivateDirectory(content, requireNew: true);
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                RejectLinks(file);
                RejectHardLink(file);
                var target = Contained(content, Path.GetRelativePath(source, file));
                PrivateParents(content, Path.GetDirectoryName(target)!);
                await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
                await using var output = PrivateFile(target);
                var length = input.Length;
                if (length > FileLimit || await CopyBoundedAsync(input, output, length, null, ct) != length) throw new InvalidDataException();
                output.Flush(flushToDisk: true);
            }
            _ = BootstrapFiles(source, ct);
            if (AdapterDeployment.ContentHash(source) != image.ContentId || !ValidateImage(content, image, ct)) throw new InvalidDataException();
            var versions = Path.Combine(root, "versions");
            PrivateDirectory(versions);
            var final = Contained(versions, image.ContentId);
            if (!Directory.Exists(final))
            {
                ct.ThrowIfCancellationRequested();
                try { Directory.Move(content, final); }
                catch (IOException) when (Directory.Exists(final)) { }
            }
            if (!ValidateImage(final, image, ct)) throw new InvalidDataException();
            ct.ThrowIfCancellationRequested();
            return image;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception) when (Unavailable(exception)) { ct.ThrowIfCancellationRequested(); throw new InvalidDataException("node-bootstrap-invalid"); }
        finally { if (root is not null && scratch is not null) RemoveScratch(root, scratch); }
    }

    private static string[] BootstrapFiles(string source, CancellationToken ct)
    {
        var files = new List<string>();
        var count = 0;
        long total = 0;
        void Visit(string directory)
        {
            ct.ThrowIfCancellationRequested();
            RejectLinks(directory);
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if (++count > EntryLimit) throw new InvalidDataException();
                SafeName(Path.GetRelativePath(source, entry.FullName).Replace('\\', '/'));
                RejectLinks(entry.FullName);
                if (entry is DirectoryInfo child) Visit(child.FullName);
                else
                {
                    RejectHardLink(entry.FullName);
                    var length = ((FileInfo)entry).Length;
                    total = checked(total + length);
                    if (length > FileLimit || total > InflatedLimit) throw new InvalidDataException();
                    files.Add(entry.FullName);
                }
            }
        }
        Visit(source);
        return files.Order(StringComparer.Ordinal).ToArray();
    }

    internal static string ValidatedUpdateRoot(string updateRoot, bool create)
    {
        var root = UpdateRoot(updateRoot);
        if (File.Exists(root)) throw new InvalidDataException();
        if (create) PrivateDirectory(root);
        else if (Directory.Exists(root)) ValidatePrivacy(new DirectoryInfo(root));
        return root;
    }

    internal static bool ValidateStoredImage(string updateRoot, NodeImage? image)
    {
        if (image is null || !LowerHash(image.ContentId) || !UpperHash(image.AdapterDllSha256) || image.ReleaseId is not null && !UpperHash(image.ReleaseId) || !ValidMetadata(image.Package)) return false;
        var versions = Path.Combine(updateRoot, "versions");
        ValidatePrivacy(new DirectoryInfo(versions));
        return ValidateImage(Contained(versions, image.ContentId), image, default);
    }

    internal static void ValidatePrivateFile(string path)
    {
        RejectLinks(path);
        RejectHardLink(path);
        ValidatePrivacy(new FileInfo(path));
    }

    internal static FileStream CreatePrivateFile(string path) => PrivateFile(path);

    private const long ZipLimit = 256L * 1024 * 1024;
    private const long FileLimit = 128L * 1024 * 1024;
    private const long InflatedLimit = 768L * 1024 * 1024;
    private const int EntryLimit = 8192;

    /// <summary>
    /// The caller supplies its existing enrolled credential and a client bound to enrolledHub with
    /// PinnedOriginHandler around NodeTransport's pinned, no-redirect handler. HttpClient cannot expose
    /// or repair an unsafe handler after it has forwarded a credential. This method never owns that client.
    /// </summary>
    public static async Task<StagedNode?> StageAsync(NodeRelease release, Uri enrolledHub, HttpClient pinnedClient, string machineCredential, string updateRoot, NodeHostCapabilities host, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string? scratch = null;
        string? root = null;
        try
        {
            if (!ValidRelease(release) || !Compatible(release.Package, host) || !ValidHub(enrolledHub) || string.IsNullOrWhiteSpace(machineCredential) || machineCredential.Length > 16384 || machineCredential.Any(char.IsControl)) return Rejected();
            root = UpdateRoot(updateRoot);
            PrivateDirectory(root);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(enrolledHub, "/join/adapter.zip?release=" + release.ReleaseId));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", machineCredential);
            using var response = await pinnedClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength is { } length && length != release.ZipBytes) return Rejected();

            scratch = Path.Combine(root, ".staging-" + Guid.NewGuid().ToString("N"));
            PrivateDirectory(scratch, requireNew: true);
            var zipPath = Path.Combine(scratch, "download.zip");
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var destination = PrivateFile(zipPath);
                var bytes = await CopyBoundedAsync(input, destination, release.ZipBytes, hash, ct);
                if (bytes != release.ZipBytes || Convert.ToHexString(hash.GetHashAndReset()) != release.ZipSha256) throw new InvalidDataException();
                destination.Flush(flushToDisk: true);
            }

            // No archive entry is opened or extracted before the complete transfer's length and SHA-256 agree.
            var content = Path.Combine(scratch, "content");
            PrivateDirectory(content, requireNew: true);
            using (var zip = File.OpenRead(zipPath))
            {
                ValidateHeaders(zip);
                using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
                ValidateEntries(archive);
                long inflated = 0;
                foreach (var entry in archive.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var directory = entry.FullName.EndsWith('/');
                    var path = Contained(content, entry.FullName.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar));
                    PrivateParents(content, directory ? path : Path.GetDirectoryName(path)!);
                    if (directory) continue;
                    await using var input = entry.Open();
                    await using var destination = PrivateFile(path);
                    var count = await CopyBoundedAsync(input, destination, Math.Min(FileLimit, InflatedLimit - inflated), null, ct);
                    inflated = checked(inflated + count);
                    if (count != entry.Length || inflated > InflatedLimit) throw new InvalidDataException();
                    destination.Flush(flushToDisk: true);
                }
            }
            ct.ThrowIfCancellationRequested();
            var metadata = NodePackageMetadataReader.Read(content);
            if (!SameMetadata(metadata, release.Package) || DllHash(content) != release.AdapterDllSha256) return Rejected();
            var id = AdapterDeployment.ContentHash(content);
            ct.ThrowIfCancellationRequested();
            var image = new NodeImage(id, release.Package, release.AdapterDllSha256, release.ReleaseId);
            var versions = Path.Combine(root, "versions");
            PrivateDirectory(versions);
            var final = Contained(versions, id);
            if (Directory.Exists(final))
            {
                if (!ValidateImage(final, image, ct)) return Rejected();
            }
            else
            {
                RejectLinks(final);
                ct.ThrowIfCancellationRequested();
                try { Directory.Move(content, final); }
                catch (IOException) when (Directory.Exists(final))
                {
                    if (!ValidateImage(final, image, ct)) return Rejected();
                }
            }
            ct.ThrowIfCancellationRequested();
            return new(release, image, final);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception) when (Unavailable(exception)) { ct.ThrowIfCancellationRequested(); return Rejected(); }
        finally
        {
            if (scratch is not null && root is not null) RemoveScratch(root, scratch);
        }
    }

    /// <summary>Returns an ArgumentList specification only; candidate code is never loaded or started here.</summary>
    public static NodeLaunchSpec? Launch(NodeImage image, string nodeFolder, Guid? activationNonce, NodeHostCapabilities host)
    {
        try
        {
            if (!LowerHash(image.ContentId) || !UpperHash(image.AdapterDllSha256) || image.ReleaseId is not null && !UpperHash(image.ReleaseId) || !Compatible(image.Package, host)) return null;
            var root = UpdateRoot(Path.Combine(Canonical(nodeFolder), "updates"));
            var versions = Path.Combine(root, "versions");
            ValidatePrivacy(new DirectoryInfo(root));
            ValidatePrivacy(new DirectoryInfo(versions));
            var path = Contained(versions, image.ContentId);
            if (!ValidateImage(path, image, default)) return null;
            var arguments = new List<string>();
            var file = image.Package.SelfContained ? Path.Combine(path, "CoCern.Adapter.exe") : host.DotnetPath!;
            if (!image.Package.SelfContained) arguments.Add(Path.Combine(path, "CoCern.Adapter.dll"));
            arguments.AddRange(["--node-worker", "--data-dir", Canonical(nodeFolder)]);
            if (activationNonce is { } nonce) arguments.AddRange(["--activation-nonce", nonce.ToString("D")]);
            return new(file, arguments.ToArray());
        }
        catch (Exception exception) when (Unavailable(exception)) { return null; }
    }

    /// <summary>Only an explicitly known existing dotnet host may be queried, never the candidate apphost.</summary>
    public static NodeHostCapabilities ReadHostCapabilities(string? dotnetPath)
    {
        var rid = OperatingSystem.IsWindows() ? RuntimeInformation.ProcessArchitecture switch { Architecture.X64 => "win-x64", Architecture.Arm64 => "win-arm64", _ => "" } : "";
        if (dotnetPath is null) return new(null, [], rid);
        try
        {
            if (!KnownDotnet(dotnetPath)) return new(null, [], rid);
            var start = new ProcessStartInfo(Canonical(dotnetPath)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--list-runtimes");
            using var process = Process.Start(start);
            if (process is null) return new(null, [], rid);
            var stdout = ReadInventoryAsync(process.StandardOutput);
            var stderr = ReadInventoryAsync(process.StandardError);
            if (!process.WaitForExit(10000)) { process.Kill(entireProcessTree: true); process.WaitForExit(); return new(null, [], rid); }
            var text = stdout.GetAwaiter().GetResult();
            _ = stderr.GetAwaiter().GetResult();
            if (process.ExitCode != 0) return new(null, [], rid);
            var frameworks = new List<NodeFramework>();
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3 || parts[0].Length > 128 || !Version.TryParse(parts[1], out var version) || version.Build < 0 || version.Revision != -1 || !parts[2].StartsWith('[') || !parts[2].EndsWith(']')) continue;
                frameworks.Add(new(parts[0], parts[1]));
            }
            return new(Canonical(dotnetPath), frameworks.Distinct().OrderBy(f => f.Name, StringComparer.Ordinal).ThenBy(f => f.Version, StringComparer.Ordinal).ToArray(), rid);
        }
        catch (Exception exception) when (Unavailable(exception) || exception is Win32Exception) { return new(null, [], rid); }
    }

    private static async Task<string> ReadInventoryAsync(StreamReader reader)
    {
        var result = new StringBuilder();
        var buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer)) != 0)
        {
            if (result.Length + count > 65536) throw new InvalidDataException();
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }

    private static bool ValidRelease(NodeRelease release) => release is not null && release.Schema == 1 && UpperHash(release.ReleaseId) && release.ReleaseId == release.ZipSha256 && UpperHash(release.AdapterDllSha256) && release.ZipBytes is > 0 and <= ZipLimit && ValidMetadata(release.Package) && JsonSerializer.SerializeToUtf8Bytes(release).Length <= 65536;

    private static bool ValidMetadata(NodePackageMetadata package)
    {
        if (package is null || package.Schema != 1 || package.SupervisorProtocol != 1 || package.ProductVersion is not { Length: > 0 and <= 256 } || package.ProductVersion.Any(char.IsControl) || package.TargetFramework is not { Length: > 3 and <= 32 } || !package.TargetFramework.StartsWith("net", StringComparison.Ordinal) || !Version.TryParse(package.TargetFramework[3..], out var target) || target.Major < 5 || target.Build != -1 || target.Revision != -1 || package.Rid is not ("any" or "win-x64" or "win-arm64") || package.SelfContained && package.Rid == "any" || package.Frameworks is not { Length: > 0 and <= 16 }) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var framework in package.Frameworks)
        {
            if (framework is null || framework.Name is not { Length: > 0 and <= 128 } || framework.Name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) || framework.Version is not { Length: > 0 and <= 32 } || !Version.TryParse(framework.Version, out var version) || version.Major != target.Major || version.Minor != target.Minor || version.Build < 0 || version.Revision != -1 || !names.Add(framework.Name)) return false;
        }
        return names.Contains("Microsoft.NETCore.App");
    }

    private static bool Compatible(NodePackageMetadata package, NodeHostCapabilities host)
    {
        if (!ValidMetadata(package) || host is null || host.WindowsRid is not ("win-x64" or "win-arm64") || package.Rid != "any" && package.Rid != host.WindowsRid) return false;
        if (package.SelfContained) return true;
        if (!KnownDotnet(host.DotnetPath) || host.InstalledFrameworks is null) return false;
        return package.Frameworks.All(required => host.InstalledFrameworks.Any(installed => installed is not null && installed.Name == required.Name && Version.TryParse(installed.Version, out var actual) && Version.TryParse(required.Version, out var minimum) && actual.Major == minimum.Major && actual.Minor == minimum.Minor && actual.Build >= minimum.Build && actual.Revision == -1));
    }

    private static bool KnownDotnet(string? path) => path is not null && Path.IsPathFullyQualified(path) && Path.GetFileName(path).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path);
    private static bool UpperHash(string? value) => value is { Length: 64 } && value.All(c => char.IsAsciiDigit(c) || c is >= 'A' and <= 'F');
    private static bool LowerHash(string? value) => value is { Length: 64 } && value.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');
    private static bool ValidHub(Uri hub) => hub is { IsAbsoluteUri: true } && hub.UserInfo.Length == 0 && hub.Query.Length == 0 && hub.Fragment.Length == 0 && (hub.Scheme == Uri.UriSchemeHttps || hub.Scheme == Uri.UriSchemeHttp && hub.IsLoopback);
    private static bool SameMetadata(NodePackageMetadata? left, NodePackageMetadata right) => left is not null && ValidMetadata(right) && left.Schema == right.Schema && left.ProductVersion == right.ProductVersion && left.TargetFramework == right.TargetFramework && left.Rid == right.Rid && left.SelfContained == right.SelfContained && left.SupervisorProtocol == right.SupervisorProtocol && left.Frameworks.OrderBy(f => f.Name, StringComparer.Ordinal).ThenBy(f => f.Version, StringComparer.Ordinal).Select(f => (f.Name, f.Version)).SequenceEqual(right.Frameworks.OrderBy(f => f.Name, StringComparer.Ordinal).ThenBy(f => f.Version, StringComparer.Ordinal).Select(f => (f.Name, f.Version)));
    private static string DllHash(string folder) { using var stream = File.OpenRead(Path.Combine(folder, "CoCern.Adapter.dll")); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static StagedNode? Rejected() { Console.Error.WriteLine("node-update:package-unavailable"); return null; }
    private static bool Unavailable(Exception exception) => exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException or OverflowException or HttpRequestException or OperationCanceledException or JsonException or System.Security.SecurityException;

    private static async Task<long> CopyBoundedAsync(Stream source, Stream destination, long limit, IncrementalHash? hash, CancellationToken ct)
    {
        long total = 0;
        var buffer = new byte[81920];
        int count;
        while ((count = await source.ReadAsync(buffer, ct)) != 0)
        {
            total = checked(total + count);
            if (total > limit) throw new InvalidDataException();
            hash?.AppendData(buffer.AsSpan(0, count));
            await destination.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        ct.ThrowIfCancellationRequested();
        return total;
    }

    private static string Canonical(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException();
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!string.Equals(full, Path.TrimEndingDirectorySeparator(path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
        SafeName(full[Path.GetPathRoot(full)!.Length..].Replace(Path.DirectorySeparatorChar, '/'));
        RejectLinks(full);
        return full;
    }

    private static string UpdateRoot(string path)
    {
        var full = Canonical(path);
        if (!Path.GetFileName(full).Equals("updates", StringComparison.OrdinalIgnoreCase) || Path.GetDirectoryName(full) is not { } node || !Directory.Exists(node)) throw new InvalidDataException();
        return full;
    }

    private static string Contained(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
        RejectLinks(path);
        return path;
    }

    private static void RejectLinks(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
            }
            else if (new FileInfo(current).LinkTarget is not null || new DirectoryInfo(current).LinkTarget is not null) throw new InvalidDataException();
        }
    }

    private static void PrivateDirectory(string path, bool requireNew = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        RejectLinks(path);
        if (Directory.Exists(path))
        {
            if (requireNew) throw new IOException();
        }
        else
        {
            var security = new DirectorySecurity();
            var user = WindowsIdentity.GetCurrent().User!;
            security.SetOwner(user);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).Create(security);
        }
        ValidatePrivacy(new DirectoryInfo(path));
    }

    private static void ValidatePrivacy(FileSystemInfo info)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var user = WindowsIdentity.GetCurrent().User!;
        var security = info is DirectoryInfo directory ? (FileSystemSecurity)directory.GetAccessControl() : ((FileInfo)info).GetAccessControl();
        if (!user.Equals(security.GetOwner(typeof(SecurityIdentifier)))) throw new UnauthorizedAccessException();
        var fullControl = false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (!user.Equals(rule.IdentityReference)) throw new UnauthorizedAccessException();
            fullControl |= (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl;
        }
        if (!fullControl) throw new UnauthorizedAccessException();
    }

    private static FileStream PrivateFile(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var user = WindowsIdentity.GetCurrent().User!;
        var security = new FileSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 81920, FileOptions.Asynchronous, security);
    }

    private static void PrivateParents(string root, string destination)
    {
        var relative = Path.GetRelativePath(root, destination);
        if (relative == ".") return;
        var current = root;
        foreach (var component in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Contained(current, component);
            PrivateDirectory(current);
        }
    }

    private static bool ValidateImage(string folder, NodeImage image, CancellationToken ct)
    {
        RejectLinks(folder);
        ValidatePrivacy(new DirectoryInfo(folder));
        long total = 0;
        var count = 0;
        void Visit(string directory)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if (++count > EntryLimit) throw new InvalidDataException();
                SafeName(Path.GetRelativePath(folder, entry.FullName).Replace('\\', '/'));
                RejectLinks(entry.FullName);
                ValidatePrivacy(entry);
                if (entry is DirectoryInfo child) Visit(child.FullName);
                else
                {
                    RejectHardLink(entry.FullName);
                    var length = ((FileInfo)entry).Length;
                    total = checked(total + length);
                    if (length > FileLimit || total > InflatedLimit) throw new InvalidDataException();
                }
            }
        }
        Visit(folder);
        return AdapterDeployment.ContentHash(folder) == image.ContentId && DllHash(folder) == image.AdapterDllSha256 && SameMetadata(NodePackageMetadataReader.Read(folder), image.Package);
    }

    private static void RejectHardLink(string file)
    {
        using var handle = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!GetFileInformationByHandle(handle, out var information) || information.NumberOfLinks != 1) throw new InvalidDataException();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, out FileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    private static void RemoveScratch(string root, string scratch)
    {
        try
        {
            if (Path.GetDirectoryName(scratch) != root || !Path.GetFileName(scratch).StartsWith(".staging-", StringComparison.Ordinal)) return;
            _ = Contained(root, Path.GetFileName(scratch));
            void Remove(string directory)
            {
                RejectLinks(directory);
                ValidatePrivacy(new DirectoryInfo(directory));
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    RejectLinks(entry.FullName);
                    if (entry is DirectoryInfo child) Remove(child.FullName); else entry.Delete();
                }
                Directory.Delete(directory);
            }
            if (Directory.Exists(scratch)) Remove(scratch);
        }
        catch (Exception exception) when (Unavailable(exception)) { Console.Error.WriteLine("node-update:staging-retained"); }
    }

    private static void SafeName(string name)
    {
        if (name.Length is 0 or > 1024 || name.Contains('\\') || name.StartsWith('/') || name.Any(c => c < 32 || c is ':' or '<' or '>' or '"' or '|' or '?' or '*')) throw new InvalidDataException();
        foreach (var component in name.Split('/'))
        {
            if (component.Length == 0 || component is "." or ".." || component.EndsWith('.') || component.EndsWith(' ')) throw new InvalidDataException();
            var device = component.Split('.')[0].TrimEnd(' ', '.').ToUpperInvariant();
            if (device is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" or "CONIN$" or "CONOUT$" || device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(device[3])) throw new InvalidDataException();
        }
    }

    private static void ValidateEntries(ZipArchive archive)
    {
        if (archive.Entries.Count is 0 or > EntryLimit) throw new InvalidDataException();
        var names = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var prefixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var directory = entry.FullName.EndsWith('/');
            var name = directory ? entry.FullName[..^1] : entry.FullName;
            SafeName(name);
            if (!names.TryAdd(name, directory) || entry.Length < 0 || entry.Length > FileLimit || directory && entry.Length != 0) throw new InvalidDataException();
            var components = name.Split('/');
            for (var i = 1; i <= components.Length; i++)
            {
                var prefix = string.Join('/', components.Take(i));
                if (prefixes.TryGetValue(prefix, out var prior) && !string.Equals(prefix, prior, StringComparison.Ordinal)) throw new InvalidDataException();
                prefixes[prefix] = prefix;
            }
            total = checked(total + entry.Length);
            if (total > InflatedLimit) throw new InvalidDataException();
        }
        foreach (var name in names.Keys)
        {
            for (var slash = name.IndexOf('/'); slash >= 0; slash = name.IndexOf('/', slash + 1))
                if (names.TryGetValue(name[..slash], out var directory) && !directory) throw new InvalidDataException();
        }
    }

    // This preflight only refuses unsupported structures. ZipArchive remains the decompressor and CRC owner.
    private static void ValidateHeaders(Stream zip)
    {
        var tailOffset = Math.Max(0, zip.Length - 65557);
        var tail = ReadAt(zip, tailOffset, checked((int)(zip.Length - tailOffset)));
        var end = -1;
        for (var i = tail.Length - 22; i >= 0; i--)
            if (U32(tail, i) == 0x06054b50 && i + 22 + U16(tail, i + 20) == tail.Length) { end = i; break; }
        if (end < 0 || U16(tail, end + 4) != 0 || U16(tail, end + 6) != 0 || U16(tail, end + 8) != U16(tail, end + 10)) throw new InvalidDataException();
        var entries = U16(tail, end + 10);
        long central = U32(tail, end + 16);
        var centralEnd = checked(central + U32(tail, end + 12));
        if (entries is 0 or > EntryLimit || centralEnd != tailOffset + end) throw new InvalidDataException();
        var ranges = new List<(long Start, long End, uint Crc, uint Compressed, uint Inflated, bool Descriptor)>();
        var position = central;
        for (var i = 0; i < entries; i++)
        {
            var header = ReadAt(zip, position, 46);
            var flags = U16(header, 8);
            var method = U16(header, 10);
            var compressed = U32(header, 20);
            var inflated = U32(header, 24);
            var localOffset = U32(header, 42);
            if (U32(header, 0) != 0x02014b50 || U16(header, 6) > 20 || (flags & ~0x0808) != 0 || method is not (0 or 8) || U16(header, 34) != 0 || compressed == uint.MaxValue || inflated == uint.MaxValue || localOffset == uint.MaxValue) throw new InvalidDataException();
            var attributes = U32(header, 38);
            var mode = attributes >> 16 & 0xF000;
            if ((attributes & 0x400) != 0 || mode is not (0 or 0x8000 or 0x4000)) throw new InvalidDataException();
            var nameLength = U16(header, 28);
            var name = ReadAt(zip, position + 46, nameLength);
            ValidateExtra(ReadAt(zip, position + 46 + nameLength, U16(header, 30)));
            position = checked(position + 46 + nameLength + U16(header, 30) + U16(header, 32));
            var local = ReadAt(zip, localOffset, 30);
            var descriptor = (flags & 8) != 0;
            if (U32(local, 0) != 0x04034b50 || U16(local, 4) > 20 || U16(local, 6) != flags || U16(local, 8) != method || U16(local, 26) != nameLength || !ReadAt(zip, localOffset + 30L, nameLength).AsSpan().SequenceEqual(name)) throw new InvalidDataException();
            if (!descriptor && (U32(local, 14) != U32(header, 16) || U32(local, 18) != compressed || U32(local, 22) != inflated)) throw new InvalidDataException();
            ValidateExtra(ReadAt(zip, localOffset + 30L + nameLength, U16(local, 28)));
            var dataEnd = checked(localOffset + 30L + nameLength + U16(local, 28) + compressed);
            if (dataEnd > central) throw new InvalidDataException();
            ranges.Add((localOffset, dataEnd, U32(header, 16), compressed, inflated, descriptor));
        }
        if (position != centralEnd) throw new InvalidDataException();
        var ordered = ranges.OrderBy(range => range.Start).ToArray();
        if (ordered[0].Start != 0) throw new InvalidDataException();
        for (var i = 0; i < ordered.Length; i++)
        {
            var item = ordered[i];
            var next = i + 1 < ordered.Length ? ordered[i + 1].Start : central;
            if (!item.Descriptor) { if (item.End != next) throw new InvalidDataException(); continue; }
            var size = checked((int)(next - item.End));
            if (size is not (12 or 16)) throw new InvalidDataException();
            var descriptor = ReadAt(zip, item.End, size);
            var start = size == 16 ? 4 : 0;
            if (size == 16 && U32(descriptor, 0) != 0x08074b50 || U32(descriptor, start) != item.Crc || U32(descriptor, start + 4) != item.Compressed || U32(descriptor, start + 8) != item.Inflated) throw new InvalidDataException();
        }
    }

    private static void ValidateExtra(byte[] bytes)
    {
        var position = 0;
        while (position < bytes.Length)
        {
            if (bytes.Length - position < 4) throw new InvalidDataException();
            var id = U16(bytes, position);
            var length = U16(bytes, position + 2);
            position += 4;
            if (length > bytes.Length - position || id is not (0x5455 or 0x000a)) throw new InvalidDataException();
            if (id == 0x000a && (length != 32 || U16(bytes, position + 4) != 1 || U16(bytes, position + 6) != 24)) throw new InvalidDataException();
            position += length;
        }
    }

    private static byte[] ReadAt(Stream stream, long offset, int count)
    {
        if (offset < 0 || count < 0 || checked(offset + count) > stream.Length) throw new InvalidDataException();
        stream.Position = offset;
        var bytes = new byte[count];
        stream.ReadExactly(bytes);
        return bytes;
    }
    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
}
