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
        var transport = new FileTransport(protectedRoot, generation, join, context, cancellationToken);
        var decision = await new EditClaimGuard(new EditClaimLedger(ledgerDirectory), transport).AcquireAsync(intentId, paths, cancellationToken);
        if (!decision.Abstain) return decision;
        var current = EditClaimResolver.Resolve(protectedRoot, cwd, targets);
        return current.Supported && current.Targets.SequenceEqual(resolved.Targets) && ProtectedCheckoutRoot.Prove(cwd) == protectedRoot
            ? decision : new(false, "path-changed");
    }

    // The persisted guard identifiers are canonical relative paths. The hub alone derives opaque resource keys.
    private sealed class FileTransport(string root, string expectedGeneration, NativeJoin join, NativeHookContext context, CancellationToken cancellationToken) : IEditClaimTransport
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

        private Task<AdapterReply> CallAsync(string tool, string path, string requestId, long? generation = null, long? confirmGeneration = null)
        {
            // Revalidate local containment/case/link semantics at every use, including the final ownership read.
            var physical = EditClaimResolver.Resolve(root, root, [path]);
            if (!physical.Supported || physical.Targets.Count != 1 || physical.Targets[0].RelativeKey != path)
                return Task.FromResult(new AdapterReply(false, "physical-scope-unavailable"));
            if (ProtectedCheckoutRoot.Prove(context.Cwd) != root) return Task.FromResult(new AdapterReply(false, "physical-scope-unavailable"));
            var args = new JsonObject { ["filePath"] = path };
            if (generation is { } grant) args["generation"] = grant;
            if (confirmGeneration is { } expected) args["confirmGeneration"] = expected;
            return join.CallAsync(context, requestId, tool, JsonSerializer.SerializeToElement(args), cancellationToken, expectedGeneration);
        }
    }
}
