namespace CoCern.Contracts;

public sealed record NodeFramework(string Name, string Version);
public sealed record NodePackageMetadata(int Schema, string ProductVersion, string TargetFramework, string Rid, bool SelfContained, NodeFramework[] Frameworks, int SupervisorProtocol);
public sealed record NodeRelease(int Schema, string ReleaseId, NodePackageMetadata Package, long ZipBytes, string ZipSha256, string AdapterDllSha256);
public sealed record NodeImage(string ContentId, NodePackageMetadata Package, string AdapterDllSha256, string? ReleaseId);
/// <summary>The running node's package and process, and the last release it refused to stage or start, when there is one.</summary>
public sealed record NodeBuildObservation(string ProductVersion, string ContentId, string AdapterDllSha256, int ProcessId, NodeUpdateRefusal? LastRefusal = null);
/// <summary>
/// A release a node refused or failed to stage or start: the hub's release ID, a short fixed reason code from <see cref="NodeUpdateRefusals"/>, and when.
/// It never carries a path, a message or a credential.
/// </summary>
public sealed record NodeUpdateRefusal(string ReleaseId, string Reason, DateTimeOffset AtUtc);

/// <summary>The reason codes a node reports for a refused release.</summary>
public static class NodeUpdateRefusals
{
    public const string InvalidRelease = "invalid-release";
    public const string InvalidRequest = "invalid-request";
    public const string UnsupportedRid = "unsupported-rid";
    public const string NoDotnetHost = "no-dotnet-host";
    public const string MissingRuntime = "missing-runtime";
    public const string DownloadRefused = "download-refused";
    public const string DownloadFailed = "download-failed";
    public const string SizeMismatch = "size-mismatch";
    public const string HashMismatch = "hash-mismatch";
    public const string ArchiveInvalid = "archive-invalid";
    public const string MetadataMismatch = "metadata-mismatch";
    public const string DllHashMismatch = "dll-hash-mismatch";
    public const string StoreFailed = "store-failed";
    public const string PrepareFailed = "prepare-failed";
    public const string ActivationFailed = "activation-failed";
    public const string Unavailable = "unavailable";

    /// <summary>A reason code as the hub accepts it: 1 to 32 lowercase letters, digits and hyphens.</summary>
    public static bool IsCode(string? reason) => reason is { Length: > 0 and <= 32 } && reason.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
}
