using System.Text.Json;

namespace CoCern.Adapter.Node;

// Owned by one MCP backend. Candidate validation permits inert discovery; only this process's
// authenticated helper exchange permits invocations and native background work.
internal sealed class NativeRegistrationGate : IAsyncDisposable
{
    private readonly NativeRegistration registration;
    private readonly CancellationTokenSource stopping = new();
    private readonly TaskCompletionSource<bool> accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task listening;

    internal NativeRegistrationGate(NativeRegistration registration, TimeSpan? challengeBudget = null)
    {
        this.registration = registration;
        var budget = challengeBudget ?? NativeRegistration.MaximumWait;
        if (budget <= TimeSpan.Zero || budget > NativeRegistration.MaximumWait) throw new ArgumentOutOfRangeException(nameof(challengeBudget));
        // ListenAsync creates the private pipe before its first await, before the host can serve metadata.
        listening = ListenAsync(budget);
    }

    /// <summary>How long a listener waits, jittered, when every pipe instance of its registration is in use: none frees until a hook drains them or one expires.</summary>
    internal static TimeSpan BusyRetry() => TimeSpan.FromMilliseconds(1000 + Random.Shared.Next(4000));

    private async Task ListenAsync(TimeSpan budget)
    {
        var retryDelay = TimeSpan.FromMilliseconds(10);
        try
        {
            // The loop's own check paces waiting only; acceptance below re-reads the registration in full.
            while (!stopping.IsCancellationRequested && registration.StillCurrentForListening())
            {
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                var busy = false;
                if (await registration.AcceptAsync(stopping.Token, budget, deferUntilHook: true, () => busy = true) is not null && registration.StillCurrent())
                {
                    if (!stopping.IsCancellationRequested) accepted.TrySetResult(true);
                    return;
                }
                // A disconnected, malformed or expired challenge is never authority. Rearm with a fresh nonce.
                // Back off immediate failures instead of spinning; all pipe instances in use waits seconds, since no slot frees sooner than a hook's drain.
                retryDelay = busy ? BusyRetry() : System.Diagnostics.Stopwatch.GetElapsedTime(started) >= TimeSpan.FromMilliseconds(100)
                    ? TimeSpan.FromMilliseconds(50) : TimeSpan.FromMilliseconds(Math.Min(50, retryDelay.TotalMilliseconds * 2));
                await Task.Delay(retryDelay, stopping.Token);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        finally { accepted.TrySetResult(false); }
    }

    internal async Task<bool> WaitForRegistrationAsync(CancellationToken ct) =>
        await accepted.Task.WaitAsync(ct) && !stopping.IsCancellationRequested && registration.StillCurrent();

    internal async Task<AdapterReply> InvokeAsync(NativeJoin join, string tool, JsonElement arguments, CancellationToken ct)
    {
        if (stopping.IsCancellationRequested || !registration.StillCurrent() ||
            !string.Equals(join.PrivateFolder, registration.Folder, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return new(false, "native_registration_unverified");
        try
        {
            // The helper has already run. Allow only the acknowledgement/continuation scheduling race;
            // calls never start the listener and cannot turn metadata into registration evidence.
            if (!await accepted.Task.WaitAsync(TimeSpan.FromMilliseconds(250), ct) || stopping.IsCancellationRequested || !registration.StillCurrent())
                return new(false, "native_registration_unverified");
        }
        catch (TimeoutException) { return new(false, "native_registration_unverified"); }
        return await NativeJoinHook.InvokeAsync(join, registration.Provider, tool, arguments, ct);
    }

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync();
        await listening;
        stopping.Dispose();
    }
}
