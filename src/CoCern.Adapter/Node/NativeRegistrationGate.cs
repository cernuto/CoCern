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

    private async Task ListenAsync(TimeSpan budget)
    {
        var retryDelay = TimeSpan.FromMilliseconds(10);
        try
        {
            while (!stopping.IsCancellationRequested && registration.StillCurrent())
            {
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                if (await registration.AcceptAsync(stopping.Token, budget, deferUntilHook: true) is not null && registration.StillCurrent())
                {
                    if (!stopping.IsCancellationRequested) accepted.TrySetResult(true);
                    return;
                }
                // A disconnected, malformed or expired challenge is never authority. Rearm with a fresh nonce.
                // Back off immediate failures (including all four pipe slots being occupied) instead of spinning.
                // Keep a waiting fifth backend inside the genuine helper's 250ms drain window after a slot is freed.
                retryDelay = System.Diagnostics.Stopwatch.GetElapsedTime(started) >= TimeSpan.FromMilliseconds(100)
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
