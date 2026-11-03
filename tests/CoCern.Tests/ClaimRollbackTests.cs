using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orleans;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
using Xunit;

namespace CoCern.Tests;

/// <summary>
/// A lock call whose reply is lost after the lock committed (a timeout after its write): what the call did must still be undone, so a failed
/// task claim or action begin never leaves a lock held or blocked for its lease.
/// </summary>
public sealed class ClaimRollbackTests
{
    private static readonly RelayCaller Caller = new("alice", true, ["repo"], true);

    [Fact]
    public async Task A_task_claim_whose_lock_grant_reply_is_lost_releases_that_lock_with_the_rest()
    {
        var (invoke, factory) = Application();
        var one = Data(await invoke("session_open", new())); var two = Data(await invoke("session_open", new()));
        foreach (var resource in new[] { "a", "b" }) Assert.True((await invoke("control_request", new() { ["command"] = "register_resource", ["resourceId"] = resource, ["machineId"] = "pc" })).Ok);
        var create = For(one); create["taskId"] = "t"; create["expectedVersion"] = 0; Assert.True((await invoke("task_update", create)).Ok);

        factory.LoseReply(AggregateKinds.Lock, "b", RelayTools.ClaimAcquire);
        var claim = For(one); claim["taskId"] = "t"; claim["expectedVersion"] = 1; claim["resourceIds"] = new JsonArray("a", "b");
        var refused = await invoke("task_claim", claim);
        Assert.Equal(RelayErrorCodes.Unavailable, refused.Code);
        Assert.Equal("new_grants_released", Data(refused)["rollback"]!.GetValue<string>());
        Assert.Equal(1, factory.Lost);

        // Both grants, the one whose reply was lost included, and the task claim are undone: another session takes each at once.
        foreach (var resource in new[] { "a", "b" })
        {
            var acquire = For(two); acquire["resourceId"] = resource;
            var taken = await invoke("claim_acquire", acquire);
            Assert.True(taken.Ok, resource + ": " + taken.Code);
        }
        Assert.Null(Data(await invoke("task_get", new() { ["taskId"] = "t" }))["owner"]);
    }

    [Fact]
    public async Task An_action_begin_whose_block_reply_is_lost_lifts_every_block_it_placed()
    {
        var (invoke, factory) = Application();
        var one = Data(await invoke("session_open", new()));
        foreach (var resource in new[] { "a", "b" }) Assert.True((await invoke("control_request", new() { ["command"] = "register_resource", ["resourceId"] = resource, ["machineId"] = "pc" })).Ok);
        var create = For(one); create["taskId"] = "t"; create["expectedVersion"] = 0; Assert.True((await invoke("task_update", create)).Ok);
        var claim = For(one); claim["taskId"] = "t"; claim["expectedVersion"] = 1; claim["resourceIds"] = new JsonArray("a", "b");
        var claimed = await invoke("task_claim", claim);
        Assert.True(claimed.Ok, claimed.Code);

        factory.LoseReply(AggregateKinds.Lock, "b", Operations.Block);
        var begin = For(one); begin["taskId"] = "t"; begin["expectedVersion"] = 2; begin["actionId"] = "deploy"; begin["inputFingerprint"] = "f";
        var pending = await invoke("action_begin", begin);
        Assert.Equal(RelayErrorCodes.ActionPending, pending.Code);
        Assert.False(Data(pending)["replayAllowed"]!.GetValue<bool>());
        Assert.Equal(1, factory.Lost);

        // Neither lock is left blocked, the one whose block did land included: each grant releases normally.
        foreach (var grant in Data(claimed)["grants"]!.AsArray())
        {
            var release = For(one); release["resourceId"] = grant!["resourceId"]!.DeepClone(); release["generation"] = grant["generation"]!.DeepClone();
            var released = await invoke("claim_release", release);
            Assert.True(released.Ok, grant["resourceId"] + ": " + released.Code);
        }
    }

    private static (Func<string, JsonObject, Task<RelayReply>> Invoke, LosingGrainFactory Factory) Application()
    {
        var grains = DispatchProxy.Create<IGrainFactory, LosingGrainFactory>();
        var application = new RelayApplication(grains);
        return (Invoke, (LosingGrainFactory)(object)grains);

        Task<RelayReply> Invoke(string tool, JsonObject args)
        {
            args["repositoryId"] = "repo";
            if (!args.ContainsKey("requestId")) args["requestId"] = Guid.NewGuid().ToString("N");
            return application.ExecuteAsync(Caller with { IsInternal = false }, tool, JsonSerializer.SerializeToElement(args));
        }
    }

    private static JsonObject For(JsonObject session) => new() { ["sessionId"] = session["sessionId"]!.DeepClone(), ["sessionToken"] = session["sessionToken"]!.DeepClone() };

    private static JsonObject Data(RelayReply reply) => JsonNode.Parse(reply.DataJson)!.AsObject();

    /// <summary>In-memory aggregates, one of whose next calls applies and commits as usual and then throws, as a reply lost to a timeout.</summary>
    public class LosingGrainFactory : DispatchProxy
    {
        private readonly Dictionary<string, Aggregate> aggregates = [];
        private (string Kind, string Key, string Operation)? lose;
        public int Lost;

        public void LoseReply(string kind, string key, string operation) => lose = (kind, key, operation);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != "GetGrain" || !targetMethod.IsGenericMethod) throw new NotSupportedException();
            var type = targetMethod.GetGenericArguments()[0];
            var kind = type == typeof(ISessionGrain) ? AggregateKinds.Session : type == typeof(ITaskBoardGrain) ? AggregateKinds.Board : type == typeof(ILockGrain) ? AggregateKinds.Lock : AggregateKinds.Control;
            var key = args![0]!.ToString()!;
            if (!aggregates.TryGetValue(kind + ":" + key, out var aggregate)) aggregates[kind + ":" + key] = aggregate = new(this, kind, key);
            return aggregate;
        }

        private sealed class Aggregate(LosingGrainFactory owner, string kind, string key) : ISessionGrain, ITaskBoardGrain, ILockGrain, IControlGrain
        {
            private readonly StateEnvelope<CoordinationDocument> state = new();

            public Task<RelayReply> ExecuteAsync(RelayCaller caller, string operation, string argumentsJson)
            {
                var reply = CoordinationStateMachine.Apply(state, kind, key, caller, operation, JsonNode.Parse(argumentsJson)!.AsObject(), DateTimeOffset.UtcNow);
                if (owner.lose != (kind, key, operation)) return Task.FromResult(reply);
                owner.lose = null;
                owner.Lost++;
                return Task.FromException<RelayReply>(new TimeoutException("The reply was lost after the lock committed."));
            }
        }
    }
}
