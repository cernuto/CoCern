namespace CoCern.Contracts;

/// <summary>Tool selection is an intersection with the authenticated credential, never a source of authority.</summary>
public static class SessionToolProfiles
{
    public static bool Allows(string? profile, string name, bool administrator)
    {
        if (!RelayToolCatalog.TryGet(name, out var tool) || (tool.AdministratorOnly && !administrator)) return false;
        // Null means the legacy adapter selection remains authoritative until an explicit hub choice.
        if (profile is null) return true;
        return profile switch
        {
            "admin" => true,
            "worker" => !tool.AdministratorOnly,
            "diagnostics" => !tool.AdministratorOnly || name is RelayTools.AnalyticsQuery or RelayTools.HubDiagnose,
            "chat" => name is RelayTools.SessionOpen or RelayTools.SessionBind or RelayTools.SessionLabelSet or RelayTools.GetStatus or RelayTools.SendMessage or RelayTools.CheckInbox or RelayTools.Ack or RelayTools.ChatDirectory or RelayTools.Peers
                or RelayTools.ClaimRenew or RelayTools.ClaimRelease or RelayTools.ActionReport,
            _ => false
        };
    }
}
