using System.Text.Json.Nodes;
using System.Text.Json;
using CoCern.Contracts;
using CoCern.Domain;
using CoCern.Domain.Messages;
using CoCern.Domain.State;
using Xunit;

namespace CoCern.Tests;

/// <summary>
/// The board's state class declares every key a board stores (<see cref="BoardState"/>), and <see cref="LockOrderState"/> the keys
/// standing orders keep on a lock. Each document is built through <see cref="CoordinationStateMachine.Apply"/>, as the hub builds it, and
/// must hold no key its shape leaves out; the board must also hold every key its shape declares, so the check covers each one.
/// </summary>
public sealed class BoardStateShapeTests
{
    private static readonly DateTimeOffset Now = DomainBoard.Now;
    private static readonly RelayCaller Hub = new("alice", false, ["repo"], true);

    private static JsonObject Document(StateEnvelope<CoordinationDocument> envelope) => JsonNode.Parse(envelope.Value.Json)!.AsObject();

    [Fact]
    public void A_board_stores_only_the_keys_its_state_declares_and_the_fixture_stores_each_of_them()
    {
        var envelope = DomainBoard.BuildWithEveryKey();
        // A real task supplies each captured source baseline. Canceled-watch
        // turnover exercises both declared subscription state and its replay
        // fences; no task or archived record is injected or excluded.
        var target = Document(envelope)[BoardState.TasksKey]!.AsObject().First().Key;
        for (var index = 0; index < 129; index++)
        {
            var watch = new EventWaitSourceWatch("repo", "shape-owner", "shape-wait-" + index, "shape-watch-" + index,
                "task_completed", AggregateKinds.Board, DomainBoard.Key, target, Now.AddHours(1), QualifyingData: "completed");
            var payload = JsonSerializer.SerializeToNode(watch, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
            Assert.True(CoordinationStateMachine.Apply(envelope, AggregateKinds.Board, DomainBoard.Key, Hub,
                EventWaitSources.SourceWatchOperation, payload, Now.AddMinutes(1), ordersEnabled: true).Ok);
            Assert.True(CoordinationStateMachine.Apply(envelope, AggregateKinds.Board, DomainBoard.Key, Hub,
                EventWaitSources.SourceUnwatchOperation, payload, Now.AddMinutes(1), ordersEnabled: true).Ok);
        }
        Assert.NotEmpty(Document(envelope)[EventWaitSources.SubscriptionsKey]!.AsObject());
        Assert.NotEmpty(Document(envelope)["eventWaitRetired"]!.AsObject());
        var board = Document(envelope);
        var archived = envelope.PendingArchive().Select(task => JsonNode.Parse(task.Json)!.AsObject()).ToArray();
        Assert.Equal(["dep", "old"], envelope.PendingArchive().Select(task => task.TaskId).Order(StringComparer.Ordinal));
        Assert.Empty(BoardState.Shape.Undeclared(board));
        // A task the board archived left with the keys it had, and its archive time.
        Assert.All(archived, task => Assert.Empty(BoardTask.Shape.Undeclared(task)));

        // Not vacuous: each record's every declared key is held by some record of its kind, on the board or in a task it archived. Only the
        // request tombstones are missing, which a board keeps once it has stored more replies than RelayLimits.MaxRecords.
        var coverage = new Coverage();
        coverage.Walk(BoardState.Shape, board);
        foreach (var task in archived) coverage.Walk(BoardTask.Shape, task);
        Assert.Equal([ReadModels.RequestTombstonesKey], coverage.Missing(BoardState.Shape));
    }

    [Fact]
    public void The_board_reads_back_through_its_state_what_its_operations_wrote()
    {
        var state = new BoardState(Document(DomainBoard.BuildWithEveryKey()));
        Assert.Equal(("repo", true, true), (state.RepositoryId, state.RequiresResources, state.TaskRevision > 0));
        Assert.Equal(["dep", "old"], state.Task("waiting")!.Workflow!.DependsOn);
        Assert.True(state.IsArchived("dep") && state.IsArchived("old") && state.ArchiveFloor > 0);
        Assert.Equal(("a", 1L), (state.Task("t")!.Owner, state.Task("t")!.ClaimGeneration));

        var retried = state.Task("retry")!;
        Assert.Equal((2L, 1), (retried.Action("flash")!.Attempt, retried.AttemptHistory.Count));
        Assert.True(new TaskReport(retried.Reports.Last()!.AsObject()).Historical);
        // Closed with its action in flight, recovered from c2, then completed by c3, which answers for it now.
        var push = state.Task("lost")!.Action("push")!;
        Assert.Equal(("completed", true, "c3"), (push.Status, push.OwnerClosed, push.Recovery!.SessionId));

        var standing = state.Standing!;
        Assert.Equal((3L, Now.AddSeconds(201)), (standing.Revision, standing.HubProposedAt("tip-x")));
        Assert.Contains(standing.ProposalRecords, proposal => proposal.ProposedBy == SessionRoles.HubProposer && proposal.Line!.Id() == "tip-x");
        var orders = state.Orders!;
        Assert.Equal((1L, false), (orders.Order("o-3")!.Runs, orders.Order("o-3")!.Disabled));
        Assert.True(orders.Order("o-2")!.Disabled);
    }

    [Fact]
    public void A_lock_declares_the_keys_standing_orders_keep_on_it()
    {
        RelayReply Apply(StateEnvelope<CoordinationDocument> envelope, string operation, JsonObject args)
        {
            args["repositoryId"] = "repo";
            return CoordinationStateMachine.Apply(envelope, AggregateKinds.Lock, "rig-1", Hub, operation, args, Now, ordersEnabled: true);
        }
        var shape = CommonState.Shape.With(LockOrderState.Fields);
        var release = new OrderRelease("repo", AggregateKinds.DefaultBoard, "repo/default", "a", "default/t#1", "o-1", 1);

        // Watched by two boards, one of which stops, and asked for a release it has no claim for: only the order keys.
        var unclaimed = new StateEnvelope<CoordinationDocument>();
        Assert.True(Apply(unclaimed, OrderWatch.Operation, new OrderWatch("repo", AggregateKinds.DefaultBoard, "repo/default", "o-2", true).ToJson()).Ok);
        Assert.True(Apply(unclaimed, OrderWatch.Operation, new OrderWatch("repo", "ops", "repo/ops", "o-1", true).ToJson()).Ok);
        Assert.True(Apply(unclaimed, OrderWatch.Operation, new OrderWatch("repo", "ops", "repo/ops", "", false).ToJson()).Ok);
        Assert.True(Apply(unclaimed, OrderRelease.Operation, release.ToJson()).Ok);
        Assert.Equal(new[] { CommonState.RepositoryIdKey, ReadModels.RequestsKey, LockOrderState.OrderBoardsKey, LockOrderState.OrderReleasesKey }.Order(StringComparer.Ordinal),
            Document(unclaimed).Select(entry => entry.Key).Order(StringComparer.Ordinal));
        Assert.Empty(shape.Undeclared(Document(unclaimed)));
        Assert.Equal([("repo/default", AggregateKinds.DefaultBoard)], new LockOrderState(Document(unclaimed)).WatchingBoards);

        // A claimed lock an order releases: the order keys are declared to their entries, and what is left are the lock's own keys.
        var claimed = new StateEnvelope<CoordinationDocument>();
        Assert.True(Apply(claimed, OrderWatch.Operation, new OrderWatch("repo", AggregateKinds.DefaultBoard, "repo/default", "o-2", true).ToJson()).Ok);
        Assert.True(Apply(claimed, RelayTools.ClaimAcquire, new() { ["sessionId"] = "a", ["requestId"] = "c", ["taskId"] = "t", ["taskClaimGeneration"] = 1 }).Ok);
        Assert.True(JsonNode.Parse(Apply(claimed, OrderRelease.Operation, release.ToJson()).DataJson)!["released"]!.GetValue<bool>());
        Assert.True(JsonNode.Parse(Apply(claimed, OrderRelease.Operation, release.ToJson()).DataJson)!["repeated"]!.GetValue<bool>());
        var orderKeys = LockOrderState.Fields.Select(field => field.Key).ToArray();
        var undeclared = shape.Undeclared(Document(claimed));
        Assert.NotEmpty(undeclared);
        Assert.All(undeclared, path => Assert.True(!path.Contains('.') && !orderKeys.Contains(path), path));
        // The release told the watching board, with the lock's generation.
        var fire = Assert.Single(claimed.Effects, effect => effect.Operation == OrderFire.Operation);
        Assert.Equal(("repo/default", new OrderFire("repo", AggregateKinds.DefaultBoard, "rig-1", 1)), (fire.TargetKey, OrderFire.Decode(JsonNode.Parse(fire.PayloadJson)!.AsObject()).Message));
    }

    /// <summary>
    /// Which declared keys a document's records hold, by record kind (one <see cref="StateShape"/>), so a key is covered when any record
    /// of its kind holds it, wherever that kind is used.
    /// </summary>
    private sealed class Coverage
    {
        private readonly Dictionary<StateShape, HashSet<string>> seen = new(ReferenceEqualityComparer.Instance);

        private HashSet<string> Keys(StateShape shape) => seen.TryGetValue(shape, out var keys) ? keys : seen[shape] = new(StringComparer.Ordinal);

        public void Walk(StateShape? shape, JsonNode? node)
        {
            if (shape is null || shape.Opaque || node is null) return;
            if (node is JsonArray array)
            {
                foreach (var item in array) Walk(shape, item);
                return;
            }
            if (node is not JsonObject json) return;
            foreach (var (key, value) in json)
            {
                if (shape.KeyedByIds) { Walk(shape.Entries, value); continue; }
                Keys(shape).Add(key);
                if (shape.Fields.TryGetValue(key, out var child)) Walk(child, value);
            }
        }

        /// <summary>The declared keys no record of their kind holds, each by the path where its kind first appears under <paramref name="root"/>.</summary>
        public IReadOnlyList<string> Missing(StateShape root)
        {
            var visited = new HashSet<StateShape>(ReferenceEqualityComparer.Instance);
            var missing = new List<string>();
            void Visit(StateShape? shape, string path)
            {
                if (shape is null || shape.Opaque || !visited.Add(shape)) return;
                if (shape.KeyedByIds) { Visit(shape.Entries, Join(path, "*")); return; }
                foreach (var (key, child) in shape.Fields)
                {
                    if (!Keys(shape).Contains(key)) missing.Add(Join(path, key));
                    Visit(child, Join(path, key));
                }
            }
            Visit(root, "");
            return missing;
        }

        private static string Join(string path, string key) => path.Length == 0 ? key : path + "." + key;
    }
}
