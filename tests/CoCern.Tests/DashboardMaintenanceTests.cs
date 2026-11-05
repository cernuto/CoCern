using System.Net;
using System.Net.Http.Json;
using CoCern.Contracts.Dashboard;
using CoCern.Host.Dashboard;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace CoCern.Tests;

public sealed class DashboardMaintenanceTests
{
    [Fact]
    public async Task Local_sign_in_and_reset_require_local_origin_admin_and_explicit_confirmation()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Relay:Clients:0:IsAdministrator"] = "true",
            ["Relay:Dashboard:ViewerPrincipalIds:0"] = "test-user"
        };
        await using var host = new RelayHostFixture(remoteAddress: IPAddress.Loopback, settings: settings);
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/dashboard/api/database/reset")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/dashboard/api/login-local", null)).StatusCode);
        client.DefaultRequestHeaders.Add("Origin", "https://attacker.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/dashboard/api/login-local", null)).StatusCode);
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "https://localhost");
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "127.0.0.1");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/dashboard/api/login-local", null)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Forwarded-For");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/dashboard/api/login-local", null)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.5");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/dashboard/api/database/reset", new DashboardDatabaseResetRequest(DashboardDatabaseManagement.Confirmation))).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Forwarded-For");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/dashboard/api/database/reset", new DashboardDatabaseResetRequest("reset"))).StatusCode);
        using var scheduled = await client.PostAsJsonAsync("/dashboard/api/database/reset", new DashboardDatabaseResetRequest(DashboardDatabaseManagement.Confirmation));
        Assert.True((await scheduled.Content.ReadFromJsonAsync<DashboardDatabaseResetStatus>())!.Pending);
        client.DefaultRequestHeaders.Remove("Origin");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/dashboard/api/database/reset", new DashboardDatabaseResetRequest("", true))).StatusCode);
        client.DefaultRequestHeaders.Add("Origin", "https://localhost");
        using var canceled = await client.PostAsJsonAsync("/dashboard/api/database/reset", new DashboardDatabaseResetRequest("", true));
        Assert.False((await canceled.Content.ReadFromJsonAsync<DashboardDatabaseResetStatus>())!.Pending);

        await using var remote = new RelayHostFixture(remoteAddress: IPAddress.Parse("203.0.113.5"), settings: settings);
        using var remoteClient = remote.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        remoteClient.DefaultRequestHeaders.Add("Origin", "https://localhost");
        Assert.Equal(HttpStatusCode.Forbidden, (await remoteClient.PostAsync("/dashboard/api/login-local", null)).StatusCode);

        await using var viewerDenied = new RelayHostFixture(remoteAddress: IPAddress.Loopback, settings: new Dictionary<string, string?>
        {
            ["Relay:Clients:0:IsAdministrator"] = "false",
            ["Relay:Dashboard:ViewerPrincipalIds:0"] = "test-user"
        });
        using var deniedClient = viewerDenied.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        deniedClient.DefaultRequestHeaders.Add("Origin", "https://localhost");
        Assert.Equal(HttpStatusCode.Unauthorized, (await deniedClient.PostAsync("/dashboard/api/login-local", null)).StatusCode);
    }
}
