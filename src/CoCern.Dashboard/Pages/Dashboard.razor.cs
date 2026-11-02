using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using CoCern.Contracts.Dashboard;
using CoCern.Dashboard.Services;

namespace CoCern.Dashboard.Pages;

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
    private bool _signingOut;

    private string View => Navigation.ToBaseRelativePath(Navigation.Uri).Split('?', '#')[0].Trim('/').ToLowerInvariant() switch
    {
        "sessions" => "sessions",
        "sql" => "sql",
        "logs" => "logs",
        "standing" => "standing",
        "documents" => "documents",
        _ => "overview"
    };
    private string PageTitle => View switch { "sessions" => "Sessions", "sql" => "Database", "logs" => "Logs", "standing" => "Standing", "documents" => "Documents", _ => "Overview" };
    private string? PageDescription => View switch
    {
        "standing" => "Each board's settled lines, grants and orders, and who approved them.",
        "documents" => "Document indexes loaded on this hub.",
        _ => null
    };
    private bool IsLive => Connection.State == DashboardLinkState.Live && Connection.ObservationsFresh;
    private string ConnectionLabel => Connection.State switch
    {
        DashboardLinkState.Live when !Connection.ObservationsConfirmed => "Waiting for an update",
        DashboardLinkState.Live => IsLive ? "Live" : "Stale",
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
        StateHasChanged();
    }

    private void Refresh() => _ = InvokeAsync(StateHasChanged);
    private void SelectSession(string sessionId) => _selectedId = sessionId;
    // Motion follows the system's reduced-motion setting (see dashboard.css).
    private bool Pulse(string sessionId) => IsLive && Connection.IsPulsing(sessionId, DateTimeOffset.UtcNow);
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

    private async Task SavePreferencesAsync() => await JavaScript.InvokeVoidAsync("pkDashboard.setPreferences", _theme);

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
    }
}

