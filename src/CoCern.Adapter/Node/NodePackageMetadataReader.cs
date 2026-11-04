using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using CoCern.Contracts;

namespace CoCern.Adapter.Node;

public static class NodePackageMetadataReader
{
    private const int JsonLimit = 64 * 1024;
    private const int DependenciesLimit = 4 * 1024 * 1024;
    private const int PeLimit = 128 * 1024 * 1024;

    /// <summary>Reads build output without loading or executing candidate assemblies.</summary>
    public static NodePackageMetadata? Read(string folder) => TryRead(name =>
    {
        var path = Path.Combine(folder, name);
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    });

    /// <summary>Publication reads the completed archive, so metadata and the leased ZIP describe the same immutable bytes.</summary>
    public static NodePackageMetadata? Read(ZipArchive archive) => TryRead(name =>
    {
        var entries = archive.Entries.Where(entry => string.Equals(entry.FullName, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        return entries.Length == 1 && entries[0].FullName == name ? entries[0].Open() : null;
    });

    private static NodePackageMetadata? TryRead(Func<string, Stream?> open)
    {
        try { return ReadCore(open); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or BadImageFormatException or JsonException or InvalidOperationException or ArgumentException or OverflowException or KeyNotFoundException)
        {
            return null;
        }
    }

    private static NodePackageMetadata? ReadCore(Func<string, Stream?> open)
    {
        using var dll = Bounded(open, "CoCern.Adapter.dll", PeLimit);
        using var pe = new PEReader(dll);
        if (!pe.HasMetadata || pe.PEHeaders.CorHeader is not { } cor || (cor.Flags & CorFlags.ILOnly) == 0) return null;
        var reader = pe.GetMetadataReader();
        if (!reader.IsAssembly || reader.GetString(reader.GetAssemblyDefinition().Name) != "CoCern.Adapter") return null;
        string? product = null, protocol = null, assemblyFramework = null;
        foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = reader.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
            var member = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (member.Parent.Kind != HandleKind.TypeReference) continue;
            var type = reader.GetTypeReference((TypeReferenceHandle)member.Parent);
            var name = reader.GetString(type.Namespace) + "." + reader.GetString(type.Name);
            if (name is not ("System.Reflection.AssemblyInformationalVersionAttribute" or "System.Reflection.AssemblyMetadataAttribute" or "System.Runtime.Versioning.TargetFrameworkAttribute")) continue;
            var blob = reader.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() != 1) return null;
            var value = blob.ReadSerializedString();
            if (name == "System.Reflection.AssemblyInformationalVersionAttribute")
            {
                if (product is not null) return null;
                product = value;
            }
            else if (name == "System.Runtime.Versioning.TargetFrameworkAttribute")
            {
                if (assemblyFramework is not null) return null;
                assemblyFramework = value;
            }
            else if (value == "CoCernNodeSupervisorProtocol")
            {
                if (protocol is not null) return null;
                protocol = blob.ReadSerializedString();
            }
        }
        if (product is not { Length: > 0 and <= 256 } || product.Any(char.IsControl) || protocol != "1") return null;
        var architecture = pe.PEHeaders.CoffHeader.Machine switch
        {
            Machine.I386 when (cor.Flags & (CorFlags.Requires32Bit | CorFlags.Prefers32Bit)) == 0 => "any",
            Machine.Amd64 => "win-x64",
            Machine.Arm64 => "win-arm64",
            _ => null
        };
        if (architecture is null) return null;

        using var runtimeBytes = Bounded(open, "CoCern.Adapter.runtimeconfig.json", JsonLimit);
        using var depsBytes = Bounded(open, "CoCern.Adapter.deps.json", DependenciesLimit);
        using var runtime = JsonDocument.Parse(runtimeBytes, new JsonDocumentOptions { MaxDepth = 16 });
        using var deps = JsonDocument.Parse(depsBytes, new JsonDocumentOptions { MaxDepth = 16 });
        if (!Unique(runtime.RootElement) || !Unique(deps.RootElement)) return null;
        var options = runtime.RootElement.GetProperty("runtimeOptions");
        if (options.ValueKind != JsonValueKind.Object) return null;
        // Only default runtime selection is supported by the updater's compatibility checks.
        if (new[] { "rollForward", "applyPatches", "rollForwardOnNoCandidateFx", "additionalProbingPaths", "additionalDeps", "startupHooks" }.Any(name => options.TryGetProperty(name, out _))) return null;
        var tfm = options.GetProperty("tfm").GetString();
        if (tfm is null || !tfm.StartsWith("net", StringComparison.Ordinal) || !Version.TryParse(tfm[3..], out var target) || target.Major < 5 || target.Build != -1 || target.Revision != -1) return null;
        var frameworkMoniker = $".NETCoreApp,Version=v{target.Major}.{target.Minor}";
        if (assemblyFramework is not null && assemblyFramework != frameworkMoniker) return null;
        var runtimeTarget = deps.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString();
        if (runtimeTarget is null) return null;
        var parts = runtimeTarget.Split('/');
        if (parts.Length is < 1 or > 2 || parts[0] != frameworkMoniker) return null;
        var rid = parts.Length == 1 ? "any" : parts[1];
        if (rid is not ("any" or "win-x64" or "win-arm64") || (parts.Length == 2 && rid == "any")) return null;
        if (rid == "any" ? architecture != "any" : architecture != "any" && architecture != rid) return null;

        var single = options.TryGetProperty("framework", out var framework);
        var multiple = options.TryGetProperty("frameworks", out var frameworks);
        var included = options.TryGetProperty("includedFrameworks", out var includedFrameworks);
        if ((single && multiple) || (included && (single || multiple)) || (!included && !single && !multiple)) return null;
        var declarations = single ? new[] { framework } : (included ? includedFrameworks : frameworks).EnumerateArray().ToArray();
        if (declarations.Length is < 1 or > 16) return null;
        var declared = new List<NodeFramework>();
        foreach (var declaration in declarations)
        {
            if (declaration.ValueKind != JsonValueKind.Object || declaration.EnumerateObject().Any(p => p.Name is not ("name" or "version"))) return null;
            var name = declaration.GetProperty("name").GetString();
            var version = declaration.GetProperty("version").GetString();
            if (name is not { Length: > 0 and <= 128 } || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) || version is null || !Version.TryParse(version, out var parsed) || parsed.Major != target.Major || parsed.Minor != target.Minor || parsed.Build < 0 || parsed.Revision != -1) return null;
            if (declared.Any(f => f.Name == name)) return null;
            declared.Add(new(name, version));
        }
        if (!declared.Any(f => f.Name == "Microsoft.NETCore.App")) return null;
        if (included)
        {
            if (rid == "any") return null;
            var machine = rid == "win-x64" ? Machine.Amd64 : Machine.Arm64;
            foreach (var file in new[] { "CoCern.Adapter.exe", "coreclr.dll", "hostfxr.dll" })
            {
                using var native = Bounded(open, file, PeLimit);
                using var nativePe = new PEReader(native);
                if (nativePe.PEHeaders.PEHeader is null || nativePe.PEHeaders.CoffHeader.Machine != machine) return null;
            }
        }
        return new(1, product, tfm, rid, included, declared.OrderBy(f => f.Name, StringComparer.Ordinal).ThenBy(f => f.Version, StringComparer.Ordinal).ToArray(), 1);
    }

    private static MemoryStream Bounded(Func<string, Stream?> open, string name, int maximum)
    {
        using var input = open(name) ?? throw new InvalidDataException("Missing package metadata.");
        var output = new MemoryStream();
        try
        {
            var buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer)) != 0)
            {
                if (output.Length + read > maximum) throw new InvalidDataException("Package metadata exceeds its bound.");
                output.Write(buffer, 0, read);
            }
            output.Position = 0;
            return output;
        }
        catch { output.Dispose(); throw; }
    }

    private static bool Unique(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == element.EnumerateObject().Count() && element.EnumerateObject().All(p => Unique(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().All(Unique),
        _ => true
    };
}
