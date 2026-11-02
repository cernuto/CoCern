namespace CoCern.Contracts.Dashboard;

public sealed class DashboardLoginOptions
{
    public int SessionHours { get; set; }
    public int RememberDays { get; set; }
}

public sealed class DashboardLoginRequest
{
    public string Token { get; set; } = "";
    public bool RememberBrowser { get; set; }
}
