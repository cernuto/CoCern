namespace CoCern.Host.Dashboard;

/// <summary>
/// How long a dashboard sign-in lasts: <c>Relay:Dashboard:SessionHours</c> for a browser session, <c>Relay:Dashboard:RememberDays</c> with
/// "Remember this browser". A browser sends a 127.0.0.1 cookie to every port on it, so a remembered cookie is kept short by default.
/// </summary>
public sealed class DashboardAuthenticationSettings
{
    public const int DefaultSessionHours = 8;
    public const int MaxSessionHours = 168;
    public const int DefaultRememberDays = 7;
    public const int MaxRememberDays = 30;

    public int SessionHours { get; }
    public int RememberDays { get; }

    public DashboardAuthenticationSettings(IConfiguration configuration)
    {
        SessionHours = Read(configuration, "SessionHours", DefaultSessionHours, MaxSessionHours);
        RememberDays = Read(configuration, "RememberDays", DefaultRememberDays, MaxRememberDays);
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
