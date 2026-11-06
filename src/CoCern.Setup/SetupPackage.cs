using System.Security.Cryptography;
using System.Text.Json;

namespace CoCern.Setup;

internal sealed record PackageFile(string Path, string Sha256);
internal sealed record PackageManifest(int Schema, string Rid, bool SelfContained, PackageFile[] Files);
internal sealed record InstalledPackage(string Kind, string Package, DateTimeOffset InstalledAt);

/// <summary>Validated immutable package copies; persistent database, enrollment and credentials are never replaced.</summary>
internal static class SetupPackage
{
    internal const string ManifestName = "cocern-package.json";
    internal const string ReceiptName = "installation.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static string Contained(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathFullyQualified(relative) || relative.Contains(':')) throw new IOException("Invalid package path.");
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new IOException("Package path escaped its installation.");
        for (FileSystemInfo? item = File.Exists(path) ? new FileInfo(path) : new DirectoryInfo(path); item is not null; item = item is FileInfo file ? file.Directory : ((DirectoryInfo)item).Parent)
            if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked package paths are unsupported.");
        return path;
    }

    internal static PackageManifest Validate(string folder)
    {
        var path = Contained(folder, ManifestName);
        if (!File.Exists(path) || new FileInfo(path).Length > 2 * 1024 * 1024) throw new IOException("CoCern package manifest is missing or too large.");
        var manifest = JsonSerializer.Deserialize<PackageManifest>(File.ReadAllBytes(path), Json) ?? throw new IOException("Invalid CoCern package.");
        if (manifest.Schema != 1 || !manifest.SelfContained || manifest.Rid is not ("win-x64" or "win-arm64") || manifest.Files is not { Length: > 0 and < 20000 }) throw new IOException("Unsupported CoCern package.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (file is null) throw new IOException("Invalid package file.");
            var source = Contained(folder, file.Path);
            if (!names.Add(Path.GetRelativePath(Path.GetFullPath(folder), source))) throw new IOException("Duplicate package file.");
            if (!File.Exists(source) || file.Sha256 is not { Length: 64 }) throw new IOException("CoCern package integrity check failed. Download or extract the package again.");
            using var content = File.OpenRead(source);
            if (!Convert.ToHexString(SHA256.HashData(content)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("CoCern package integrity check failed. Download or extract the package again.");
        }
        foreach (var entry in new[] { "CoCern.Host.exe", "CoCern.Adapter.exe", "CoCern.Setup.exe" })
            if (!names.Contains(entry.Replace('/', Path.DirectorySeparatorChar)) && !names.Contains(entry)) throw new IOException("CoCern package entry point is missing.");
        foreach (var config in new[] { "CoCern.Host.runtimeconfig.json", "CoCern.Adapter.runtimeconfig.json", "CoCern.Setup.runtimeconfig.json" })
        {
            var runtimePath = Contained(folder, config);
            if (new FileInfo(runtimePath).Length > 65536) throw new IOException("Invalid runtime metadata.");
            using var runtime = JsonDocument.Parse(File.ReadAllBytes(runtimePath));
            if (!runtime.RootElement.TryGetProperty("runtimeOptions", out var options) || options.TryGetProperty("framework", out _) || options.TryGetProperty("frameworks", out _) || !options.TryGetProperty("includedFrameworks", out _)) throw new IOException("This package is not self-contained.");
        }
        return manifest;
    }

    internal static string Install(string source, string root)
    {
        var manifest = Validate(source);
        root = Path.GetFullPath(root);
        var receipt = Contained(root, ReceiptName);
        if (File.Exists(receipt)) _ = Current(root);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Contained(source, ManifestName)))).ToLowerInvariant();
        var relative = "packages/" + hash;
        var destination = Contained(root, relative);
        if (Directory.Exists(destination))
        {
            try { Validate(destination); }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { relative += "-" + Guid.NewGuid().ToString("N"); destination = Contained(root, relative); }
        }
        if (!Directory.Exists(destination))
        {
            Directory.CreateDirectory(destination);
            foreach (var file in manifest.Files)
            {
                var target = Contained(destination, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Contained(source, file.Path), target, overwrite: false);
            }
            File.Copy(Contained(source, ManifestName), Contained(destination, ManifestName));
            Validate(destination);
        }
        Directory.CreateDirectory(root);
        var temporary = Contained(root, "installation-" + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(new InstalledPackage("cocern-installation-v1", relative, DateTimeOffset.UtcNow), Json));
        File.Move(temporary, receipt, overwrite: true);
        return destination;
    }

    internal static string Current(string root)
    {
        var receipt = Contained(root, ReceiptName);
        if (!File.Exists(receipt) || new FileInfo(receipt).Length > 16384) throw new IOException("CoCern has not been installed here. Choose Host or Join first.");
        var installation = JsonSerializer.Deserialize<InstalledPackage>(File.ReadAllBytes(receipt), Json);
        if (installation?.Kind != "cocern-installation-v1") throw new IOException("This installation is not owned by CoCern Setup.");
        return Contained(root, installation.Package);
    }
}
