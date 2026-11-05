using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CoCern.Adapter.Development;

public sealed record AdapterManifest(string Version, string Generation);

/// <summary>Immutable development copies, selected by an atomic manifest outside build output.</summary>
public static class AdapterDeployment
{
    /// <summary>Validates the tree and returns the same deterministic content identity used by local deployment, without publishing it.</summary>
    public static string ContentHash(string folder)
    {
        var root = Path.GetFullPath(folder);
        return Hash(root, Files(root));
    }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] Required = ["CoCern.Adapter.dll", "CoCern.Adapter.deps.json", "CoCern.Adapter.runtimeconfig.json"];

    public static AdapterManifest Publish(string sourceDirectory, string deploymentRoot)
    {
        var source = Path.GetFullPath(sourceDirectory);
        var root = Path.GetFullPath(deploymentRoot);
        if (Path.GetRelativePath(source, root) is var relative && !relative.StartsWith(".." + Path.DirectorySeparatorChar) && relative != ".." && !Path.IsPathRooted(relative))
            throw new IOException("The deployment folder must be outside the adapter build folder.");
        CheckRequired(source);
        Directory.CreateDirectory(root);
        RejectLink(root);
        using var publishing = Lock(root);
        var files = Files(source);
        var version = Hash(source, files);
        var versions = Path.Combine(root, "versions");
        Directory.CreateDirectory(versions);
        RejectLink(versions);
        var destination = Path.Combine(versions, version);
        if (Directory.Exists(destination))
        {
            RejectLink(destination);
            if (Hash(destination, Files(destination)) != version) throw new IOException("An immutable adapter version was modified.");
        }
        else
        {
            var staging = Path.Combine(versions, ".staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                foreach (var file in files)
                {
                    var target = Path.Combine(staging, file);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(Path.Combine(source, file), target);
                }
                if (Hash(staging, Files(staging)) != version) throw new IOException("Adapter output changed while it was being deployed. Build again.");
                TransientMove.Run(() => Directory.Move(staging, destination));
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
        }
        var manifest = new AdapterManifest(version, Guid.NewGuid().ToString("N"));
        var temporary = Path.Combine(root, "current-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(manifest, Json), new UTF8Encoding(false));
            var current = Path.Combine(root, "current.json");
            if (File.Exists(current)) File.Replace(temporary, current, null); else File.Move(temporary, current);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return manifest;
    }

    public static AdapterManifest Read(string deploymentRoot)
    {
        AdapterManifest manifest;
        try
        {
            using var reader = new StreamReader(OpenCurrentManifest(deploymentRoot));
            manifest = JsonSerializer.Deserialize<AdapterManifest>(reader.ReadToEnd(), Json) ?? throw new IOException("Missing adapter manifest.");
        }
        catch (JsonException exception) { throw new IOException("Invalid adapter manifest.", exception); }
        if (!Guid.TryParseExact(manifest.Generation, "N", out _)) throw new IOException("Invalid adapter generation.");
        var path = AdapterPath(deploymentRoot, manifest);
        RejectLink(Path.GetFullPath(deploymentRoot));
        RejectLink(Path.Combine(deploymentRoot, "versions"));
        RejectLink(Path.GetDirectoryName(path)!);
        CheckRequired(Path.GetDirectoryName(path)!);
        return manifest;
    }

    // Keep this reader's snapshot open without blocking publication of the next atomic manifest.
    internal static FileStream OpenCurrentManifest(string deploymentRoot) =>
        new(Path.Combine(deploymentRoot, "current.json"), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

    public static string AdapterPath(string deploymentRoot, AdapterManifest manifest)
    {
        if (manifest.Version is not { Length: 64 } version || version.Any(c => !char.IsAsciiHexDigit(c))) throw new IOException("Invalid adapter version.");
        return Path.Combine(Path.GetFullPath(deploymentRoot), "versions", version, "CoCern.Adapter.dll");
    }

    private static void CheckRequired(string directory)
    {
        foreach (var name in Required)
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path)) throw new FileNotFoundException("Build the adapter before starting the development deployment.", path);
            RejectLink(path);
        }
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Adapter deployment does not follow symbolic links or junctions.");
    }

    private static string[] Files(string root)
    {
        var result = new List<string>();
        void Visit(string folder)
        {
            RejectLink(folder);
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
            {
                RejectLink(entry);
                if (Directory.Exists(entry)) Visit(entry); else result.Add(Path.GetRelativePath(root, entry));
            }
        }
        Visit(root);
        return result.Order(StringComparer.Ordinal).ToArray();
    }

    private static string Hash(string root, string[] files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        foreach (var file in files)
        {
            var name = Encoding.UTF8.GetBytes(file.Replace('\\', '/'));
            hash.AppendData(BitConverter.GetBytes(name.Length)); hash.AppendData(name);
            using var stream = File.OpenRead(Path.Combine(root, file));
            hash.AppendData(BitConverter.GetBytes(stream.Length));
            int count;
            while ((count = stream.Read(buffer)) > 0) hash.AppendData(buffer.AsSpan(0, count));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static FileStream Lock(string root)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try { return new FileStream(Path.Combine(root, "publish.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < until) { Thread.Sleep(25); }
        }
    }
}
