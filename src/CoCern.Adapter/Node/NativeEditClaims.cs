using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using CoCern.Contracts;

namespace CoCern.Adapter.Node;

/// <summary>Composition seam for the accepted private native binding, never constructed from hook event scope fields.</summary>
internal sealed class NativeEditClaims(string privateDirectory, string protectedRoot, string repositoryId, string sessionId,
    string generation, NativeJoin join, NativeHookContext context)
{
    private readonly string ledgerDirectory = Path.Combine(privateDirectory, "file-claims-" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { repositoryId, sessionId, generation, protectedRoot })))));

    public async Task<EditClaimDecision> CheckAsync(string intentId, string cwd, IReadOnlyList<string> targets, CancellationToken cancellationToken)
    {
        var resolved = EditClaimResolver.Resolve(protectedRoot, cwd, targets);
        if (!resolved.Supported) return new(false, resolved.Reason);
        var paths = resolved.Targets.Select(target => target.RelativeKey).ToArray();
        if (ProtectedCheckoutRoot.Prove(cwd) != protectedRoot) return new(false, "path-changed");
        var transport = new FileTransport(protectedRoot, repositoryId, generation, join, context, cancellationToken);
        EditClaimDecision decision;
        // A clean release must not land between this confirmation and the published attempt, or this edit would go ahead on a claim it no longer has.
        await using (var gate = await new EditClaimLifecycleStore(ledgerDirectory).DecisionGateAsync(EditClaimLifecycle.PreDecisionWait, cancellationToken))
        {
            if (gate is null) return new(false, "lifecycle-busy");
            decision = await new EditClaimGuard(new EditClaimLedger(ledgerDirectory), transport).AcquireAsync(intentId, paths, cancellationToken);
            if (!decision.Abstain) return decision;
            var current = EditClaimResolver.Resolve(protectedRoot, cwd, targets);
            if (!current.Supported || !current.Targets.SequenceEqual(resolved.Targets) || ProtectedCheckoutRoot.Prove(cwd) != protectedRoot) return new(false, "path-changed");
            // Without the saved attempt this edit could never be told from a sibling still needing the claim, so it refuses like an unavailable ledger.
            if (!await Lifecycle(cancellationToken).PrepareAsync(intentId, paths, cancellationToken)) return new(false, "lifecycle-unavailable");
        }
        EditClaimLifecycleMarker.Set(join.PrivateFolder, context, DateTimeOffset.UtcNow.AddSeconds(EditClaimLifecycle.LeaseSeconds / 2));
        return decision;
    }

    internal EditClaimLifecycle Lifecycle(CancellationToken cancellationToken) => new(new EditClaimLedger(ledgerDirectory), new EditClaimLifecycleStore(ledgerDirectory),
        new EditClaimCheckout(protectedRoot, context.Cwd), new FileTransport(protectedRoot, repositoryId, generation, join, context, cancellationToken));

    // The persisted guard identifiers are canonical relative paths. The hub alone derives opaque resource keys.
    private sealed class FileTransport(string root, string repositoryId, string expectedGeneration, NativeJoin join, NativeHookContext context, CancellationToken cancellationToken) : IEditClaimTransport, IEditClaimLifecycleTransport
    {
        public async Task<EditClaimAcquireResult> AcquireAsync(string resourceId, string requestId)
        {
            var reply = await CallAsync(RelayTools.ClaimAcquire, resourceId, requestId);
            if (!reply.Ok) return new(reply.Code == RelayErrorCodes.Busy ? "busy" : "unknown",
                Holder: reply.Data is { ValueKind: JsonValueKind.Object } busy ? ChatLocators.Text(busy, "holder") : null);
            if (reply.Data is not { ValueKind: JsonValueKind.Object } data
                || !data.TryGetProperty("generation", out var generation) || !generation.TryGetInt64(out var value) || value <= 0
                || !data.TryGetProperty("newlyAcquired", out var acquired) || acquired.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return new("unknown");
            return new("held", value, !acquired.GetBoolean());
        }

        public async Task<bool> ConfirmAsync(string resourceId, long generation)
        {
            var reply = await CallAsync(RelayTools.ClaimAcquire, resourceId, Guid.NewGuid().ToString("N"), confirmGeneration: generation);
            return reply.Ok && reply.Data is { ValueKind: JsonValueKind.Object } data
                && data.TryGetProperty("held", out var held) && held.ValueKind == JsonValueKind.True
                && data.TryGetProperty("generation", out var current) && current.TryGetInt64(out var value) && value == generation;
        }

        public async Task<bool?> ReleaseAsync(string resourceId, long generation, string requestId)
        {
            var reply = await CallAsync(RelayTools.ClaimRelease, resourceId, requestId, generation: generation);
            return reply.Ok ? true : null;
        }

        public Task<AdapterReply> RenewAsync(string relativePath, long generation, string invocation) =>
            CallAsync(RelayTools.ClaimRenew, relativePath, invocation, generation: generation, leaseSeconds: EditClaimLifecycle.LeaseSeconds);

        public Task<AdapterReply> CleanReleaseAsync(string relativePath, long generation, string invocation) =>
            CallAsync(RelayTools.ClaimRelease, relativePath, invocation, generation: generation);

        public Task<AdapterReply> ReadAsync(string relativePath, long generation) =>
            CallAsync(RelayTools.ClaimAcquire, relativePath, Guid.NewGuid().ToString("N"), confirmGeneration: generation);

        public Task<bool> RetireRenewalAsync(string relativePath, long generation, string invocation) =>
            join.RetireRenewalFenceAsync(context, repositoryId, expectedGeneration, invocation, JsonSerializer.SerializeToElement(Arguments(relativePath, generation, null, EditClaimLifecycle.LeaseSeconds)), cancellationToken);

        public async Task<bool> RetireSettledAsync(string relativePath, long generation, string acquireInvocation) =>
            await RetireAsync(RelayTools.ClaimAcquire, relativePath, null, acquireInvocation)
            && await RetireAsync(RelayTools.ClaimRelease, relativePath, generation, acquireInvocation + "-release")
            && await RetireAsync(RelayTools.ClaimRelease, relativePath, generation, acquireInvocation + "-clean-release");

        private Task<bool> RetireAsync(string tool, string path, long? generation, string invocation) =>
            join.RetireClaimFenceAsync(context, repositoryId, expectedGeneration, invocation, tool, JsonSerializer.SerializeToElement(Arguments(path, generation, null, null)), cancellationToken);

        private static JsonObject Arguments(string path, long? generation, long? confirmGeneration, int? leaseSeconds)
        {
            var args = new JsonObject { ["filePath"] = path };
            if (generation is { } grant) args["generation"] = grant;
            if (confirmGeneration is { } expected) args["confirmGeneration"] = expected;
            if (leaseSeconds is { } lease) args["leaseSeconds"] = lease;
            return args;
        }

        private Task<AdapterReply> CallAsync(string tool, string path, string requestId, long? generation = null, long? confirmGeneration = null, int? leaseSeconds = null)
        {
            // Revalidate local containment/case/link semantics at every use, including the final ownership read.
            var physical = EditClaimResolver.Resolve(root, root, [path]);
            if (!physical.Supported || physical.Targets.Count != 1 || physical.Targets[0].RelativeKey != path)
                return Task.FromResult(new AdapterReply(false, "physical-scope-unavailable"));
            if (ProtectedCheckoutRoot.Prove(context.Cwd) != root) return Task.FromResult(new AdapterReply(false, "physical-scope-unavailable"));
            return join.CallAsync(context, requestId, tool, JsonSerializer.SerializeToElement(Arguments(path, generation, confirmGeneration, leaseSeconds)), cancellationToken, expectedGeneration);
        }
    }
}
