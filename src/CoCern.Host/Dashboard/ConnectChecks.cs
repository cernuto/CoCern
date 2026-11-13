using CoCern.Contracts.Dashboard;

namespace CoCern.Host.Dashboard;

/// <summary>The installation checks that belong to one connected app, picked from Repair's read-only check rows.</summary>
internal static class ConnectChecks
{
    private const string Passed = "passed";
    private const string Warning = "warning";
    private const string BeforeFirstChat = "This can't pass until the app starts a chat. ";

    private static readonly string[] Shared = ["adapter", "enrollment", "hub", "hub-transport", "hub-access", "startup"];
    private static readonly string[] ClaudeCode = ["claude", "claude-wake"];
    private static readonly string[] Codex = ["codex", "codex-native-helper", "native-activation-preview", "workflow-skill", "native-setup", "native-startup", "session", "wake"];
    private static readonly string[] CodexAfterFirstChat = ["native-startup", "session", "wake"];

    /// <summary>
    /// The rows for <paramref name="client"/>, in their original order. For Codex, a row that only a started chat can pass is a warning, not a failure,
    /// while no chat has started.
    /// </summary>
    public static DashboardRepairCheck[] For(string client, IReadOnlyList<DashboardRepairCheck> rows)
    {
        string[] own = client switch { ConnectClients.ClaudeCode => ClaudeCode, ConnectClients.Codex => Codex, _ => [] };
        return rows.Where(row => Shared.Contains(row.Id) || own.Contains(row.Id))
            .Select(row => client == ConnectClients.Codex && CodexAfterFirstChat.Contains(row.Id) && row.State != Passed
                ? row with { State = Warning, Detail = BeforeFirstChat + row.Detail }
                : row)
            .ToArray();
    }
}
