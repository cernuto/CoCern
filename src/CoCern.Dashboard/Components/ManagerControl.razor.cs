using CoCern.Contracts;
using CoCern.Contracts.Dashboard;
using CoCern.Dashboard.Services;
using Microsoft.AspNetCore.Components;

namespace CoCern.Dashboard.Components;

public partial class ManagerControl : ComponentBase
{
    [Parameter] public DashboardSession[] Sessions { get; set; } = [];
    [Parameter] public string? DefaultRepositoryId { get; set; }
    [Inject] public HttpClient ManagerHttp { get; set; } = default!;
    private string repositoryId = "";
    private string targetSessionId = "";
    public string RepositoryId
    {
        get => repositoryId;
        set
        {
            if (repositoryId == value) return;
            repositoryId = value;
            HumanWords = "";
            InvalidateSelection();
            dispatchRevision++;
            SetMessage(null);
        }
    }
    public string TargetSessionId
    {
        get => targetSessionId;
        set
        {
            if (targetSessionId == value) return;
            targetSessionId = value;
            HumanWords = "";
        }
    }
    public string HumanWords { get; set; } = "";
    public ManagerSelectionStatus? Selection { get; private set; }
    public DashboardManagerDecision? UnconfirmedDecision { get; private set; }
    public DashboardDispatchDecision? UnconfirmedDispatch { get; private set; }
    public string? Message { get; private set; }
    public string? DispatchMessage { get; private set; }
    public bool DecisionBusy { get; private set; }
    public bool DispatchBusy { get; private set; }
    public bool ReadBusy { get; private set; }
    public bool? Paused { get; private set; }
    public ManagerSelectionHistory[] RecentSelections { get; private set; } = [];
    private string? loadedRepository;
    private bool initialized;
    private long? nextHistoryBefore;
    // Reads update each field only while no newer operation has superseded that field.
    private long selectionRevision;
    private long dispatchRevision;
    private long messageRevision;

    protected override async Task OnParametersSetAsync()
    {
        if (initialized) return;
        RepositoryId = DefaultRepositoryId ?? Sessions.FirstOrDefault()?.RepositoryId ?? "";
        initialized = true;
        if (!string.IsNullOrWhiteSpace(RepositoryId)) await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (ReadBusy || string.IsNullOrWhiteSpace(RepositoryId)) return;
        ReadBusy = true;
        var repository = RepositoryId;
        var selectionAtStart = selectionRevision;
        var dispatchAtStart = dispatchRevision;
        var messageAtStart = messageRevision;
        try
        {
            var result = await ManagerManagementClient.ReadAsync(ManagerHttp, repository, null);
            if (RepositoryId != repository) return;
            if (result.Ok)
            {
                if (selectionRevision == selectionAtStart && result.Manager is { } manager) ApplySelection(manager, repository);
                if (dispatchRevision == dispatchAtStart && result.Paused is { } paused) Paused = paused;
                if (messageRevision == messageAtStart) SetMessage(null);
            }
            else if (messageRevision == messageAtStart) SetMessage(result.Code);
        }
        catch (Exception error) when (Uncertain(error))
        {
            if (RepositoryId == repository && messageRevision == messageAtStart)
                SetMessage("Could not read the current selection. Try Refresh selection.");
        }
        finally { ReadBusy = false; }
    }

    public async Task LoadEarlierAsync()
    {
        if (ReadBusy || nextHistoryBefore is null || loadedRepository != RepositoryId) return;
        ReadBusy = true;
        var repository = loadedRepository;
        var selectionAtStart = selectionRevision;
        var messageAtStart = messageRevision;
        try
        {
            var result = await ManagerManagementClient.ReadAsync(ManagerHttp, repository, nextHistoryBefore);
            if (RepositoryId != repository || selectionRevision != selectionAtStart) return;
            if (result.Ok && result.Manager is { } page)
            {
                RecentSelections = RecentSelections.Concat(page.History).DistinctBy(item => item.Generation).OrderByDescending(item => item.Generation).ToArray();
                nextHistoryBefore = page.NextHistoryBefore;
            }
            else if (!result.Ok && messageRevision == messageAtStart) SetMessage(result.Code);
        }
        catch (Exception error) when (Uncertain(error))
        {
            if (RepositoryId == repository && selectionRevision == selectionAtStart && messageRevision == messageAtStart)
                SetMessage("Could not load earlier selections. Try again.");
        }
        finally { ReadBusy = false; }
    }

    public void SelectTarget(ChangeEventArgs change)
    {
        if (UnconfirmedDecision is not null) return;
        TargetSessionId = change.Value?.ToString() ?? "";
        if (Sessions.FirstOrDefault(session => session.SessionId == TargetSessionId) is { } target)
            RepositoryId = target.RepositoryId;
    }

