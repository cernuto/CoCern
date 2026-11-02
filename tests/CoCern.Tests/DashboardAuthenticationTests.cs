using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CoCern.Host.Dashboard;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace CoCern.Tests;

public sealed class DashboardAuthenticationTests
{
    [Fact]
    public async Task Root_redirects_to_the_dashboard_without_intercepting_other_routes()
    {
        await using var host = OperatorHost();
        using var client = BrowserClient(host);
        var response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/dashboard/", response.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/unrelated/deep-link")).StatusCode);
    }

    [Theory]
    [InlineData(false, null, null, 8)]
    [InlineData(true, null, null, 720)]
    [InlineData(false, "2", "14", 2)]
    [InlineData(true, "2", "14", 336)]
    public async Task Login_issues_the_selected_absolute_lifetime_and_only_remembered_cookies_persist(bool remember, string? sessionHours, string? rememberDays, int expectedHours)
    {
        await using var host = OperatorHost(sessionHours, rememberDays);
        using var client = BrowserClient(host);
        var response = await client.PostAsJsonAsync("/dashboard/api/login", new { token = RelayHostFixture.Token, rememberBrowser = remember });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Equal(remember, cookie.Contains("expires=", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(RelayHostFixture.Token, cookie);
        var options = host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(DashboardSecurity.Scheme);
        var ticket = Assert.IsType<AuthenticationTicket>(options.TicketDataFormat.Unprotect(cookie.Split(';')[0].Split('=', 2)[1]));
        var issued = Assert.IsType<DateTimeOffset>(ticket.Properties.IssuedUtc);
        var expires = Assert.IsType<DateTimeOffset>(ticket.Properties.ExpiresUtc);
        Assert.InRange((expires - issued).TotalHours, expectedHours - .001, expectedHours + .001);
        Assert.Equal(remember, ticket.Properties.IsPersistent);
        Assert.False(ticket.Properties.AllowRefresh);
        Assert.False(options.SlidingExpiration);
        var security = host.Services.GetRequiredService<DashboardSecurity>();
        Assert.True(security.IsValid(ticket.Principal, issued.AddHours(expectedHours).AddSeconds(-1)));
        Assert.False(security.IsValid(ticket.Principal, expires.AddSeconds(1)));
        var auth = await client.GetAsync("/dashboard/api/auth");
        Assert.Equal(HttpStatusCode.OK, auth.StatusCode);
        Assert.False(auth.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/dashboard/api/logout", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/dashboard/api/auth")).StatusCode);
    }

    [Fact]
    public async Task Login_without_remember_choice_keeps_a_browser_session_cookie()
    {
        await using var host = OperatorHost();
        using var client = BrowserClient(host);
        var response = await client.PostAsJsonAsync("/dashboard/api/login", new { token = RelayHostFixture.Token });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("expires=", Assert.Single(response.Headers.GetValues("Set-Cookie")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sign_in_options_show_the_configured_durations_before_authentication_without_caching()
    {
        await using var host = OperatorHost("12", "45");
        using var client = BrowserClient(host);
        var response = await client.GetAsync("/dashboard/api/login-options");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(12, json.GetProperty("sessionHours").GetInt32());
        Assert.Equal(45, json.GetProperty("rememberDays").GetInt32());
        Assert.DoesNotContain(RelayHostFixture.Token, await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/dashboard/api/snapshot")).StatusCode);
    }

    [Theory]
    [InlineData("SessionHours", "0")]
    [InlineData("SessionHours", "169")]
    [InlineData("SessionHours", "bad")]
    [InlineData("RememberDays", "0")]
    [InlineData("RememberDays", "91")]
    public async Task Invalid_authentication_durations_fail_clearly(string key, string value)
    {
        await using var host = OperatorHost(key == "SessionHours" ? value : null, key == "RememberDays" ? value : null);
        var failure = Assert.Throws<InvalidOperationException>(() => host.Services);
        Assert.Contains("Relay:Dashboard:" + key, failure.ToString());
    }

    [Fact]
    public async Task Remembered_duration_cannot_be_shorter_than_the_browser_session_duration()
    {
        await using var host = OperatorHost("168", "1");
        var failure = Assert.Throws<InvalidOperationException>(() => host.Services);
        Assert.Contains("Relay:Dashboard:RememberDays", failure.ToString());
    }

    [Fact]
    public async Task Invalid_remember_choice_does_not_issue_a_cookie()
    {
        await using var host = OperatorHost();
        using var client = BrowserClient(host);
        var response = await client.PostAsJsonAsync("/dashboard/api/login", new { token = RelayHostFixture.Token, rememberBrowser = "true" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    private static RelayHostFixture OperatorHost(string? sessionHours = null, string? rememberDays = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Relay:Clients:0:IsAdministrator"] = "true",
            ["Relay:Dashboard:ViewerPrincipalIds:0"] = "test-user"
        };
        if (sessionHours is not null) settings["Relay:Dashboard:SessionHours"] = sessionHours;
        if (rememberDays is not null) settings["Relay:Dashboard:RememberDays"] = rememberDays;
        return new RelayHostFixture(settings: settings);
    }

    private static HttpClient BrowserClient(RelayHostFixture host)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Origin", "https://localhost");
        return client;
    }
}
