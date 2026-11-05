using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Domain;
using CoCern.Domain.State;
using Xunit;

namespace CoCern.Tests;

public sealed class BoardStateShapeTests
{
    private static readonly DateTimeOffset Now = DomainBoard.Now;
    private static readonly RelayCaller Hub = new("alice", false, ["repo"], true);
    private static JsonObject Document(StateEnvelope<CoordinationDocument> envelope) => JsonNode.Parse(envelope.Value.Json)!.AsObject();

    [Fact]
    public void A_board_stores_only_the_keys_its_state_declares()
    {
        var envelope = DomainBoard.BuildWithEveryKey();
        var target = Document(envelope)[BoardState.TasksKey]!.AsObject().First().Key;
        ExhaustiveStateFixture.ExerciseSourceRetirement(index => new EventWaitSourceWatch("repo", "shape-owner", "shape-wait-" + index, "shape-watch-" + index,
            "task_completed", AggregateKinds.Board, DomainBoard.Key, target, Now.AddHours(1), QualifyingData: "completed"),
            (operation, payload) => CoordinationStateMachine.Apply(envelope, AggregateKinds.Board, DomainBoard.Key, Hub,
                operation, payload, Now.AddMinutes(1), ordersEnabled: true));
        Assert.NotEmpty(Document(envelope)[EventWaitSources.SubscriptionsKey]!.AsObject());
        Assert.NotEmpty(Document(envelope)["eventWaitRetired"]!.AsObject());
        var board = Document(envelope);
        var archived = envelope.PendingArchive().Select(task => JsonNode.Parse(task.Json)!.AsObject()).ToArray();
        Assert.Equal(["dep", "old"], envelope.PendingArchive().Select(task => task.TaskId).Order(StringComparer.Ordinal));
        ExhaustiveStateFixture.Verify(BoardState.Shape, [board], archived.Select(task => (BoardTask.Shape, task)).ToArray());

        // Reuse the same expensive fixture for its typed reads.
        var state = new BoardState(board);
        Assert.Equal(("repo", true, true), (state.RepositoryId, state.RequiresResources, state.TaskRevision > 0));
        Assert.Equal(["dep", "old"], state.Task("waiting")!.Workflow!.DependsOn);
        Assert.True(state.IsArchived("dep") && state.IsArchived("old") && state.ArchiveFloor > 0);
        Assert.Equal(("a", 1L), (state.Task("t")!.Owner, state.Task("t")!.ClaimGeneration));
        var retried = state.Task("retry")!;
        Assert.Equal((2L, 1), (retried.Action("flash")!.Attempt, retried.AttemptHistory.Count));
        Assert.True(new TaskReport(retried.Reports.Last()!.AsObject()).Historical);
        var push = state.Task("lost")!.Action("push")!;
        Assert.Equal(("completed", true, "c3"), (push.Status, push.OwnerClosed, push.Recovery!.SessionId));
        var standing = state.Standing!;
        Assert.Equal((3L, Now.AddSeconds(201)), (standing.Revision, standing.HubProposedAt("tip-x")));
        Assert.Contains(standing.ProposalRecords, proposal => proposal.ProposedBy == SessionRoles.HubProposer && proposal.Line!.Id() == "tip-x");
        var orders = state.Orders!;
        Assert.Equal((1L, false), (orders.Order("o-3")!.Runs, orders.Order("o-3")!.Disabled));
        Assert.True(orders.Order("o-2")!.Disabled);
    }
}
