using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using PkRelay.Core;

namespace PkRelay.Host;

[Authorize]
public sealed class CoordinationHub(IRelayApplication application) : Hub
{
    public async Task WatchSession(string sessionId, string sessionToken, string repositoryId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(sessionToken) || string.IsNullOrWhiteSpace(repositoryId) || sessionId.Length > 256 || sessionToken.Length > 256 || repositoryId.Length > 256) throw new HubException("Session access denied.");
        var caller = RelayAuthentication.Caller(Context.User!);
        var reply = await application.ExecuteAsync(caller, "get_status", JsonSerializer.SerializeToElement(new { repositoryId, sessionId, sessionToken }), Context.ConnectionAborted);
        if (!reply.Ok) throw new HubException("Session access denied.");
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(sessionId), Context.ConnectionAborted);
    }

    internal static string Group(string sessionId) => "session:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)));
}

internal sealed class CoordinationNotifier(IHubContext<CoordinationHub> hub) : ICoordinationNotifier
{
    public Task NotifyAsync(string sessionId, string eventId, CancellationToken cancellationToken = default) => hub.Clients.Group(CoordinationHub.Group(sessionId)).SendAsync("coordination.changed", new { sessionId, eventId }, cancellationToken);
}

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
