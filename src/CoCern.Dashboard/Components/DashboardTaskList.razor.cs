using System.Diagnostics;
using System.Threading.Channels;
using CoCern.Contracts.Dashboard;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace CoCern.Dashboard.Components;

public partial class DashboardTaskList : ComponentBase, IAsyncDisposable
{
    [Inject] private NavigationManager Navigation { get; set; } = null!;
    [Parameter, EditorRequired] public string Repository { get; set; } = "";
    [Parameter, EditorRequired] public string Board { get; set; } = "";
    [Parameter] public DashboardSession[] Sessions { get; set; } = [];
    private readonly Dictionary<string, string> sessionLabels = new(StringComparer.Ordinal);

    protected override void OnParametersSet()
    {
        sessionLabels.Clear();
        foreach (var session in Sessions.Where(session => session.RepositoryId == Repository && !string.IsNullOrWhiteSpace(session.Label)))
            sessionLabels[session.SessionId] = session.Label;
    }

    private string SessionLabel(string? id) => string.IsNullOrEmpty(id) ? "Unknown" : sessionLabels.GetValueOrDefault(id, id);
    private readonly CancellationTokenSource lifetime = new();
    private readonly SortedDictionary<string, DashboardBoardRow> rows = new(StringComparer.Ordinal);
    private string? expandedTask;
    private HubConnection? connection;

    private void ToggleTask(string taskId) => expandedTask = expandedTask == taskId ? null : taskId;

    private string NextActor(DashboardBoardRow row)
    {
        if (row.BlockerKind == "human_decision")
        {
            if (!row.HumanDecisionsAnswered) return "Paul";
            return string.IsNullOrEmpty(row.NextActor) ? "—" : SessionLabel(row.NextActor);
        }
        if (row.WorkflowState is "completed" or "blocked") return "—";
        if (row.LeaseUntil is { } until && until > sampledAt + sampleAge.Elapsed && !string.IsNullOrEmpty(row.Owner)) return SessionLabel(row.Owner);
        return string.IsNullOrWhiteSpace(row.NextActor) ? "—" : SessionLabel(row.NextActor);
    }
    private string? notice = "Loading board…";
    private string? instance;
    private long revision;
    private Task? runner;
    private static readonly TimeSpan RecoveryInterval = TimeSpan.FromMinutes(5);
    private DateTimeOffset sampledAt;
    private readonly Stopwatch sampleAge = new();

    private string ClaimLabel(DashboardBoardRow row)
    {
        if (string.IsNullOrEmpty(row.Owner)) return "Unassigned";
        if (row.LeaseUntil is { } until ? until > sampledAt + sampleAge.Elapsed : row.LeaseActive) return SessionLabel(row.Owner);
        return row.UnresolvedActions == true
            ? $"Lease expired · {SessionLabel(row.Owner)} · actions pending" : $"Lease expired · {SessionLabel(row.Owner)}";
    }

    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender) runner = WatchAsync(lifetime.Token);
    }

    private async Task WatchAsync(CancellationToken token)
    {
        var retrySeconds = 2;
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var hub = new HubConnectionBuilder().WithUrl(Navigation.ToAbsoluteUri("hub/boards")).Build();
                    var changes = Channel.CreateBounded<bool>(1);
                    long highestHint = 0;
                    using var subscription = hub.On<long>("BoardChanged", value =>
                    {
                        // A late older broadcast must not replace a newer hint while a page is in flight.
                        lock (changes) highestHint = Math.Max(highestHint, value);
                        changes.Writer.TryWrite(true);
                    });
                    hub.Closed += _ => InvokeAsync(() =>
                    {
                        // An expanded task must not keep calling a connection that has ended.
                        if (ReferenceEquals(connection, hub)) connection = null;
                        changes.Writer.TryComplete();
                        if (token.IsCancellationRequested) return;
                        notice = "Board connection interrupted. Reconnecting…";
                        StateHasChanged();
                    });
                    await hub.StartAsync(token);
                    // Subscribe before the initial read. A change arriving during paging remains queued for the next reconciliation.
                    var connectedInstance = await hub.InvokeAsync<string>("WatchBoard", Repository, Board, token);
                    connection = hub;
                    if (instance != connectedInstance)
                    {
                        revision = 0;
                        instance = connectedInstance;
                    }
                    while (hub.State == HubConnectionState.Connected)
                    {
                        await ReconcileAsync(hub, token);
                        if (hub.State != HubConnectionState.Connected) break;
                        notice = null;
                        retrySeconds = 2;
                        using var recovery = CancellationTokenSource.CreateLinkedTokenSource(token);
                        recovery.CancelAfter(RecoveryInterval);
                        var leaseDisplay = RenderAtLeaseExpirationsAsync(recovery.Token);
                        try
                        {
                            // Duplicate hints already covered by the committed page need no additional read.
                            while (await changes.Reader.WaitToReadAsync(recovery.Token))
                            {
                                changes.Reader.TryRead(out _);
                                lock (changes) if (highestHint > revision) break;
                            }
                        }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                        finally
                        {
                            await recovery.CancelAsync();
                            await leaseDisplay;
                        }
                    }
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    if (ex is Microsoft.AspNetCore.SignalR.HubException
                        or HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized })
                    {
                        rows.Clear();
                        revision = 0;
                    }
                }
                // The connection of this pass is disposed by now, however the pass ended.
                connection = null;
                token.ThrowIfCancellationRequested();
                notice = "Board unavailable. Check the repository and board. Retrying automatically…";
                StateHasChanged();
                await Task.Delay(TimeSpan.FromSeconds(retrySeconds), token);
                retrySeconds = Math.Min(60, retrySeconds * 2);
            }
        }
        // A disposed transport may surface a closed-channel error while navigation cancels the component.
        catch (Exception) when (token.IsCancellationRequested) { }
    }

    private async Task ReconcileAsync(HubConnection hub, CancellationToken token)
    {
        var since = revision;
        // A concurrently changing board may invalidate a paged read. Keep the last complete view until a replacement is ready.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            List<DashboardBoardRow> changes = [];
            long? snapshot = null;
            var cursor = 0;
            while (true)
            {
                var page = await hub.InvokeAsync<DashboardBoardPage>("ReadChanges", since, snapshot, cursor, token);
                if (page.Restart) { since = 0; break; }
                snapshot ??= page.Revision;
                if (page.Revision != snapshot || page.NextCursor <= cursor) throw new InvalidOperationException("Invalid board continuation.");
                changes.AddRange(page.Items);
                if (page.NextCursor is { } next) { cursor = next; continue; }
                if (since == 0) rows.Clear();
                foreach (var row in changes)
                {
                    if (row.Archived) rows.Remove(row.TaskId);
                    else rows[row.TaskId] = row;
                }
                revision = page.Revision;
                sampledAt = page.SampledAt ?? DateTimeOffset.UtcNow;
                sampleAge.Restart();
                return;
            }
        }
        throw new InvalidOperationException("Board changed during paging.");
    }

    private async Task RenderAtLeaseExpirationsAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var now = sampledAt + sampleAge.Elapsed;
                StateHasChanged();
                var next = rows.Values.Select(row => row.LeaseUntil).Where(until => until > now).Min();
                if (next is null || next.Value - now > RecoveryInterval) return;
                // Round up so a sub-millisecond remaining lease cannot spin the renderer.
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling((next.Value - now).TotalMilliseconds)), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        if (runner is not null) await runner;
        lifetime.Dispose();
    }
}
