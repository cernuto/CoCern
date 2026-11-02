using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using CoCern.Contracts.Dashboard;

namespace CoCern.Dashboard.Services;

public enum DashboardLinkState { Connecting, Live, Reconnecting, SignIn }

/// <summary>Read-only, same-origin observer transport. Retains only complete sanitized snapshots.</summary>
public sealed class DashboardConnection(HttpClient http, NavigationManager navigation) : IAsyncDisposable
{
    private CancellationTokenSource? _connectionLifetime;
    private Task? _runner;
    private readonly HashSet<string> _activitySessions = [];
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly DashboardObservationClock _observations = new();
    private DateTimeOffset _pulseAt;
    private bool _disposed;

    public event Action? Changed;
    public DashboardSnapshot? Snapshot { get; private set; }
    public DashboardLinkState State { get; private set; } = DashboardLinkState.Connecting;
    public bool Authenticated { get; private set; }
    public string? PrincipalId { get; private set; }
    public string? Notice { get; private set; }
    public DateTimeOffset? ReceivedAt { get; private set; }
    public bool ObservationsConfirmed => _observations.Confirmed;
    public bool ObservationsFresh => _observations.IsFresh(_elapsed.Elapsed, TimeSpan.FromSeconds(12));
    public DateTimeOffset ObservationNow => _observations.ServerNow(_elapsed.Elapsed) ?? DateTimeOffset.UtcNow;

    public void Start()
    {
        if (_disposed || _runner is { IsCompleted: false }) return;
        _connectionLifetime?.Dispose();
        _connectionLifetime = new CancellationTokenSource();
        _runner = RunAsync(_connectionLifetime.Token);
    }

    public bool IsPulsing(string sessionId, DateTimeOffset now) =>
        State == DashboardLinkState.Live && now - _pulseAt < TimeSpan.FromMilliseconds(1400) && _activitySessions.Contains(sessionId);

    public Task<bool> LoginAsync(string token) => PostLoginAsync(new { token }, "This credential does not have dashboard access.");

    /// <summary>Signs in with a one-time link from the hub's log, then connects; a spent or expired link leaves the sign-in form saying so.</summary>
    public async Task StartWithLinkAsync(string link)
    {
        if (!await PostLoginAsync(new { link }, "This sign-in link was already used or has expired. The hub logs a new one each time one is used, and at every start.")) { _signInNotice = Notice; Start(); }
    }

    private string? _signInNotice;

    private async Task<bool> PostLoginAsync(object body, string refused)
    {
        Notice = null;
        try
        {
            using var response = await http.PostAsJsonAsync("api/login", body);
            if (!response.IsSuccessStatusCode)
            {
                Notice = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? refused
                    : "Sign-in is unavailable. Check the relay connection and try again.";
                Changed?.Invoke();
                return false;
            }
            State = DashboardLinkState.Connecting;
            Start();
            Changed?.Invoke();
            return true;
        }
        catch (HttpRequestException)
        {
            Notice = "Can't reach the relay. Check your connection and try again.";
            Changed?.Invoke();
            return false;
        }
        catch (TaskCanceledException)
        {
            Notice = "Sign-in timed out. Try again.";
            Changed?.Invoke();
            return false;
        }
    }

    public async Task LogoutAsync()
    {
        try
        {
            using var response = await http.PostAsync("api/logout", null);
            if (!response.IsSuccessStatusCode)
            {
                Notice = "Sign-out is unavailable. Try again.";
                Changed?.Invoke();
                return;
            }
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            Notice = "Can't reach the relay to sign out. Try again.";
            Changed?.Invoke();
            return;
        }
        await StopAsync();
        RequireSignIn();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var authResponse = await http.GetAsync("api/auth", cancellationToken);
                if (authResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    RequireSignIn();
                    return;
                }
                authResponse.EnsureSuccessStatusCode();
                var auth = await authResponse.Content.ReadFromJsonAsync<DashboardAuthStatus>(cancellationToken);
                if (auth?.Authenticated != true)
                {
                    RequireSignIn();
                    return;
                }
                Authenticated = true;
                PrincipalId = auth.PrincipalId;
                State = Snapshot is null ? DashboardLinkState.Connecting : DashboardLinkState.Reconnecting;
                Notice = null;
                Changed?.Invoke();

                await using var hub = new HubConnectionBuilder()
                    .WithUrl(navigation.ToAbsoluteUri("hub"))
                    .Build();
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                hub.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
                using var subscription = hub.On<DashboardSnapshot>("snapshot", AcceptSnapshot);
                await hub.StartAsync(cancellationToken);
                AcceptSnapshot(await hub.InvokeAsync<DashboardSnapshot>("GetSnapshot", cancellationToken));
                State = DashboardLinkState.Live;
                Notice = null;
                Changed?.Invoke();
                await closed.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // Transport and server details are intentionally never shown to a viewer.
            }
            if (cancellationToken.IsCancellationRequested) return;
            State = DashboardLinkState.Reconnecting;
            _activitySessions.Clear();
            Notice = "Connection interrupted. Retrying automatically…";
            Changed?.Invoke();
            try { await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void AcceptSnapshot(DashboardSnapshot incoming)
    {
        if (!_observations.Accept(incoming.InstanceId, incoming.Revision, incoming.ObservedAt, _elapsed.Elapsed)) return;
        _activitySessions.Clear();
        if (Snapshot is { } previous && previous.InstanceId == incoming.InstanceId)
        {
            var priorActivity = previous.Sessions.ToDictionary(session => session.SessionId, session => session.LastActivityAt);
            foreach (var session in incoming.Sessions)
            {
                if (session.LastActivityAt is { } activity && priorActivity.TryGetValue(session.SessionId, out var prior) && (prior is null || activity > prior))
                    _activitySessions.Add(session.SessionId);
            }
            var latestTraffic = previous.Traffic.Select(entry => entry.Sequence).DefaultIfEmpty().Max();
            foreach (var entry in incoming.Traffic.Where(entry => entry.Sequence > latestTraffic && entry.SessionId is not null))
                _activitySessions.Add(entry.SessionId!);
        }
        Snapshot = incoming;
        ReceivedAt = DateTimeOffset.UtcNow;
        _pulseAt = ReceivedAt.Value;
        Changed?.Invoke();
    }

    private void RequireSignIn()
    {
        Authenticated = false;
        PrincipalId = null;
        Snapshot = null;
        ReceivedAt = null;
        _observations.Reset();
        _activitySessions.Clear();
        State = DashboardLinkState.SignIn;
        Notice = _signInNotice;
        _signInNotice = null;
        Changed?.Invoke();
    }

    private async Task StopAsync()
    {
        if (_connectionLifetime is null) return;
        await _connectionLifetime.CancelAsync();
        if (_runner is not null) await _runner;
        _connectionLifetime.Dispose();
        _connectionLifetime = null;
        _runner = null;
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await StopAsync();
    }
}

