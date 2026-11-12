using System.Diagnostics;
using System.Net.Http.Json;
using CoCern.Contracts;
using CoCern.Dashboard.Services;
using Microsoft.AspNetCore.Components;

namespace CoCern.Dashboard.Components;

/// <summary>Bounds active-view reads to fresh dashboard pulses and one request at a time.</summary>
public abstract class LiveDashboardView : ComponentBase, IDisposable
{
    [Inject] protected HttpClient Http { get; set; } = default!;
    [Inject] protected DashboardConnection Connection { get; set; } = default!;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private TimeSpan? _lastAttempt;
    private CancellationTokenSource? _pending;
    private long _generation;
    private bool _disposed;

    protected bool Loading { get; private set; }
    protected string? Error { get; set; }
    protected DateTimeOffset? LastUpdated { get; private set; }
    protected string[] RepositoryInventory { get; private set; } = [];
    protected bool RepositoryInventoryLoaded { get; private set; }
    protected bool RepositoryInventoryTruncated { get; private set; }
    protected DateTimeOffset? RepositoryInventoryAt { get; private set; }
    protected string? RepositoryInventoryError { get; private set; }
    protected bool CanRead => !_disposed && Connection.Authenticated && Connection.State == DashboardLinkState.Live && Connection.ObservationsFresh;
    protected string FreshnessLabel => !CanRead ? "Updates paused" : RepositoryInventoryError is not null ? "Repository list unavailable" : LastUpdated is null ? "Waiting for a sample" : Error is not null ? "Last sample retained" : "Live";
    protected string FreshnessDescription => !CanRead ? "The hub connection is stale or disconnected. Reads resume with fresh updates." : "Refreshes automatically while this view is open, at most once every five seconds.";

    protected override async Task OnInitializedAsync()
    {
        Connection.Changed += ConnectionChanged;
        InitializeSelection();
        await RefreshAsync(true);
    }

    protected virtual void InitializeSelection() { }
    protected abstract Task ReadAsync(CancellationToken cancellationToken, long generation);
    protected bool IsCurrent(long generation) => !_disposed && generation == _generation && CanRead;

    protected async Task<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanRead) throw new OperationCanceledException();
        using var response = await Http.GetAsync(path, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
    }

    protected async Task<bool> ReadRepositoryInventoryAsync(CancellationToken cancellationToken, long generation)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanRead) throw new OperationCanceledException();
        try
        {
            using var response = await Http.GetAsync("api/retained-context/repositories", cancellationToken);
            var reply = await response.Content.ReadFromJsonAsync<DashboardRetainedContextReply<DashboardRetainedContextRepositories>>(cancellationToken);
            if (!IsCurrent(generation)) return false;
            if (reply is not { Ok: true, Data: not null })
            {
                RepositoryInventoryError = $"The authorized repository list is unavailable ({reply?.Code ?? "unknown response"}). A fresh hub update will retry.";
                return false;
            }
            RepositoryInventory = reply.Data.Items.Where(repository => !string.IsNullOrWhiteSpace(repository))
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
            RepositoryInventoryLoaded = true;
            RepositoryInventoryTruncated = reply.Data.Truncated;
            RepositoryInventoryAt = reply.SampledAt;
            RepositoryInventoryError = null;
            return true;
        }
        catch (Exception exception) when ((exception is HttpRequestException or System.Text.Json.JsonException or OperationCanceledException) && !cancellationToken.IsCancellationRequested)
        {
            if (IsCurrent(generation)) RepositoryInventoryError = "The authorized repository list could not be loaded. A fresh hub update will retry; any previous list may be stale.";
            return false;
        }
    }

    protected void ResetSample()
    {
        LastUpdated = null;
        Error = null;
    }

    protected async Task RefreshAsync(bool force = false)
    {
        if (!CanRead) return;
        if (!force && (Loading || _lastAttempt is { } last && _clock.Elapsed - last < TimeSpan.FromSeconds(5))) return;

        _pending?.Cancel();
        var pending = _pending = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = pending.Token;
        var generation = ++_generation;
        _lastAttempt = _clock.Elapsed;
        Loading = true;
        Error = null;
        var entered = false;
        try
        {
            await _readGate.WaitAsync(token);
            entered = true;
            if (!IsCurrent(generation)) return;
            await ReadAsync(token, generation);
            if (IsCurrent(generation) && Error is null && RepositoryInventoryError is null) LastUpdated = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (IsCurrent(generation)) Error = "This read timed out. A fresh hub update will retry.";
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or OperationCanceledException)
        {
            if (IsCurrent(generation)) Error = "This view could not be refreshed. Check the hub connection; another fresh update will retry.";
        }
        finally
        {
            if (entered) _readGate.Release();
            if (!_disposed && generation == _generation)
            {
                Loading = false;
                _pending = null;
                StateHasChanged();
            }
            pending.Dispose();
        }
    }

    private void ConnectionChanged()
    {
        if (_disposed) return;
        _ = InvokeAsync(async () =>
        {
            if (_disposed) return;
            if (!CanRead)
            {
                ++_generation;
                _pending?.Cancel();
                _pending = null;
                Loading = false;
            }
            else await RefreshAsync();
            if (!_disposed) StateHasChanged();
        });
    }

    public virtual void Dispose()
    {
        _disposed = true;
        ++_generation;
        Connection.Changed -= ConnectionChanged;
        _pending?.Cancel();
        _pending = null;
    }
}
