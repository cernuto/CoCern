namespace CoCern.Contracts;

public sealed record NodeFramework(string Name, string Version);
public sealed record NodePackageMetadata(int Schema, string ProductVersion, string TargetFramework, string Rid, bool SelfContained, NodeFramework[] Frameworks, int SupervisorProtocol);
public sealed record NodeRelease(int Schema, string ReleaseId, NodePackageMetadata Package, long ZipBytes, string ZipSha256, string AdapterDllSha256);
public sealed record NodeImage(string ContentId, NodePackageMetadata Package, string AdapterDllSha256, string? ReleaseId);
