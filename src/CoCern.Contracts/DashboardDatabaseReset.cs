namespace CoCern.Contracts.Dashboard;

public sealed record DashboardDatabaseResetRequest(string Confirmation, bool Cancel = false);
public sealed record DashboardDatabaseResetStatus(bool Pending);
