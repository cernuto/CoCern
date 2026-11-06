using System.Net;
using System.Net.Http.Json;
using CoCern.Contracts.Dashboard;
using CoCern.Host.Dashboard;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace CoCern.Tests;

public sealed class DashboardInstallationTests
{
    [Fact]
    public void Published_adapter_uses_its_bundled_executable_without_requiring_dotnet()
    {
        var root = Path.Combine(Path.GetTempPath(), "cocern-apphost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var dll = Path.Combine(root, "CoCern.Adapter.dll");
            var executable = Path.Combine(root, "CoCern.Adapter" + (OperatingSystem.IsWindows() ? ".exe" : ""));
            File.WriteAllText(dll, "fixture assembly path");
            File.WriteAllText(executable, "fixture apphost path");
            var bundled = DashboardConnect.AdapterProcess(dll, ["--repair", "--check", "--json"]);
            Assert.Equal(executable, bundled.FileName);
            Assert.Equal(new[] { "--repair", "--check", "--json" }, bundled.ArgumentList);
            File.Delete(executable);
            var fallback = DashboardConnect.AdapterProcess(dll, ["--repair", "--check"]);
            Assert.Equal(dll, fallback.ArgumentList[0]);
            Assert.Equal("--repair", fallback.ArgumentList[1]);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Repair_and_diagnostics_require_local_viewer_and_same_origin_before_any_adapter_action()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Relay:Clients:0:IsAdministrator"] = "true",
            ["Relay:Dashboard:ViewerPrincipalIds:0"] = "test-user",
            ["Relay:Connect:AdapterPath"] = Path.Combine(Path.GetTempPath(), "absent-cocern-" + Guid.NewGuid().ToString("N"), "CoCern.Adapter.dll")
        };
        await using var remote = new RelayHostFixture(remoteAddress: IPAddress.Parse("192.0.2.25"), settings: settings);
        using var remoteBrowser = remote.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        remoteBrowser.DefaultRequestHeaders.Add("Origin", "https://localhost");
        Assert.Equal(HttpStatusCode.Unauthorized, (await remoteBrowser.GetAsync("/dashboard/api/connect/repair")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await remoteBrowser.GetAsync("/dashboard/api/connect/diagnostics")).StatusCode);
        (await remoteBrowser.PostAsJsonAsync("/dashboard/api/login", new { token = RelayHostFixture.Token })).EnsureSuccessStatusCode();
        var preview = await remoteBrowser.GetFromJsonAsync<DashboardRepairStatus>("/dashboard/api/connect/repair");
        Assert.NotNull(preview?.Unavailable);
        Assert.Empty(preview!.Checks);
        Assert.Equal(HttpStatusCode.Forbidden, (await remoteBrowser.PostAsync("/dashboard/api/connect/repair", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await remoteBrowser.GetAsync("/dashboard/api/connect/diagnostics")).StatusCode);

        await using var local = new RelayHostFixture(remoteAddress: IPAddress.Loopback, settings: settings);
        using var localBrowser = local.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        localBrowser.DefaultRequestHeaders.Add("Origin", "https://localhost");
        (await localBrowser.PostAsJsonAsync("/dashboard/api/login", new { token = RelayHostFixture.Token })).EnsureSuccessStatusCode();
        localBrowser.DefaultRequestHeaders.Remove("Origin");
        localBrowser.DefaultRequestHeaders.Add("Origin", "https://unrelated.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await localBrowser.PostAsync("/dashboard/api/connect/repair", null)).StatusCode);
    }
}
