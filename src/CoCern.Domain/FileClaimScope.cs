using System.Diagnostics.CodeAnalysis;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CoCern.Domain;

/// <summary>A file resource key derived only after its caller has verified the native checkout scope.</summary>
public sealed class FileClaimScope
{
    private static readonly UTF8Encoding Strict = new(false, true);
    private FileClaimScope(string resourceId, string relativePath) => (ResourceId, RelativePath) = (resourceId, relativePath);
    public string ResourceId { get; }
    public string RelativePath { get; }

    public static bool TryCreate(string repositoryId, string machineScope, string canonicalWorkspace, string relativePath,
        [NotNullWhen(true)] out FileClaimScope? scope)
    {
        scope = null;
        if (!Component(repositoryId, 512) || !Component(machineScope, 512) || !Component(canonicalWorkspace, 32767)
            || !Rooted(canonicalWorkspace) || !Relative(relativePath)) return false;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Span<byte> length = stackalloc byte[4];
            foreach (var value in new[] { "cocern-file-claim-v1", repositoryId, machineScope, canonicalWorkspace, relativePath })
            {
                var bytes = Strict.GetBytes(value);
                BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
                hash.AppendData(length);
                hash.AppendData(bytes);
            }
            scope = new("file-v1:" + Convert.ToHexString(hash.GetHashAndReset()), relativePath);
            return true;
        }
        catch (EncoderFallbackException) { return false; }
    }

    private static bool Component(string value, int max) => value is { Length: > 0 } && value.Length <= max
        && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);

    // This checks syntax only: native capture must already have canonicalized the actual working-copy root.
    private static bool Rooted(string value) => value.StartsWith('/') || value.StartsWith(@"\\", StringComparison.Ordinal)
        || value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '\\' or '/';

    private static bool Relative(string value)
    {
        if (!Component(value, 32767) || value.IndexOfAny(['\\', ':', '<', '>', '"', '|', '?', '*']) >= 0) return false;
        foreach (var part in value.Split('/'))
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')) return false;
            // Refuse Windows path aliases even when the hub happens to run on another OS.
            var stem = part.Split('.')[0].TrimEnd(' ', '.');
            if (new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$" }.Contains(stem, StringComparer.OrdinalIgnoreCase)
                || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && "123456789¹²³".Contains(stem[3])) return false;
        }
        return true;
    }
}
