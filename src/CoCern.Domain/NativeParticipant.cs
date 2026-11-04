using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CoCern.Contracts;

namespace CoCern.Domain;

/// <summary>A session identity produced by hub verification, without its credential.</summary>
public sealed record VerifiedNativeParticipant(string ParticipantId, string SessionId, string RepositoryId);

public static class NativeParticipant
{
    public const string Id = "nativeParticipantId";
    public const string Generation = "nativeGeneration";
    public const string Request = "nativeRequestId";
    public const string ConfirmedExpiry = "nativeExpiredSessionId";

    // Replace the adapter's exact frozen proof with hub-only arguments before tracing, replay or persistence.
    public static bool Admit(RelayCaller caller, JsonObject args)
    {
        if (args.Count != 4 || args["nativeJoin"] is not JsonObject join) return false;
        try
        {
            if (join.Count != 5 || !String(join, "provider", out var provider) || provider is not ("claude" or "codex")
                || !String(join, "nativeId", out var native) || !CanonicalGuid(native, "D")
                || !String(join, "workspace", out var workspace) || workspace.Length > 32767 || workspace.Any(char.IsControl)
                || !(workspace.StartsWith('/') || workspace.StartsWith(@"\\", StringComparison.Ordinal)
                    || workspace.Length >= 3 && char.IsAsciiLetter(workspace[0]) && workspace[1] == ':' && workspace[2] is '\\' or '/')
                || !String(join, "bindingSecret", out var encoded) || encoded.Length != 44
                || !String(join, "generation", out var generation) || !CanonicalGuid(generation, "N")
                || !String(args, "requestId", out var request) || !CanonicalGuid(request, "N")
                || !String(args, "machineId", out var machine) || !String(args, "repositoryId", out var repository)) return false;
            Span<byte> secret = stackalloc byte[32];
            if (!Convert.TryFromBase64String(encoded, secret, out var length) || length != secret.Length || Convert.ToBase64String(secret) != encoded) return false;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Span<byte> size = stackalloc byte[4];
            foreach (var value in new[] { "cocern-native-participant-v1", caller.PrincipalId, repository, provider, native, machine, workspace })
            {
                var bytes = Encoding.UTF8.GetBytes(value);
                BinaryPrimitives.WriteInt32BigEndian(size, bytes.Length);
                hash.AppendData(size);
                hash.AppendData(bytes);
            }
            BinaryPrimitives.WriteInt32BigEndian(size, secret.Length);
            hash.AppendData(size);
            hash.AppendData(secret);
            CryptographicOperations.ZeroMemory(secret);
            args.Remove("nativeJoin");
            // The directory retains this pair past ordinary replay eviction and must recheck the live row on every attempt.
            args.Remove("requestId");
            args[Id] = Convert.ToHexString(hash.GetHashAndReset());
            args[Generation] = generation;
            args[Request] = request;
            return true;
        }
        catch (ArgumentException) { return false; } // Repeated nested JSON keys.
    }

    private static bool String(JsonObject json, string key, out string text)
    {
        text = "";
        if (json[key] is not JsonValue value || !value.TryGetValue<string>(out var found) || string.IsNullOrWhiteSpace(found)) return false;
        text = found;
        return true;
    }

    private static bool CanonicalGuid(string value, string format) =>
        Guid.TryParseExact(value, format, out var guid) && value == guid.ToString(format);
}
