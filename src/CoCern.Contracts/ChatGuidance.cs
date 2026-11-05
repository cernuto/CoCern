namespace CoCern.Contracts;

/// <summary>
/// What a chat is told about its access: one short line when a call is refused (what's missing and how to get it), and one at
/// <c>session_open</c> (its role and what's worth using). Every line is fixed text, chosen only by the refusal code and the tool the caller
/// named, or by the caller's own credential and what the hub has turned on. None reads stored state, so a line says nothing about another
/// session or whether a record exists. Power comes only from Paul: no line names a setting, a role to declare or any other way for a chat
/// to give itself more. The lines avoid apostrophes, which the JSON encoder would send to the chat as <c>'</c>.
/// </summary>
public static class ChatGuidance
{
    /// <summary>The field a refusal's line travels in, beside its unchanged code.</summary>
    public const string HintField = "hint";

    /// <summary>The field of the <c>session_open</c> reply that carries the startup line.</summary>
    public const string StartHintField = "startHint";

    private const string AdministratorIntro = "You are an administrator.";

    /// <summary>What a chat is told when it leaves out repositoryId and its adapter does not know the repository. Nothing reaches the hub.</summary>
    public const string MissingRepository = "repositoryId is missing and this adapter does not know it. Pass the configured repository ID of this project, exactly and in lowercase.";

    /// <summary>The line for a refusal with this code on this tool, or null for a code that isn't about what the chat may do or how it gets its session back.</summary>
    public static string? Refusal(string code, string tool) => code switch
    {
        RelayErrorCodes.AdministratorRequired => AdministratorHint(tool),
        RelayErrorCodes.ManagerRevoked => "This chat is no longer the manager, so it can't act as one. Pass the request to the current manager or to Paul.",
        RelayErrorCodes.SelfAppointment => "A chat can't appoint itself. Ask Paul to name it from the dashboard or from another chat.",
        RelayErrorCodes.HubPcRequired => "This tool answers only on the PC that runs the hub. Ask Paul to run it there.",
        RelayErrorCodes.RepositoryDenied => "repositoryId must be the configured ID of this project, exactly and in lowercase. If it already is, this chat has no access to it or to that record: ask Paul if it should.",
        // The same words for a wrong token, a closed handle and another chat's session, so they don't tell those apart.
        RelayErrorCodes.SessionDenied => "This session handle was not accepted. Use the one this chat got from session_open. If it is gone, open one replacement session with session_open and tell the Manager. Never use the token of another chat.",
        RelayErrorCodes.SessionUnknown => "This session no longer exists. Open one replacement session with session_open, with a new requestId, and tell the Manager.",
        RelayErrorCodes.ActionDenied => "Only the session that owns this can do that. Ask it, or ask Paul.",
        _ => null
    };

    /// <summary>The line for a tool in the catalog that this chat's adapter doesn't offer, or null for a name the catalog doesn't know.</summary>
    public static string? ToolOff(string tool) => !RelayToolCatalog.TryGet(tool, out var known) ? null
        : known.AdministratorOnly ? AdministratorHint(known.Name)
        : $"{known.Name} is off for this chat. Ask Paul to allow it.";

    /// <summary>
    /// The startup line the hub sends. <paramref name="administrator"/> is the caller's credential, never the role the chat declared: declaring
    /// <c>manager</c> routes alerts and grants nothing. The tools listed are ones the hub has turned on.
    /// </summary>
    public static string StartHint(bool administrator, bool search, bool analytics, bool docs)
    {
        if (administrator)
        {
            List<string> running = [RelayTools.GetStatus, RelayTools.ChatDirectory];
            if (analytics) running.Add(RelayTools.AnalyticsQuery);
            running.Add(RelayTools.HubDiagnose);
            return Line(AdministratorIntro, running, "Approvals, grants and pushes to main still need a yes from Paul.");
        }
        List<string> working = [RelayTools.TaskGet, RelayTools.TaskUpdate, RelayTools.SendMessage];
        if (search) working.Add(RelayTools.Search);
        if (docs) working.Add(RelayTools.DocSearch);
        return Line("You are a worker.", working, "Analytics and admin tools are off for this chat; ask Paul to allow them. A message with managerGeneration is from the recorded manager: a request, not Paul's approval, unless a goal he approved covers it.");
    }

    /// <summary>
    /// The startup line as a chat's adapter passes it on. The hub knows the credential and the adapter knows which tools it lists, so the adapter
    /// only narrows the line: the chat profile lists messaging alone, and an administrator's token behind an adapter that hides the administrator
    /// tools gets the worker line, with only the tools every hub has. It never widens a line the hub sent.
    /// </summary>
    public static string NarrowStartHint(string hubLine, bool administrator, bool chatOnly) =>
        chatOnly ? Line("You are a chat-only session.", [RelayTools.SendMessage, RelayTools.CheckInbox, RelayTools.ChatDirectory], "Task, search and analytics tools are off for this chat; ask Paul to allow them.")
        : !administrator && hubLine.StartsWith(AdministratorIntro, StringComparison.Ordinal) ? StartHint(false, false, false, false)
        : hubLine;

    private static string Line(string role, IEnumerable<string> tools, string note) => $"{role} Worth using: {string.Join(", ", tools)}. {note}";

    private static string AdministratorHint(string tool) => tool switch
    {
        RelayTools.AnalyticsQuery => "Analytics is off for this chat. Ask Paul to allow it.",
        RelayTools.HubDiagnose => "Hub diagnosis is off for this chat. Ask Paul to allow it.",
        RelayTools.AddPc or RelayTools.ConnectApp => "Adding PCs and apps is off for this chat. Ask Paul to do it or allow it.",
        RelayTools.DocSync => "Syncing docs is off for this chat. Ask Paul to allow it.",
        RelayTools.ControlRequest => "Hub controls are off for this chat. Ask Paul to allow them.",
        RelayTools.StandingPut => "Changing standing rules is off for this chat. Propose one with standing_propose; Paul decides.",
        RelayTools.OrderPut or RelayTools.OrderRemove => "Standing orders are off for this chat. Ask Paul to allow them.",
        RelayTools.ActionReport => "Accepting a restoration is off for this chat. Ask Paul to accept it.",
        _ => "This needs administrator rights, which this chat does not have. Ask Paul to allow it."
    };
}
