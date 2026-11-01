using System.Collections.Concurrent;
using System.Security.Claims;
using System.Threading.Channels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using CoCern.Contracts.Dashboard;

namespace CoCern.Host.Dashboard;

[Authorize(AuthenticationSchemes = DashboardSecurity.Scheme, Policy = DashboardSecurity.Policy)]
public sealed class DashboardHub(DashboardCollector collector, DashboardConnections connections, DashboardSecurity security) : Hub
{
    public DashboardSnapshot GetSnapshot()
    {
        if (!security.IsValid(Context.User, DateTimeOffset.UtcNow)) { Context.Abort(); throw new HubException("Sign-in is required."); }
        return collector.Current;
    }

    public override Task OnConnectedAsync()
    {
        if (!security.IsValid(Context.User, DateTimeOffset.UtcNow) || !connections.Add(Context, collector.Current)) Context.Abort();
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}

/// <summary>One latest-value slot per viewer; a slow viewer cannot queue collector frames.</summary>
public sealed class DashboardConnections(IHubContext<DashboardHub> hub, DashboardSecurity security, IHostApplicationLifetime lifetime)
{
    private readonly ConcurrentDictionary<string, Viewer> viewers = new();
    private readonly object connectionGate = new();
    private readonly SemaphoreSlim viewerAvailable = new(0, 1);

    public bool HasViewers => !viewers.IsEmpty;

    public async Task WaitForViewerAsync(CancellationToken cancellationToken)
    {
        while (!HasViewers) await viewerAvailable.WaitAsync(cancellationToken);
    }

    public bool Add(HubCallerContext context, DashboardSnapshot initial)
    {
        Viewer viewer;
        lock (connectionGate)
        {
            if (viewers.Count >= 64) return false;
            viewer = new Viewer(context, CancellationTokenSource.CreateLinkedTokenSource(context.ConnectionAborted, lifetime.ApplicationStopping));
            if (!viewers.TryAdd(context.ConnectionId, viewer)) { viewer.Cancellation.Dispose(); return false; }
            viewer.Frames.Writer.TryWrite(initial);
            if (viewerAvailable.CurrentCount == 0) viewerAvailable.Release();
        }
        _ = SendAsync(viewer);
        return true;
    }

    public void Remove(string id)
    {
        if (viewers.TryRemove(id, out var viewer))
        {
            // The sender alone disposes the source; it may already have completed after removal.
            try { viewer.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    public void Publish(DashboardSnapshot snapshot)
    {
        foreach (var viewer in viewers.Values)
            viewer.Frames.Writer.TryWrite(snapshot);
    }

    private async Task SendAsync(Viewer viewer)
    {
        try
        {
            await foreach (var snapshot in viewer.Frames.Reader.ReadAllAsync(viewer.Cancellation.Token))
            {
                if (!security.IsValid(viewer.Context.User, DateTimeOffset.UtcNow)) break;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(viewer.Cancellation.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await hub.Clients.Client(viewer.Context.ConnectionId).SendAsync("snapshot", snapshot, timeout.Token);
            }
        }
        catch (Exception) { /* Transport failure is isolated from relay work and never exported as raw text. */ }
        finally
        {
            viewers.TryRemove(viewer.Context.ConnectionId, out _);
            viewer.Context.Abort();
            viewer.Cancellation.Dispose();
        }
    }

    private sealed record Viewer(HubCallerContext Context, CancellationTokenSource Cancellation)
    {
        public Channel<DashboardSnapshot> Frames { get; } = Channel.CreateBounded<DashboardSnapshot>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
    }
}

