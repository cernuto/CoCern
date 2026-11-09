using System.Globalization;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain;
using CoCern.Domain.Messages;
using static CoCern.Contracts.RelayErrorCodes;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Core;

/// <summary>File claims bypass IDE registration only after fresh authenticated native-scope resolution.</summary>
internal sealed class FileClaimTools(Aggregates aggregates, CallGuard guard)
{
    internal static bool Reserved(string resource) => resource.StartsWith("file-v1:", StringComparison.OrdinalIgnoreCase);

    public async Task<RelayReply> RunAsync(RelayCall call)
    {
        var args = call.Args;
        if (call.Tool is not (RelayTools.ClaimAcquire or RelayTools.ClaimRenew or RelayTools.ClaimRelease)
            || args["filePath"] is not JsonValue pathValue || !pathValue.TryGetValue<string>(out var path)
            || path.Length is 0 or > 512 || path.Any(c => c > 127)
            || new[] { "resourceId", "resourceIds", "taskId", "machineId", "workspace", "participantId", "nativeGeneration" }.Any(args.ContainsKey))
            return Error(InvalidArguments);
        var scope = await guard.ResolveNativeFileScopeAsync(call);
        if (scope is null) return Error(ClaimRequired);
        if (!FileClaimScope.TryCreate(scope.RepositoryId, scope.MachineScope,
            scope.CanonicalWorkingCopyRoot.ToLowerInvariant(), path.ToLowerInvariant(), out var file)) return Error(InvalidArguments);

        RelayReply reply;
        if (args.ContainsKey("confirmGeneration"))
        {
            if (call.Tool != RelayTools.ClaimAcquire || args["confirmGeneration"] is not JsonValue expectedValue
                || !expectedValue.TryGetValue<long>(out var expected) || expected <= 0 || args.ContainsKey("leaseSeconds")) return Error(InvalidArguments);
            // This read has no request ID, no renewal, and never trusts a replayed acquire receipt.
            reply = await aggregates.Request(file.ResourceId, call.Caller,
                new ClaimStateQuery(scope.RepositoryId, scope.SessionId));
            if (!reply.Ok) return reply;
            var state = Aggregates.Parse(reply);
            var held = state["held"]?.GetValue<bool>() == true && Number(state, "generation") == expected
                && DateTimeOffset.TryParse(Text(state, "leaseUntil"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var until)
                && until >= DateTimeOffset.UtcNow.AddSeconds(15);
            reply = Ok(new { resourceId = file.ResourceId, held, generation = Number(state, "generation"), leaseUntil = Text(state, "leaseUntil") }, reply.Version);
        }
        else
        {
            var claim = args.DeepClone().AsObject();
            claim.Remove("filePath");
            claim["resourceId"] = file.ResourceId;
            reply = await aggregates.Call(AggregateKinds.Lock, file.ResourceId, call.Caller, call.Tool, claim);
            // A chat that moved to another checkout still settles claims it took before the move: a release or renewal the current root does not hold
            // tries this machine's earlier roots, newest first. The lock grants nothing new: it acts only on a claim this session holds at this generation.
            if (reply.Code == StaleClaim && call.Tool is RelayTools.ClaimRelease or RelayTools.ClaimRenew)
                foreach (var earlier in scope.EarlierRoots.Reverse())
                {
                    if (!FileClaimScope.TryCreate(scope.RepositoryId, scope.MachineScope, earlier.ToLowerInvariant(), path.ToLowerInvariant(), out var moved)) continue;
                    claim["resourceId"] = moved.ResourceId;
                    var settled = await aggregates.Call(AggregateKinds.Lock, moved.ResourceId, call.Caller, call.Tool, claim);
                    if (settled.Code == StaleClaim) continue;
                    (reply, file) = (settled, moved);
                    break;
                }
            if (reply.Ok)
            {
                var data = Aggregates.Parse(reply);
                data["resourceId"] = file.ResourceId;
                reply = Ok(data, reply.Version);
            }
        }
        // Scope resolution and a lock call are separate awaits. Never authorize through a replaced/closing native binding.
        return await guard.ResolveNativeFileScopeAsync(call) == scope ? reply : Error(ClaimRequired);
    }
}
