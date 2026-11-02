using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Adapter.Setup;
using CoCern.Contracts.Dashboard;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace CoCern.Tests;

/// <summary>Adding CoCern to the hub PC's own apps: the adapter's <c>--connect</c> and the dashboard's Connect page.</summary>
public sealed class ConnectTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "cocern-connect-" + Guid.NewGuid().ToString("N"));
    private string Profile => Path.Combine(root, "profile");
    private string AppData => Path.Combine(root, "appdata");
    private string Adapter => Path.Combine(root, "repo", "artifacts", "adapter", "CoCern.Adapter.dll");
    private string Token => Path.Combine(root, "repo", "data", "local-client-token");

    public ConnectTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Adapter)!);
        Directory.CreateDirectory(Path.GetDirectoryName(Token)!);
        File.WriteAllText(Token, "secret");
    }

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private (int Code, string Output, string Error) Connect(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = ConnectCommand.Run(args, Profile, AppData, "dotnet", Adapter, output, error);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void Connect_finds_the_hub_token_and_adds_cocern_to_claude_code_and_codex_once()
    {
        var (code, output, _) = Connect();
        Assert.Equal(0, code);
        Assert.Contains("Claude Code: connected", output);
        Assert.Contains("Codex: connected", output);
        var claude = JsonNode.Parse(File.ReadAllText(Path.Combine(Profile, ".claude.json")))!["mcpServers"]!["cocern"]!;
        Assert.Equal(Token, claude["env"]!["COCERN_TOKEN_FILE"]!.GetValue<string>());
        Assert.Equal(Adapter, claude["args"]![0]!.GetValue<string>());
        Assert.Contains($"env = {{ COCERN_TOKEN_FILE = '{Token}' }}", File.ReadAllText(Path.Combine(Profile, ".codex", "config.toml")));
        // The desktop app is not installed here, so it is not offered.
        Assert.DoesNotContain("desktop", output);
        Assert.Contains("already connected", Connect().Output);
    }

    [Fact]
    public void Check_reports_without_writing_and_the_desktop_app_is_offered_once_installed()
    {
        Directory.CreateDirectory(Path.Combine(AppData, "Claude"));
        var (code, output, _) = Connect("--check", "--json");
        Assert.Equal(0, code);
        var clients = JsonDocument.Parse(output).RootElement.GetProperty("clients").EnumerateArray().Select(client => (client.GetProperty("id").GetString(), client.GetProperty("state").GetString())).ToArray();
        Assert.Equal([("claude-code", "available"), ("claude-desktop", "available"), ("codex", "available")], clients);
        Assert.False(Directory.Exists(Profile));
        Assert.Equal(0, Connect("--only", "claude-desktop").Code);
        var desktop = JsonNode.Parse(File.ReadAllText(Path.Combine(AppData, "Claude", "claude_desktop_config.json")))!["mcpServers"]!["cocern"]!;
        Assert.Null(desktop["type"]);
        Assert.Equal(Token, desktop["env"]!["COCERN_TOKEN_FILE"]!.GetValue<string>());
        Assert.False(File.Exists(Path.Combine(Profile, ".claude.json")));
    }

    [Fact]
    public void A_settings_file_it_cannot_read_is_reported_and_left_alone()
    {
        Directory.CreateDirectory(Profile);
        File.WriteAllText(Path.Combine(Profile, ".claude.json"), "{ not json");
        var (code, output, _) = Connect("--json");
        Assert.Equal(SetupCommand.MalformedClientFileExitCode, code);
        var states = JsonDocument.Parse(output).RootElement.GetProperty("clients").EnumerateArray().ToDictionary(client => client.GetProperty("id").GetString()!, client => client.GetProperty("state").GetString());
        Assert.Equal("error", states["claude-code"]);
        Assert.Equal("added", states["codex"]);
        Assert.Equal("{ not json", File.ReadAllText(Path.Combine(Profile, ".claude.json")));
    }

    [Fact]
    public void Without_a_token_file_it_says_how_to_get_one()
    {
        File.Delete(Token);
        var (code, _, error) = Connect();
        Assert.Equal(1, code);
        Assert.Contains("Start the hub once", error);
    }

    [Fact]
    public async Task The_dashboard_connects_apps_only_for_a_viewer_on_the_hub_pc()
    {
        var settings = new Dictionary<string, string?> { ["Relay:Clients:0:IsAdministrator"] = "true", ["Relay:Dashboard:ViewerPrincipalIds:0"] = "test-user", ["Relay:Connect:AdapterPath"] = Path.Combine(root, "missing.dll") };
        foreach (var (address, onThisPc) in new[] { (IPAddress.Parse("192.168.1.20"), false), (IPAddress.Loopback, true) })
        {
            await using var host = new RelayHostFixture(remoteAddress: address, settings: settings);
            using var browser = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
            browser.DefaultRequestHeaders.Add("Origin", "https://localhost");
            Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/dashboard/api/connect")).StatusCode);
            (await browser.PostAsync("/dashboard/api/login", JsonContent(new { token = RelayHostFixture.Token }))).EnsureSuccessStatusCode();
            var status = JsonSerializer.Deserialize<DashboardConnectStatus>(await browser.GetStringAsync("/dashboard/api/connect"), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.Contains(onThisPc ? "adapter was not found" : "on the hub's PC", status.Unavailable);
            Assert.Equal(onThisPc ? HttpStatusCode.OK : HttpStatusCode.Forbidden, (await browser.PostAsync("/dashboard/api/connect/codex", null)).StatusCode);
            if (onThisPc) Assert.Equal(HttpStatusCode.NotFound, (await browser.PostAsync("/dashboard/api/connect/anything", null)).StatusCode);
        }
    }

    private static StringContent JsonContent(object value) => new(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json");
}
