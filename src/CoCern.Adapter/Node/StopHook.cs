namespace CoCern.Adapter.Node;

/// <summary>
/// <c>--stop-hook</c>: the Claude Code Stop hook that keeps an idle chat reachable. Installed with <c>asyncRewake</c>, it runs in the
/// background when a turn ends, reads the hook's JSON for the chat's <c>session_id</c> and waits with <see cref="ClaudeWaiter"/> on this
/// PC's node credentials and the chat's session handle. When the hub queues a wake it prints the waiting messages to standard error and
/// exits 2, which wakes the chat with them; the hub queues none for <c>wake: false</c> messages, so those wait for the chat's next turn.
/// Anything else (no chat ID, no node or handle, this client's waiter already running, a timeout, an error) ends quietly with 0.
/// </summary>
public static class StopHook
{
    /// <summary>How long one wait lasts: just under the client's hook timeout, so the waiter stops itself cleanly before the client would kill it.</summary>
    internal static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(Setup.ClientSettings.StopHookTimeoutSeconds) - TimeSpan.FromMinutes(5);
    /// <summary>How long a hook waits for another client's waiter to end. One whose client is gone ends within seconds; past this, that client is still running and keeps the chat's waiter.</summary>
    private static readonly TimeSpan OtherClientGrace = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OtherClientCheck = TimeSpan.FromMilliseconds(500);

    /// <summary>Waits on one chat's inbox: returns "queued", "timeout" or "stopped", and delivers the wake's text before "queued".</summary>
    internal delegate Task<string> Waiter(string nodeFolder, string chat, Action<string> deliver, TimeSpan limit, CancellationToken cancellationToken);

    public static Task<int> RunAsync(TextReader input, TextWriter error, string nodeFolder, CancellationToken cancellationToken) =>
        RunAsync(input, error, nodeFolder, cancellationToken, (folder, chat, deliver, limit, ct) => ClaudeWaiter.RunAsync(folder, chat, deliver, limit, ct, printInbox: true),
            chat => LocalWakeConnection.FindForClaude(chat), OwningProcess.WaitUntilGoneAsync,
            (folder, chat, ct) => ClaudeWaiter.AwaitOtherClientsWaiterAsync(folder, chat, OwningProcess.Read, OtherClientGrace, OtherClientCheck, ct));

    /// <param name="findLocal">
    /// The chat's own local connection folder, when exactly one holds its session (a chat on the hub's PC, signed in with the hub's token file);
    /// the waiter uses it before <paramref name="nodeFolder"/>, so a local chat never needs a node.
    /// </param>
    /// <param name="ownerGone">
    /// Completes when the client that ran this hook is gone. The wait then ends, so a waiter left behind by an app restart neither blocks the new
    /// app's hooks nor spends a wake on the old app. A watch that fails only stops watching.
    /// </param>
    /// <param name="otherClient">Completes once a waiter another client started for this chat has ended, or has had long enough to, so this hook arms in its place.</param>
    internal static async Task<int> RunAsync(TextReader input, TextWriter error, string nodeFolder, CancellationToken cancellationToken, Waiter wait, Func<string, string?>? findLocal = null, Func<CancellationToken, Task>? ownerGone = null, Func<string, string, CancellationToken, Task>? otherClient = null)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var watch = ownerGone is null ? Task.CompletedTask : CancelWhenOwnerGoneAsync(ownerGone, waiting);
        try
        {
            if (await PromptHook.ReadSessionIdAsync(input) is not { } chat || !ChatLocators.IsUuid(chat)) return 0;
            var folder = findLocal?.Invoke(chat) ?? nodeFolder;
            if (otherClient is not null) await otherClient(folder, chat, waiting.Token);
            string? text = null;
            if (await wait(folder, chat, delivered => text = delivered, WaitLimit, waiting.Token) != "queued" || text is null) return 0;
            await error.WriteLineAsync(text);
            return 2;
        }
        catch (Exception) { /* A hook must never trouble the chat: no node, no handle, a running waiter or a failed call all end quietly. */ return 0; }
        finally
        {
            await waiting.CancelAsync();
            await watch;
        }
    }

    private static async Task CancelWhenOwnerGoneAsync(Func<CancellationToken, Task> ownerGone, CancellationTokenSource waiting)
    {
        try
        {
            await ownerGone(waiting.Token);
            await waiting.CancelAsync();
        }
        catch (Exception) { /* Cancelled because the wait ended, or the owner can't be watched: the wait goes on unwatched. */ }
    }
}
