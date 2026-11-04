using CoCern.Contracts;
using System.Text.Json.Serialization;

namespace CoCern.Adapter.Node;

internal sealed record NodeUpdateContext(string NodeFolder, Uri EnrolledHub, HttpClient PinnedHttp, [property: JsonIgnore] string MachineCredential, NodeHostCapabilities Host)
{
    public override string ToString() => "NodeUpdateContext";
}

internal interface INodeUpdateCoordinator
{
    // Null means no change; rejected staging throws so the scheduler can apply failure backoff.
    Task<StagedNode?> TryStageAsync(NodeRelease? offered, CancellationToken ct);
    Task PrepareAsync(StagedNode staged, CancellationToken ct);
}

internal sealed class NodeUpdateCoordinator(NodeUpdateContext context) : INodeUpdateCoordinator
{
    private readonly SemaphoreSlim owner = new(1, 1);
    public async Task<StagedNode?> TryStageAsync(NodeRelease? offered, CancellationToken ct)
    {
        if (offered is null || !await owner.WaitAsync(0, ct)) return null;
        try
        {
            var root = Path.Combine(context.NodeFolder, "updates");
            var journal = NodeUpdateState.Read(root).Record;
            if (journal?.Current.ReleaseId == offered.ReleaseId) return null;
            var staged = await NodePackage.StageAsync(offered, context.EnrolledHub, context.PinnedHttp, context.MachineCredential, root, context.Host, ct)
                ?? throw new InvalidDataException("node-update:stage-rejected");
            return staged.Image.ContentId == journal?.Current.ContentId ? null : staged;
        }
        finally { owner.Release(); }
    }
    public Task PrepareAsync(StagedNode staged, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var root = NodePackage.ValidatedUpdateRoot(Path.Combine(context.NodeFolder, "updates"), create: false);
        var expected = Path.Combine(root, "versions", staged.Image.ContentId);
        if (!NodePackage.ValidRelease(staged.Release) || !string.Equals(Path.GetFullPath(staged.Directory), expected, StringComparison.OrdinalIgnoreCase) ||
            staged.Image.ReleaseId != staged.Release.ReleaseId || staged.Image.AdapterDllSha256 != staged.Release.AdapterDllSha256 ||
            !NodePackage.SameMetadata(staged.Image.Package, staged.Release.Package) || NodePackage.Launch(staged.Image, context.NodeFolder, null, context.Host) is null) throw new InvalidDataException("node-request-invalid");
        ct.ThrowIfCancellationRequested();
        NodeUpdateState.WriteRequest(root, new(staged.Image));
        return Task.CompletedTask;
    }
}