    public async Task DecideAsync(string command)
    {
        if (DecisionBusy || UnconfirmedDecision is not null) return;
        if (Selection is null || loadedRepository != RepositoryId)
        {
            SetMessage("Refresh selection for this repository before deciding."); return;
        }
        if (command is not ("appoint_manager" or "clear_manager")) return;
        if (string.IsNullOrWhiteSpace(HumanWords) || HumanWords.Length > 200 || HumanWords.Any(ch => char.IsControl(ch) || ch is '\u2028' or '\u2029'))
        {
            SetMessage("Enter a fresh exact direct human answer, on one line, up to 200 characters. It has not been shortened."); return;
        }
        if (command == "appoint_manager" && !Sessions.Any(session => session.SessionId == TargetSessionId && session.RepositoryId == RepositoryId))
        {
            SetMessage("Choose an exact session and its repository."); return;
        }
        UnconfirmedDecision = new(RepositoryId, Guid.NewGuid().ToString("N"), command, command == "appoint_manager" ? TargetSessionId : null, Selection.Generation, HumanWords);
        await RetryDecisionAsync();
    }

    public async Task RetryDecisionAsync()
    {
        if (DecisionBusy || UnconfirmedDecision is not { } decision) return;
        DecisionBusy = true;
        selectionRevision++;
        var dispatchAtStart = dispatchRevision;
        SetMessage(null);
        try
        {
            var result = await ManagerManagementClient.DecideAsync(ManagerHttp, decision);
            UnconfirmedDecision = null;
            HumanWords = "";
            selectionRevision++;
            if (result.Manager is { } manager) ApplySelection(manager, decision.RepositoryId);
            else if (result.Code == RelayErrorCodes.VersionConflict) InvalidateSelection();
            if (dispatchRevision == dispatchAtStart && result.Paused is { } paused) { Paused = paused; dispatchRevision++; }
            SetMessage(result.Code == RelayErrorCodes.VersionConflict
                ? result.Manager is null
                    ? "Selection changed. The current selection could not be read. Use Refresh selection before making a new decision."
                    : "Selection changed. The current selection is shown; review it before making a new decision."
                : result.Ok ? "Manager selection recorded." : result.Code);
        }
        catch (Exception error) when (Uncertain(error))
        {
            selectionRevision++;
            SetMessage("The result is unconfirmed. Retry decision keeps the same request, generation and exact human words.");
        }
        finally { DecisionBusy = false; }
    }

    public void NewDecision()
    {
        if (DecisionBusy) return;
        if (UnconfirmedDecision is not null) InvalidateSelection();
        UnconfirmedDecision = null;
        HumanWords = "";
        SetMessage("Review the current selection and enter a fresh human answer before making a new decision.");
    }

    public async Task DispatchAsync(string command)
    {
        if (DispatchBusy || string.IsNullOrWhiteSpace(RepositoryId) || command is not ("pause" or "stop" or "resume")) return;
        // Choosing a different command is an explicit new intent; retrying this command reuses its request.
        if (UnconfirmedDispatch is null || UnconfirmedDispatch.Command != command || UnconfirmedDispatch.RepositoryId != RepositoryId)
            UnconfirmedDispatch = new(RepositoryId, Guid.NewGuid().ToString("N"), command);
        await RetryDispatchAsync();
    }

    public async Task RetryDispatchAsync()
    {
        if (DispatchBusy || UnconfirmedDispatch is not { } decision) return;
        DispatchBusy = true;
        dispatchRevision++;
        try
        {
            var result = await ManagerManagementClient.DispatchAsync(ManagerHttp, decision);
            UnconfirmedDispatch = null;
            dispatchRevision++;
            if (result.Paused is { } paused) Paused = paused;
            DispatchMessage = result.Ok ? decision.Command switch
            {
                "stop" => "Dispatch paused. Cancellation requested from known chats; running tool outcomes remain unknown.",
                "pause" => "Dispatch paused.",
                _ => "Dispatch resumed."
            } : result.Code;
        }
        catch (Exception error) when (Uncertain(error))
        {
            dispatchRevision++;
            DispatchMessage = "The dispatch result is unconfirmed. Retry dispatch reuses the same request.";
        }
        finally { DispatchBusy = false; }
    }

    private void ApplySelection(ManagerSelectionStatus manager, string repository)
    {
        if (Selection is { } previous && previous.Generation != manager.Generation) HumanWords = "";
        Selection = manager; loadedRepository = repository;
        RecentSelections = manager.History;
        nextHistoryBefore = manager.NextHistoryBefore;
    }

    private void InvalidateSelection()
    {
        selectionRevision++;
        Selection = null; loadedRepository = null;
        RecentSelections = []; nextHistoryBefore = null;
    }

    private void SetMessage(string? message) { messageRevision++; Message = message; }
    private static bool Uncertain(Exception error) => error is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException;
}
