using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using CoCern.Contracts.Dashboard;
using CoCern.Host;
using CoCern.Host.Dashboard;
using Xunit;

namespace CoCern.Tests;

/// <summary>The dashboard's "Add another PC": the filled-in setup command, shown only on the hub's PC.</summary>
public sealed class AddPcTests : IDisposable
{
    private const string Fingerprint = "AB12CD34AB12CD34AB12CD34AB12CD34AB12CD34AB12CD34AB12CD34AB12CD34";
    private readonly string root = Path.Combine(Path.GetTempPath(), "cocern-add-pc-" + Guid.NewGuid().ToString("N"));
    private string Adapter => Path.Combine(root, "artifacts", "adapter", "CoCern.Adapter.dll");

    public AddPcTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Adapter)!);
        File.WriteAllText(Adapter, "");
    }

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    private DashboardAddPcStatus Status(bool discovery, (int, string)? https, string? adapter = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Relay:Discovery:Enabled"] = discovery.ToString(), ["Relay:Discovery:JoinCode"] = "JOIN-CODE-1234", ["Relay:Connect:AdapterPath"] = adapter ?? Adapter
        }).Build();
        var environment = new HostingEnvironment { ContentRootPath = root };
        var services = new ServiceCollection().AddSingleton(HubJoinCode.Load(configuration, environment)).BuildServiceProvider();
        return new DashboardAddPc(configuration, services, new HubListener { Https = https }, new DashboardConnect(configuration, environment, null!)).Status();
    }

    [Fact]
    public void The_command_is_filled_in_with_this_hubs_address_join_code_and_fingerprint()
    {
        var status = Status(discovery: true, (5273, Fingerprint));
        Assert.Null(status.Unavailable);
        Assert.Equal(Path.GetDirectoryName(Adapter), status.AdapterFolder);
        var byName = status.Addresses[0];
        Assert.Equal($"https://{Environment.MachineName}:5273/", byName.Url);
        Assert.All(status.Addresses, address =>
        {
            Assert.Contains($"{address.Url}join/adapter.zip", address.Command);
            Assert.Contains($"--setup --hub {address.Url} --join-code JOIN-CODE-1234 --fingerprint {Fingerprint}", address.Command);
            Assert.Contains("CoCern.Adapter.exe", address.Command);
            Assert.Contains("CoCern.Adapter.dll", address.Command);
            Assert.DoesNotContain(".\\CoCern.Adapter.dll --setup", address.Command);
        });
        Assert.Contains("-LocalPort 5273", status.FirewallCommand);
    }

    [Fact]
    public void The_one_paste_command_pins_the_hub_certificate_by_its_sha256_fingerprint()
    {
        var command = Status(discovery: true, (5273, Fingerprint)).Addresses[0].Command;
        Assert.DoesNotContain("\n", command);
        Assert.Contains("ServerCertificateValidationCallback", command);
        Assert.Contains("SHA256", command);
        Assert.Contains("OrdinalIgnoreCase", command);
        // Once to pin the download, once for --fingerprint.
        Assert.True(command.Split(Fingerprint).Length - 1 >= 2, "the fingerprint should pin the download as well as the join");
        Assert.Contains("$env:TEMP", command);
        Assert.Contains("$env:LOCALAPPDATA\\CoCern\\adapter", command);
        Assert.Contains("Expand-Archive", command);
        Assert.DoesNotContain("SkipCertificateCheck", command);
    }

    private static RelayHostFixture ZipHost(bool discovery, string adapterPath, IPAddress? remote = null) => new(remoteAddress: remote, settings: new Dictionary<string, string?>
    {
        ["Relay:Discovery:Enabled"] = discovery ? "true" : "false", ["Relay:Connect:AdapterPath"] = adapterPath
    });

    private static HttpClient Client(RelayHostFixture host, string scheme = "https") => host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(scheme + "://localhost"), AllowAutoRedirect = false });

    [Fact]
    public async Task The_zip_endpoint_streams_the_adapter_folder_without_a_dashboard_sign_in()
    {
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(Adapter)!, "CoCern.Adapter.runtimeconfig.json"), "{}");
        await using var host = ZipHost(discovery: true, Adapter, remote: IPAddress.Parse("192.168.1.20"));
        using var client = Client(host);
        using var response = await client.GetAsync("/join/adapter.zip");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("CoCern.Adapter.zip", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        using var zip = new System.IO.Compression.ZipArchive(await response.Content.ReadAsStreamAsync(), System.IO.Compression.ZipArchiveMode.Read);
        Assert.Contains("CoCern.Adapter.dll", zip.Entries.Select(entry => entry.FullName));
        Assert.Contains("CoCern.Adapter.runtimeconfig.json", zip.Entries.Select(entry => entry.FullName));
        Assert.DoesNotContain(zip.Entries, entry => entry.FullName.Contains("CoCern.Host"));
    }

    [Fact]
    public async Task The_zip_endpoint_is_404_without_the_adapter_folder()
    {
        await using var host = ZipHost(discovery: true, Path.Combine(root, "missing", "CoCern.Adapter.dll"));
        using var client = Client(host);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/join/adapter.zip")).StatusCode);
    }

    [Fact]
    public async Task The_zip_endpoint_does_not_exist_while_discovery_is_off()
    {
        await using var host = ZipHost(discovery: false, Adapter);
        using var client = Client(host);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/join/adapter.zip")).StatusCode);
    }

    [Fact]
    public async Task The_zip_endpoint_refuses_plain_http_from_another_pc()
    {
        await using var host = ZipHost(discovery: true, Adapter, remote: IPAddress.Parse("192.168.1.20"));
        using var client = Client(host, scheme: "http");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/join/adapter.zip")).StatusCode);
    }

    [Fact]
    public void It_says_what_to_turn_on_or_build_first()
    {
        Assert.Contains("Relay:Discovery:Enabled", Status(discovery: false, (5273, Fingerprint)).Unavailable);
        Assert.Contains("Relay:Network:Enabled", Status(discovery: true, https: null).Unavailable);
        Assert.Contains("adapter was not found", Status(discovery: true, (5273, Fingerprint), adapter: Path.Combine(root, "missing.dll")).Unavailable);
    }

    [Fact]
    public async Task Only_a_viewer_on_the_hubs_pc_sees_the_join_code()
    {
        var settings = new Dictionary<string, string?> { ["Relay:Clients:0:IsAdministrator"] = "true", ["Relay:Dashboard:ViewerPrincipalIds:0"] = "test-user", ["Relay:Discovery:Enabled"] = "true" };
        await using var host = new RelayHostFixture(remoteAddress: IPAddress.Parse("192.168.1.20"), settings: settings);
        using var browser = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        browser.DefaultRequestHeaders.Add("Origin", "https://localhost");
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/dashboard/api/add-pc")).StatusCode);
        (await browser.PostAsync("/dashboard/api/login", new StringContent(JsonSerializer.Serialize(new { token = RelayHostFixture.Token }), System.Text.Encoding.UTF8, "application/json"))).EnsureSuccessStatusCode();
        var body = await browser.GetStringAsync("/dashboard/api/add-pc");
        Assert.DoesNotContain(RelayHostFixture.JoinCode, body);
        Assert.Contains("on the hub's PC", JsonSerializer.Deserialize<DashboardAddPcStatus>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Unavailable);
    }
}
