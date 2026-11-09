using CoCern.Contracts;
using CoCern.Contracts.Dashboard;
using CoCern.Dashboard.Services;
using Microsoft.AspNetCore.Components;

namespace CoCern.Dashboard.Components;

public partial class ManagerControl : ComponentBase
{
    [Parameter] public bool Compact { get; set; }
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
            repositoryTouched = true;
            value = RepositoryIds.Match(value, Sessions.Select(session => session.RepositoryId)) ?? value;
            if (repositoryId == value) return;
            repositoryId = value;
            var wordsCleared = HumanWords.Length > 0;
            HumanWords = "";
            InvalidateSelection();
            dispatchRevision++;
            Paused = null;
            DispatchMessage = null;
            SetMessage(wordsCleared ? WordsCleared : null);
        }
    }
    // Human words authorize one exact selection, so a new target or repository clears them, and says so.
    public string TargetSessionId
    {
        get => targetSessionId;
        set
        {
            if (targetSessionId == value) return;
            targetSessionId = value;
            if (HumanWords.Length == 0) return;
            HumanWords = "";
            SetMessage(WordsCleared);
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
    public AdminActionEntry[] RecentActions { get; private set; } = [];
    public long ActionsDropped { get; private set; }
    private long? nextActionsBefore;
    private string? loadedRepository;
    private bool repositoryTouched;
    private const string RepositoryRequired = "Enter a repository to refresh selection or control dispatch.";
    private const string WordsCleared = "The human answer was cleared because the chosen chat or repository changed. Enter it again for this exact selection.";
    private long? nextHistoryBefore;
    // Reads update each field only while no newer operation has superseded that field.
    private long selectionRevision;
    private long dispatchRevision;
    private long messageRevision;

    protected override async Task OnParametersSetAsync()
    {
        if (repositoryTouched || !string.IsNullOrWhiteSpace(RepositoryId)) return;
        var candidate = !string.IsNullOrWhiteSpace(DefaultRepositoryId) ? DefaultRepositoryId
            : Sessions.FirstOrDefault(session => !string.IsNullOrWhiteSpace(session.RepositoryId))?.RepositoryId;
        if (string.IsNullOrWhiteSpace(candidate)) return;
        RepositoryId = candidate;
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (string.IsNullOrWhiteSpace(RepositoryId)) { SetMessage(RepositoryRequired); return; }
        if (ReadBusy) return;
        ReadBusy = true;
        var repository = RepositoryId;
        var selectionAtStart = ++selectionRevision;
        var dispatchAtStart = ++dispatchRevision;
        var messageAtStart = messageRevision;
        try
        {
            var result = await ManagerManagementClient.ReadAsync(ManagerHttp, repository, null);
            if (RepositoryId != repository) return;
            if (result.Ok)
            {
                if (selectionRevision == selectionAtStart && result.Manager is { } manager) ApplySelection(manager, repository);
                if (selectionRevision == selectionAtStart) ApplyActions(result.Actions);
                if (dispatchRevision == dispatchAtStart && result.Paused is { } paused) Paused = paused;
                if (messageRevision == messageAtStart) SetMessage(null);
            }
            else if (messageRevision == messageAtStart) SetMessage(Describe(result.Code));
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
            else if (!result.Ok && messageRevision == messageAtStart) SetMessage(Describe(result.Code));
        }
        catch (Exception error) when (Uncertain(error))
        {
            if (RepositoryId == repository && selectionRevision == selectionAtStart && messageRevision == messageAtStart)
                SetMessage("Could not load earlier selections. Try again.");
        }
        finally { ReadBusy = false; }
    }

    public async Task LoadEarlierActionsAsync()
    {
        if (ReadBusy || nextActionsBefore is null || loadedRepository != RepositoryId) return;
        ReadBusy = true;
        var repository = loadedRepository;
        var selectionAtStart = selectionRevision;
        var messageAtStart = messageRevision;
        try
        {
            var result = await ManagerManagementClient.ReadAsync(ManagerHttp, repository, null, actionsBefore: nextActionsBefore);
            if (RepositoryId != repository || selectionRevision != selectionAtStart) return;
            if (result.Ok && result.Actions is { } page)
            {
                RecentActions = RecentActions.Concat(page.Items).DistinctBy(item => item.Seq).OrderByDescending(item => item.Seq).ToArray();
                nextActionsBefore = page.NextBefore;
                ActionsDropped = page.Dropped;
            }
            else if (!result.Ok && messageRevision == messageAtStart) SetMessage(Describe(result.Code));
        }
        catch (Exception error) when (Uncertain(error))
        {
            if (RepositoryId == repository && selectionRevision == selectionAtStart && messageRevision == messageAtStart)
                SetMessage("Could not load earlier actions. Try again.");
        }
        finally { ReadBusy = false; }
    }

    // A known chat brings its repository, and that repository's selection is read at once so Appoint works without a manual Refresh.
    public async Task ChooseTargetAsync(string sessionId)
    {
        if (DecisionBusy || UnconfirmedDecision is not null) return;
        var hadWords = HumanWords.Length > 0;
        TargetSessionId = sessionId;
        if (Sessions.FirstOrDefault(session => session.SessionId == sessionId) is not { } target || string.IsNullOrWhiteSpace(target.RepositoryId)) return;
        var before = RepositoryId;
        RepositoryId = target.RepositoryId;
        var repository = RepositoryId;
        if (repository == before && loadedRepository == repository) return;
        await RefreshAsync();
        if (RepositoryId != repository || TargetSessionId != sessionId) return;
        string?[] notes =
        [
            Message,
            repository != before ? $"Switched to repository {repository}, where this chat belongs." : null,
            hadWords && HumanWords.Length == 0 ? WordsCleared : null
        ];
        var text = string.Join(" ", notes.Where(note => !string.IsNullOrEmpty(note)).Distinct());
        SetMessage(text.Length > 0 ? text : null);
    }

    /// <summary>Every manager of the loaded repository, most recent first; an older hub's reply names only its current one.</summary>
    public ManagerSelectionSummary[] Managers => Selection?.Managers ?? (Selection?.Current is { } only ? [only] : []);

    /// <summary>
    /// Records one decision with the exact human words entered: <c>appoint_manager</c> adds the chosen chat; <c>clear_manager</c> removes
    /// <paramref name="removeSessionId"/>, the one listed manager whose Remove was chosen, or every manager of the repository when it is null.
    /// </summary>
    public async Task DecideAsync(string command, string? removeSessionId = null)
    {
        if (DecisionBusy || UnconfirmedDecision is not null) return;
        if (Selection is null || loadedRepository != RepositoryId)
        {
            // Never submit a generation the user has not seen: read it, show it, and ask for the click again.
            if (string.IsNullOrWhiteSpace(RepositoryId)) { SetMessage(RepositoryRequired); return; }
            if (ReadBusy) { SetMessage("The current selection is still being read. Choose the action again in a moment."); return; }
            var repository = RepositoryId;
            await RefreshAsync();
            if (RepositoryId != repository) return;
            if (Selection is not null && loadedRepository == repository)
                SetMessage("The current selection was just read. Review it, then choose the action again to confirm.");
            else if (Message is null)
                SetMessage("Could not read the current selection. Try Refresh selection.");
            return;
        }
        if (command is not ("appoint_manager" or "clear_manager")) return;
        if (string.IsNullOrWhiteSpace(HumanWords) || HumanWords.Length > 200 || HumanWords.Any(UnsafeCharacter))
        {
            SetMessage("Enter a fresh exact direct human answer, on one line, up to 200 characters. It has not been shortened."); return;
        }
        if (command == "appoint_manager" && (string.IsNullOrWhiteSpace(TargetSessionId) || TargetSessionId.Length > RelayLimits.MaxIdentifierLength || TargetSessionId.Any(UnsafeCharacter)))
        {
            SetMessage("Choose an exact session and its repository."); return;
        }
        if (command == "clear_manager" && removeSessionId is not null && !Managers.Any(manager => manager.SessionId == removeSessionId))
        {
            SetMessage("That chat is not a manager of this repository. Refresh selection and choose again."); return;
        }
        UnconfirmedDecision = new(RepositoryId, Guid.NewGuid().ToString("N"), command, command == "appoint_manager" ? TargetSessionId : removeSessionId, Selection.Generation, HumanWords);
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
            if (result.Ok)
            {
                var receipt = $"Manager selection recorded for request {decision.RequestId}.";
                SetMessage(receipt);
                if (!await ReadAfterWriteAsync(decision.RepositoryId))
                    SetMessage(receipt + " Current state is unknown. Use Refresh selection.");
                return;
            }
            if (result.Manager is { } manager) ApplySelection(manager, decision.RepositoryId);
            else if (result.Code == RelayErrorCodes.VersionConflict) InvalidateSelection();
            if (dispatchRevision == dispatchAtStart && result.Paused is { } paused) { Paused = paused; dispatchRevision++; }
            SetMessage(result.Code == RelayErrorCodes.VersionConflict
                ? result.Manager is null
                    ? "Selection changed. The current selection could not be read. Use Refresh selection before making a new decision."
                    : "Selection changed. The current selection is shown; review it before making a new decision."
                : Describe(result.Code));
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
        if (string.IsNullOrWhiteSpace(RepositoryId)) { DispatchMessage = RepositoryRequired; return; }
        if (DispatchBusy || command is not ("pause" or "stop" or "resume")) return;
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
            if (!result.Ok) { DispatchMessage = Describe(result.Code); return; }
            DispatchMessage = decision.Command switch
            {
                "stop" => "Stop recorded. Cancellation requested from known chats. Running tool outcomes remain unknown.",
                "pause" => "Pause recorded.",
                _ => "Resume recorded."
            };
            DispatchMessage += $" Request {decision.RequestId}.";
            if (!await ReadAfterWriteAsync(RepositoryId))
                DispatchMessage += " Current state is unknown. Use Refresh selection.";
        }
        catch (Exception error) when (Uncertain(error))
        {
            dispatchRevision++;
            DispatchMessage = "The dispatch result is unconfirmed. Retry dispatch reuses the same request.";
        }
        finally { DispatchBusy = false; }
    }

    // An idempotent receipt can describe an older generation or an already-reversed pause.
    private async Task<bool> ReadAfterWriteAsync(string repository)
    {
        if (RepositoryId != repository) return false;
        InvalidateSelection();
        HumanWords = "";
        Paused = null;
        var selectionAtStart = selectionRevision;
        var dispatchAtStart = ++dispatchRevision;
        try
        {
            var current = await ManagerManagementClient.ReadAsync(ManagerHttp, repository, null);
            if (!current.Ok || current.Manager is null || current.Paused is null || RepositoryId != repository) return false;
            if (selectionRevision == selectionAtStart)
            {
                ApplySelection(current.Manager, repository);
                ApplyActions(current.Actions);
            }
            if (dispatchRevision == dispatchAtStart) Paused = current.Paused;
            return true;
        }
        catch (Exception error) when (Uncertain(error)) { return false; }
    }

    private void ApplySelection(ManagerSelectionStatus manager, string repository)
    {
        if (UnconfirmedDecision is null && Selection is { } previous && previous.Generation != manager.Generation) HumanWords = "";
        Selection = manager; loadedRepository = repository;
        RecentSelections = manager.History;
        nextHistoryBefore = manager.NextHistoryBefore;
    }

    // A read that carries no page says nothing about the log, so what is showing stays.
    private void ApplyActions(AdminActionPage? page)
    {
        if (page is null) return;
        RecentActions = page.Items;
        nextActionsBefore = page.NextBefore;
        ActionsDropped = page.Dropped;
    }

    private void InvalidateSelection()
    {
        selectionRevision++;
        Selection = null; loadedRepository = null;
        RecentSelections = []; nextHistoryBefore = null;
        RecentActions = []; nextActionsBefore = null; ActionsDropped = 0;
    }

    private static bool UnsafeCharacter(char ch) => char.IsControl(ch) || ch is '\u2028' or '\u2029';

    private void SetMessage(string? message) { messageRevision++; Message = message; }

    // One plain line per refusal; the raw code stays in parentheses for diagnosis.
    private static string Describe(string code) => code switch
    {
        RelayErrorCodes.SessionUnknown => $"The hub does not know this chat as live: it is not connected, is being closed, or has had no activity for {RelayLimits.StaleSessionHours} hours. Reopen or reconnect it, then refresh. ({code})",
        RelayErrorCodes.RepositoryDenied => $"The chat belongs to another repository than the one shown. Choose it from the list, or enter its own repository. ({code})",
        RelayErrorCodes.SelfAppointment => $"A chat can't appoint itself. Appoint it from the dashboard or through another chat. ({code})",
        RelayErrorCodes.AdministratorRequired => $"This dashboard account isn't an administrator, so it can't change the manager. ({code})",
        RelayErrorCodes.CapacityExceeded => $"This repository already has {RelayLimits.MaxManagersPerRepository} managers. Remove one before adding another. ({code})",
        RelayErrorCodes.InvalidArguments => $"The hub refused the request as malformed. Check the session ID, repository and human answer. ({code})",
        _ => code
    };
    private static bool Uncertain(Exception error) => error is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException;
}
