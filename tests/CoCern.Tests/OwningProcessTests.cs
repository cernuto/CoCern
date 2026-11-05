using System.Diagnostics;
using CoCern.Adapter.Node;
using Xunit;

namespace CoCern.Tests;

/// <summary>
/// A Stop hook's waiter outlives the app that started it when the app restarts. Left running, it blocks the new app's hooks, since one
/// waiter may run per chat, and then spends the next wake on an app that is gone. So it ends when the process tree that started it does.
/// </summary>
public sealed class OwningProcessTests
{
    private static readonly DateTime T0 = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    private static Func<int, ProcessFacts?> Tree(params ProcessFacts[] processes) =>
        id => processes.FirstOrDefault(process => process.Id == id);

    [Theory]
    [InlineData("claude.exe")]
    public void The_chain_runs_from_the_parent_up_to_and_including_the_client(string client)
    {
        var read = Tree(
            new(5, 4, "CoCern.Adapter", T0.AddSeconds(4)), new(4, 3, "bash", T0.AddSeconds(3)), new(3, 2, "bash", T0.AddSeconds(2)),
            new(2, 1, client, T0.AddSeconds(1)), new(1, 0, "explorer", T0));
        Assert.Equal(new[] { 4, 3, 2 }, OwningProcess.Chain(5, read).Select(process => process.Id));
    }

    [Fact]
    public void A_reused_process_id_younger_than_its_child_is_not_an_ancestor()
    {
        var read = Tree(new(3, 2, "CoCern.Adapter", T0), new(2, 1, "unrelated", T0.AddMinutes(1)));
        Assert.Empty(OwningProcess.Chain(3, read));
    }
}
