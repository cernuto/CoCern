using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CoCern.Contracts;

namespace CoCern.Domain;

/// <summary>A session identity produced by hub verification, without its credential.</summary>
public sealed record VerifiedNativeParticipant(string ParticipantId, string SessionId, string RepositoryId);

/// <summary>A current native join's authenticated machine and adapter-proven checkout. Consumers must revalidate at file use.</summary>
public sealed record VerifiedNativeFileScope(string ParticipantId, string SessionId, string RepositoryId, string MachineScope, string CanonicalWorkingCopyRoot, string NativeGeneration);

public static class NativeParticipant
{
    public const string Id = "nativeParticipantId";
    public const string Generation = "nativeGeneration";
    public const string Request = "nativeRequestId";
    public const string ConfirmedExpiry = "nativeExpiredSessionId";
    /// <summary>Hub-set from the proof's provider. Display only, kept on the private join entry.</summary>
    public const string Client = "nativeClient";
    /// <summary>The chat's title, which an adapter may send beside the frozen proof. Display only and untrusted: outside the identity hash, it grants nothing.</summary>
    public const string DisplayLabel = "label";

    /// <summary>How many top-level fields a join carries: repository, machine, request and proof, and the optional display label.</summary>
    public static int JoinFields(JsonObject args) => args.ContainsKey(DisplayLabel) ? 5 : 4;
    public const string WorkingCopy = "nativeWorkingCopy";
    public const string ProtectedRoot = "nativeProtectedRoot";
    /// <summary>Hub-only marker; the proof itself uses the ordinary redacted session credential fields.</summary>
    public const string Adoption = "nativeAdoption";

    // Replace the adapter's exact frozen proof with hub-only arguments before tracing, replay or persistence.
    public static bool Admit(RelayCaller caller, JsonObject args)
    {
        if (args.Count != JoinFields(args) || args["nativeJoin"] is not JsonObject join) return false;
        try
        {
            if (args.ContainsKey(DisplayLabel) && !String(args, DisplayLabel, out _)) return false;
            var hasProtectedRoot = join.ContainsKey("protectedRoot");
            var hasAdoption = join.ContainsKey("adopt");
            if (join.Count != 5 + (hasProtectedRoot ? 1 : 0) + (hasAdoption ? 1 : 0) || !String(join, "provider", out var provider) || provider is not ("claude" or "codex")
                || !String(join, "nativeId", out var native) || !CanonicalGuid(native, "D")
                || !String(join, "workspace", out var workspace) || workspace.Length > 32767 || workspace.Any(char.IsControl)
                || !(workspace.StartsWith('/') || workspace.StartsWith(@"\\", StringComparison.Ordinal)
                    || workspace.Length >= 3 && char.IsAsciiLetter(workspace[0]) && workspace[1] == ':' && workspace[2] is '\\' or '/')
                || !String(join, "bindingSecret", out var encoded) || encoded.Length != 44
                || !String(join, "generation", out var generation) || !CanonicalGuid(generation, "N")
                || !String(args, "requestId", out var request) || !CanonicalGuid(request, "N")
                || !String(args, "machineId", out var machine) || !String(args, "repositoryId", out var repository)) return false;
            string? protectedRoot = null;
            if (hasProtectedRoot && (!String(join, "protectedRoot", out protectedRoot) || !ValidProtectedRoot(protectedRoot))) return false;
            string? adoptionId = null, adoptionToken = null;
            if (hasAdoption && (join["adopt"] is not JsonObject adoption || adoption.Count != 2 ||
                !String(adoption, "sessionId", out adoptionId) || !CanonicalGuid(adoptionId, "N") ||
                !String(adoption, "sessionToken", out adoptionToken) || adoptionToken.Length is 0 or > 8192 || adoptionToken.Any(char.IsControl))) return false;
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
            args[Client] = provider;
            args[WorkingCopy] = workspace;
            if (protectedRoot is not null) args[ProtectedRoot] = protectedRoot;
            if (adoptionId is not null)
            {
                args[Adoption] = true;
                args["sessionId"] = adoptionId;
                args["sessionToken"] = adoptionToken;
            }
            return true;
        }
        catch (ArgumentException) { return false; } // Repeated nested JSON keys.
    }

    // Validate the private adapter proof's representation without interpreting a remote machine's path on the hub.
    // This is not physical verification: only the adapter's ProtectedCheckoutRoot.Prove may supply this field.
    internal static bool ValidProtectedRoot(string root) => root.Length is >= 3 and <= 32767
        && char.IsAsciiLetter(root[0]) && root[1] == ':' && root[2] == '\\'
        && !root.Any(character => char.IsControl(character) || character is '/' or '"' or '<' or '>' or '|' or '?' or '*')
        && !root.AsSpan(3).Contains(':')
        && (root.Length == 3 || !root[3..].Split('\\').Any(part => part is "" or "." or ".."));

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
