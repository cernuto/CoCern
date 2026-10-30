using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using PkRelay.Contracts.Dashboard;
using PkRelay.Dashboard.Services;

namespace PkRelay.Dashboard.Pages;

public partial class Dashboard : IAsyncDisposable
{
    [Inject] private DashboardConnection Connection { get; set; } = null!;
    [Inject] private NavigationManager Navigation { get; set; } = null!;
    [Inject] private IJSRuntime JavaScript { get; set; } = null!;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _clock;
    private DateTimeOffset _now => Connection.ObservationNow;
    private string? _selectedId;
    private string _theme = "light";
    private bool _motion = true;
    private bool _signingOut;

    private string View => Navigation.ToBaseRelativePath(Navigation.Uri).Split('?', '#')[0].Trim('/').ToLowerInvariant() switch
    {
        "sessions" => "sessions",
        "sql" => "sql",
        "logs" => "logs",
        _ => "overview"
    };
    private string PageTitle => View switch { "sessions" => "Session watch", "sql" => "Inside the database", "logs" => "Signals from the relay", _ => "Relay at a glance" };
    private string PageDescription => View switch
    {
        "sessions" => "Find a session and inspect its latest reported state.",
        "sql" => "Real tables. Reported counts. A little more visibility.",
        "logs" => "A quiet record of recent operational events.",
        _ => "A place for every machine. A signal for every session."
    };
    private bool IsLive => Connection.State == DashboardLinkState.Live && Connection.ObservationsFresh;
    private string ConnectionLabel => Connection.State switch
    {
        DashboardLinkState.Live when !Connection.ObservationsConfirmed => "Waiting for a fresh observation",
        DashboardLinkState.Live => IsLive ? "Live observations" : "Observations stale",
        DashboardLinkState.Reconnecting => "Reconnecting",
        _ => "Connecting"
    };
    private DashboardSession? SelectedSession => Connection.Snapshot?.Sessions.FirstOrDefault(session => session.SessionId == _selectedId);

    protected override void OnInitialized()
    {
        Connection.Changed += Refresh;
        Connection.Start();
        _clock = TickAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        var preferences = await JavaScript.InvokeAsync<PresentationPreferences>("pkDashboard.preferences");
        _theme = preferences.Theme == "dark" ? "dark" : "light";
        _motion = preferences.Motion;
        StateHasChanged();
    }

    private void Refresh() => _ = InvokeAsync(StateHasChanged);
    private void SelectSession(string sessionId) => _selectedId = sessionId;
    private bool Pulse(string sessionId) => IsLive && _motion && Connection.IsPulsing(sessionId, DateTimeOffset.UtcNow);
    private async Task SignInAsync(string token) => await Connection.LoginAsync(token);

    private async Task SignOutAsync()
    {
        _signingOut = true;
        try { await Connection.LogoutAsync(); }
        finally { _signingOut = false; }
    }

    private async Task ToggleThemeAsync()
    {
        _theme = _theme == "light" ? "dark" : "light";
        await SavePreferencesAsync();
    }

    private async Task ChangeMotionAsync(ChangeEventArgs change)
    {
        _motion = change.Value is true;
        await SavePreferencesAsync();
    }

    private async Task SavePreferencesAsync() => await JavaScript.InvokeVoidAsync("pkDashboard.setPreferences", _theme, _motion);

    private async Task TickAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token))
            {
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        Connection.Changed -= Refresh;
        await _lifetime.CancelAsync();
        if (_clock is not null) await _clock;
        _lifetime.Dispose();
    }

    public sealed class PresentationPreferences
    {
        public string Theme { get; set; } = "light";
        public bool Motion { get; set; } = true;
    }
}

