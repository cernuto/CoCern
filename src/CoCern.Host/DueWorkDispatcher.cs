using CoCern.Core;
using CoCern.Domain;

namespace CoCern.Host;

internal sealed class DueWorkDispatcher(IDueWorkSource source, RelayApplication application, ILogger<DueWorkDispatcher> logger) : BackgroundService
{
    private readonly DueWorkPump pump = new(source, application.DispatchDueAsync, () => logger.LogWarning("A durable coordination effect remains pending."));
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = TimeSpan.FromMilliseconds(250);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var count = await pump.DispatchNextBatchAsync(DateTimeOffset.UtcNow, stoppingToken);
                backoff = TimeSpan.FromMilliseconds(count == 64 ? 50 : 250);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Durable coordination scan unavailable; retrying."); backoff = TimeSpan.FromSeconds(Math.Min(5, backoff.TotalSeconds * 2)); }
            try { await Task.Delay(backoff, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}

internal sealed class DueWorkPump(IDueWorkSource source, Func<DueWorkReference, CancellationToken, Task> dispatch, Action deliveryFailed)
{
    private int offset;
    public async Task<int> DispatchNextBatchAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var due = await source.ReadDueAsync(now, 64, cancellationToken, offset);
        foreach (var item in due)
        {
            try { await dispatch(item, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { deliveryFailed(); }
        }
        offset = due.Count < 64 || offset > int.MaxValue - due.Count ? 0 : offset + due.Count;
        return due.Count;
    }
}
