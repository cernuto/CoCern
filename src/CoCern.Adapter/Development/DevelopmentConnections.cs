using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace CoCern.Adapter.Development;

internal interface IDevelopmentBackend : IAsyncDisposable
{
    bool IsClosed { get; }
    string? Instructions { get; }
    ValueTask<ListToolsResult> ListToolsAsync(ListToolsRequestParams request, CancellationToken cancellationToken);
    ValueTask<CallToolResult> CallToolAsync(CallToolRequestParams request, CancellationToken cancellationToken);
}

internal sealed class DevelopmentConnections(string root, Func<AdapterManifest, CancellationToken, Task<IDevelopmentBackend>> connect, Action<string>? report = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly object calls = new();
    private IDevelopmentBackend? backend;
    private AdapterManifest? active;
    private int inFlight;
    private TaskCompletionSource drained = Completed();
    private bool disposed;
    public string? Instructions => backend?.Instructions;

    public async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try { ObjectDisposedException.ThrowIf(disposed, this); return await RefreshCoreAsync(cancellationToken); }
        finally { gate.Release(); }
    }

    public async ValueTask<T> UseAsync<T>(Func<IDevelopmentBackend, CancellationToken, ValueTask<T>> operation, CancellationToken cancellationToken = default)
    {
        IDevelopmentBackend selected;
        await gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await RefreshCoreAsync(cancellationToken);
            selected = backend!;
            lock (calls)
            {
                if (inFlight++ == 0) drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        finally { gate.Release(); }
        try { return await operation(selected, cancellationToken); }
        finally { lock (calls) { if (--inFlight == 0) drained.TrySetResult(); } }
    }

    private async Task<bool> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var desired = AdapterDeployment.Read(root);
            if (desired == active && backend is { IsClosed: false }) return false;
            Task pending;
            lock (calls) pending = drained.Task;
            // New calls queue at gate, while existing operations finish exactly once on their original backend.
            await pending.WaitAsync(cancellationToken);
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(TimeSpan.FromSeconds(15));
            var replacement = await connect(desired, startup.Token);
            var previous = backend;
            (backend, active) = (replacement, desired);
            if (previous is not null)
            {
                try { await previous.DisposeAsync(); }
                catch (Exception error) when (error is IOException or InvalidOperationException) { report?.Invoke("The previous development adapter did not shut down cleanly."); }
            }
            report?.Invoke($"Development adapter connected: {desired.Version[..12]}, generation {desired.Generation}.");
            return true;
        }
        catch (Exception error) when (backend is not null && !cancellationToken.IsCancellationRequested && error is IOException or UnauthorizedAccessException or McpException or InvalidOperationException or OperationCanceledException)
        {
            report?.Invoke("Development adapter refresh failed; keeping the previous connection.");
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true;
            Task pending;
            lock (calls) pending = drained.Task;
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { }
            if (backend is not null) await backend.DisposeAsync();
        }
        finally { gate.Release(); }
    }

    private static TaskCompletionSource Completed()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetResult(); return completion;
    }
}
