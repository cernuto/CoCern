namespace CoCern.Adapter.Node;

internal sealed class NodeDispatchGate
{
    private readonly object sync = new();
    private int active;
    private bool closed;
    private TaskCompletionSource? drained;

    public IDisposable? TryEnter()
    {
        lock (sync)
        {
            if (closed) return null;
            active++;
            return new Lease(this);
        }
    }

    public async Task<bool> TryDrainAsync(TimeSpan timeout, CancellationToken ct)
    {
        Task wait;
        lock (sync)
        {
            if (closed) throw new InvalidOperationException("node-dispatch-already-draining");
            closed = true;
            drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (active == 0) drained.TrySetResult();
            wait = drained.Task;
        }
        try
        {
            ct.ThrowIfCancellationRequested();
            await wait.WaitAsync(timeout, ct);
            return true;
        }
        catch (TimeoutException) { Resume(); return false; }
        catch { Resume(); throw; }
    }

    public void Resume()
    {
        lock (sync) { closed = false; drained = null; }
    }

    private void Exit()
    {
        lock (sync)
        {
            if (--active == 0) drained?.TrySetResult();
        }
    }

    private sealed class Lease(NodeDispatchGate owner) : IDisposable
    {
        private NodeDispatchGate? owner = owner;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Exit();
    }
}
