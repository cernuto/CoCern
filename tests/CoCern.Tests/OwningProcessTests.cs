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
    private const string Chat = "0b6a2f4e-3c1d-4e5f-8a9b-1c2d3e4f5a6b";
    private static readonly DateTime T0 = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    private static Func<int, ProcessFacts?> Tree(params ProcessFacts[] processes) =>
        id => processes.FirstOrDefault(process => process.Id == id);

    [Theory]
    [InlineData("claude.exe")]
    [InlineData("Claude")]
    [InlineData("node")]
    public void The_chain_runs_from_the_parent_up_to_and_including_the_client(string client)
    {
        var read = Tree(
            new(5, 4, "CoCern.Adapter", T0.AddSeconds(4)), new(4, 3, "bash", T0.AddSeconds(3)), new(3, 2, "bash", T0.AddSeconds(2)),
            new(2, 1, client, T0.AddSeconds(1)), new(1, 0, "explorer", T0));
        Assert.Equal(new[] { 4, 3, 2 }, OwningProcess.Chain(5, read).Select(process => process.Id));
    }

    [Fact]
    public void Without_a_known_client_the_chain_is_every_ancestor_it_can_read()
    {
        var read = Tree(new(3, 2, "CoCern.Adapter", T0.AddSeconds(2)), new(2, 1, "sh", T0.AddSeconds(1)), new(1, 99, "init", T0));
        Assert.Equal(new[] { 2, 1 }, OwningProcess.Chain(3, read).Select(process => process.Id));
    }

    [Fact]
    public void A_reused_process_id_younger_than_its_child_is_not_an_ancestor()
    {
        var read = Tree(new(3, 2, "CoCern.Adapter", T0), new(2, 1, "unrelated", T0.AddMinutes(1)));
        Assert.Empty(OwningProcess.Chain(3, read));
    }

    [Fact]
    public void A_process_tree_with_a_parent_loop_ends()
    {
        var read = Tree(new(3, 2, "CoCern.Adapter", T0.AddSeconds(1)), new(2, 2, "loop", T0));
        Assert.Equal(new[] { 2 }, OwningProcess.Chain(3, read).Select(process => process.Id));
    }

    [Fact]
    public void The_client_of_a_process_is_the_top_of_its_chain_only_when_that_is_a_client()
    {
        var read = Tree(new(5, 4, "CoCern.Adapter", T0.AddSeconds(4)), new(4, 2, "bash", T0.AddSeconds(3)), new(2, 1, "claude", T0.AddSeconds(1)), new(1, 0, "explorer", T0));
        Assert.Equal(2, OwningProcess.ClientOf(5, read)?.Id);
        // The client has exited, so its parent can no longer be read: there is no client to name.
        Assert.Null(OwningProcess.ClientOf(5, Tree(new(5, 4, "CoCern.Adapter", T0.AddSeconds(4)), new(4, 2, "bash", T0.AddSeconds(3)))));
        Assert.Null(OwningProcess.ClientOf(7, read));
    }

    [Fact]
    public async Task Waiting_ends_when_any_process_in_the_chain_is_gone()
    {
        var chain = new ProcessFacts[] { new(4, 3, "bash", T0), new(3, 2, "claude", T0) };
        var gone = new HashSet<int>();
        var waiting = OwningProcess.WaitUntilGoneAsync(chain, process => !gone.Contains(process.Id), TimeSpan.FromMilliseconds(10), CancellationToken.None);
        await Task.Delay(50);
        Assert.False(waiting.IsCompleted);
        gone.Add(3);
        await waiting.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task An_empty_chain_waits_until_cancelled_so_the_hook_behaves_as_it_did_without_a_watch()
    {
        using var stop = new CancellationTokenSource();
        var waiting = OwningProcess.WaitUntilGoneAsync([], _ => false, TimeSpan.FromMilliseconds(10), stop.Token);
        await Task.Delay(50);
        Assert.False(waiting.IsCompleted);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task A_real_child_reads_this_process_as_its_parent_and_is_seen_to_die()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c ping -n 60 127.0.0.1") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true }
            : new ProcessStartInfo("sleep", "60") { UseShellExecute = false };
        using var child = Process.Start(start)!;
        try
        {
            var facts = OwningProcess.Read(child.Id);
            Assert.NotNull(facts);
            Assert.Equal(Environment.ProcessId, facts.ParentId);
            Assert.True(OwningProcess.IsAlive(facts));
            // Same ID, different start: a reused process ID is not the process that was recorded.
            Assert.False(OwningProcess.IsAlive(facts with { StartedUtc = facts.StartedUtc.AddHours(-1) }));
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
            await OwningProcess.WaitUntilGoneAsync([facts], OwningProcess.IsAlive, TimeSpan.FromMilliseconds(20), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task The_hook_ends_quietly_and_frees_the_chat_when_its_owner_is_gone()
    {
        var error = new StringWriter();
        var owner = new TaskCompletionSource();
        var waiting = new TaskCompletionSource();
        var cancelled = false;
        var hook = StopHook.RunAsync(new StringReader($"{{ \"session_id\": \"{Chat}\" }}"), error, "node", CancellationToken.None, async (_, _, _, _, ct) =>
        {
            waiting.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled = true; throw; }
            return "timeout";
        }, null, _ => owner.Task);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(hook.IsCompleted);
        owner.SetResult();
        Assert.Equal(0, await hook.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(cancelled);
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public async Task A_wake_still_exits_2_and_stops_the_owner_watch()
    {
        var error = new StringWriter();
        var watchStopped = false;
        var code = await StopHook.RunAsync(new StringReader($"{{ \"session_id\": \"{Chat}\" }}"), error, "node", CancellationToken.None, (_, _, deliver, _, _) =>
        {
            deliver("hello");
            return Task.FromResult("queued");
        }, null, async ct =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { watchStopped = true; throw; }
        });
        Assert.Equal(2, code);
        Assert.True(watchStopped);
        Assert.Contains("hello", error.ToString());
    }

    [Fact]
    public async Task A_watch_that_fails_leaves_the_waiter_running()
    {
        var error = new StringWriter();
        var code = await StopHook.RunAsync(new StringReader($"{{ \"session_id\": \"{Chat}\" }}"), error, "node", CancellationToken.None, async (_, _, deliver, _, ct) =>
        {
            await Task.Delay(100, ct);
            deliver("hello");
            return "queued";
        }, null, _ => throw new InvalidOperationException("this platform cannot say"));
        Assert.Equal(2, code);
    }
}
