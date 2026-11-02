namespace CoCern.Host.Dashboard;

public sealed class DashboardAuthenticationSettings
{
    public int SessionHours { get; }
    public int RememberDays { get; }

    public DashboardAuthenticationSettings(IConfiguration configuration)
    {
        SessionHours = Read(configuration, "SessionHours", 8, 168);
        RememberDays = Read(configuration, "RememberDays", 30, 90);
        if (TimeSpan.FromDays(RememberDays) < TimeSpan.FromHours(SessionHours))
            throw new InvalidOperationException("Relay:Dashboard:RememberDays must cover at least Relay:Dashboard:SessionHours.");
    }

    public TimeSpan Lifetime(bool rememberBrowser) => rememberBrowser ? TimeSpan.FromDays(RememberDays) : TimeSpan.FromHours(SessionHours);

    private static int Read(IConfiguration configuration, string name, int fallback, int maximum)
    {
        var key = "Relay:Dashboard:" + name;
        if (configuration[key] is not { } raw) return fallback;
        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value) || value < 1 || value > maximum)
            throw new InvalidOperationException($"{key} must be an integer from 1 to {maximum}.");
        return value;
    }
}
