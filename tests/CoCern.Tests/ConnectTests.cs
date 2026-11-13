using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Adapter.Setup;
using CoCern.Adapter.Node;
using CoCern.Contracts.Dashboard;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
        File.WriteAllBytes(Adapter, []);
        Directory.CreateDirectory(Path.GetDirectoryName(Token)!);
        NodeState.WritePrivately(Token, System.Text.Encoding.UTF8.GetBytes("secret"));
    }

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    [Fact]
    public void A_refresh_adds_a_missing_repository_but_never_moves_one_or_touches_another_hubs_entry()
    {
        Dictionary<string, string> Entry(string? repository = null)
        {
            var entry = new Dictionary<string, string> { ["COCERN_TOKEN_FILE"] = Token };
            if (repository is not null) entry["COCERN_REPOSITORY"] = repository;
            return entry;
        }

        Assert.Equal("cocern", ConnectCommand.Refresh(Entry(), Token, null, null, null, repository: "cocern")!["COCERN_REPOSITORY"]);
        Assert.Null(ConnectCommand.Refresh(Entry("chosen"), Token, null, null, null, repository: "cocern"));
        Assert.Null(ConnectCommand.Refresh(Entry(), Token, null, null, null));
        var foreign = new Dictionary<string, string> { ["COCERN_TOKEN_FILE"] = Path.Combine(root, "elsewhere", "token") };
        Assert.Null(ConnectCommand.Refresh(foreign, Token, null, null, null, repository: "cocern"));
    }

    private (int Code, string Output, string Error) Connect(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = ConnectCommand.Run(args, Profile, AppData, "dotnet", Adapter, output, error, nativeCacheRoot: Path.Combine(root, "native-cache"));
        return (code, output.ToString(), error.ToString());
    }

    private const string CustomHttp = "http://127.0.0.1:6123/";

    [Theory]
    [InlineData("http://127.0.0.1:7123/", true)]
    [InlineData("https://127.0.0.1:7123/", false)]
    public void Foreign_custom_loopback_origin_cannot_claim_an_inline_credential(string otherHub, bool preferHttp)
    {
        LocalHubRecord(httpUrl: preferHttp ? CustomHttp : null);
        var variables = new Dictionary<string, string> { ["COCERN_URL"] = otherHub, ["COCERN_TOKEN"] = "other-hub-test-token" };
        if (!preferHttp) variables["COCERN_FINGERPRINT"] = NewPin;
        AssertEntryLeftAlone(variables);
    }

    private void AssertEntryLeftAlone(Dictionary<string, string> variables)
    {
        WriteFile(ClaudeJson, JsonSerializer.Serialize(new { mcpServers = new { cocern = new { command = "dotnet", env = variables } } }));
        var inline = string.Join(", ", variables.Select(pair => $"{pair.Key} = '{pair.Value}'"));
        WriteFile(Toml, $"# keep\n[mcp_servers.cocern]\ncommand = 'dotnet'\nenv = {{ {inline} }}\n");
        var json = File.ReadAllBytes(ClaudeJson);
        var toml = File.ReadAllBytes(Toml);
        foreach (var args in new[] { new[] { "--check", "--json", "--role", "admin" }, new[] { "--json", "--role", "admin" } })
        {
            var result = Connect(args);
            Assert.Equal(0, result.Code);
            Assert.All(States(result.Output).Values, state => Assert.Equal("other-hub", state));
            Assert.Equal(json, File.ReadAllBytes(ClaudeJson));
            Assert.Equal(toml, File.ReadAllBytes(Toml));
            Assert.Empty(Backups(ClaudeJson));
            Assert.Empty(Backups(Toml));
            Assert.False(File.Exists(Path.Combine(Profile, ".claude", "settings.json")));
        }
    }

    [Theory]
    [InlineData("https://hub.example:7443/")]
    [InlineData("not-an-origin")]
    public void Local_refresh_never_claims_an_entry_with_an_unsafe_supplied_origin(string url)
    {
        LocalHubRecord(httpUrl: CustomHttp);
        // Same token file in JSON; matching certificate without any token-file path in TOML.
        var json = JsonSerializer.Serialize(new { mcpServers = new { cocern = new { command = "dotnet", env = new { COCERN_URL = url, COCERN_TOKEN_FILE = Token } } } });
        var toml = $"[mcp_servers.cocern]\ncommand = 'dotnet'\nenv = {{ COCERN_URL = '{url}', COCERN_FINGERPRINT = '{NewPin}' }}\n";
        WriteFile(ClaudeJson, json);
        WriteFile(Toml, toml);
        Assert.Equal(0, Connect("--role", "admin").Code);
        Assert.Equal(json, File.ReadAllText(ClaudeJson));
        Assert.Equal(toml, File.ReadAllText(Toml));
    }

    [Fact]
    public void Connect_finds_the_hub_token_and_adds_cocern_to_claude_code_and_codex_once()
    {
        var (code, output, error) = Connect();
        Assert.True(code == 0, error);
        Assert.Contains("Claude Code: connected", output);
        Assert.Contains("Codex: connected", output);
        var claude = JsonNode.Parse(File.ReadAllText(Path.Combine(Profile, ".claude.json")))!["mcpServers"]!["cocern"]!;
        Assert.Equal(Token, claude["env"]!["COCERN_TOKEN_FILE"]!.GetValue<string>());
        Assert.Equal(Adapter, claude["args"]![0]!.GetValue<string>());
        Assert.Contains($"env = {{ COCERN_TOKEN_FILE = '{Token}', COCERN_ON_DEMAND_TOOLS = 'true' }}", File.ReadAllText(Path.Combine(Profile, ".codex", "config.toml")));
        Assert.DoesNotContain("--native-client", File.ReadAllText(Path.Combine(Profile, ".codex", "config.toml")));
        Assert.False(File.Exists(Path.Combine(Profile, ".codex", "hooks.json")));
        using var status = JsonDocument.Parse(Connect("--check", "--json", "--only", "codex").Output);
        var readiness = status.RootElement.GetProperty("clients")[0].GetProperty("readiness");
        Assert.Equal("standard", readiness.GetProperty("state").GetString());
        Assert.Equal("Standard connection configured", readiness.GetProperty("summary").GetString());
        Assert.Equal("Open or resume a chat to use CoCern.", readiness.GetProperty("nextAction").GetString());
        Assert.True(readiness.GetProperty("skillInstalled").GetBoolean());
        Assert.False(readiness.GetProperty("hooksInstalled").GetBoolean());
        // The desktop app is not installed here, so it is not offered.
        Assert.DoesNotContain("desktop", output);
        Assert.Contains("already connected", Connect().Output);
    }

    [Fact]
    public void Connect_repairs_owned_standard_launch_and_preserves_choices_when_optional_setup_is_unavailable()
    {
        var config = Path.Combine(Profile, ".codex", "config.toml");
        ClientSettings.PlanCodex(config, "removed-dotnet", Path.Combine(root, "removed-adapter.dll"), new Dictionary<string, string>
            { ["COCERN_TOKEN_FILE"] = Token, ["COCERN_ROLE"] = "admin", ["COCERN_PROFILE"] = "diagnostics" }).Apply(TimeProvider.System);
        var stale = File.ReadAllBytes(config);
        using var preview = JsonDocument.Parse(Connect("--only", "codex", "--check", "--json").Output);
        Assert.Equal(ConnectStates.Outdated, preview.RootElement.GetProperty("clients")[0].GetProperty("state").GetString());
        Assert.Equal("setup_required", preview.RootElement.GetProperty("clients")[0].GetProperty("readiness").GetProperty("state").GetString());
        Assert.Equal(stale, File.ReadAllBytes(config));
        Assert.Equal(0, Connect("--only", "codex").Code);
        Assert.True(ClientSettings.IsStandardCodexLaunchAvailable(config));
        Assert.Contains("COCERN_ROLE = 'admin'", File.ReadAllText(config));
        Assert.Contains("COCERN_PROFILE = 'diagnostics'", File.ReadAllText(config));
        var original = File.ReadAllBytes(config);
        var hooks = Path.Combine(Profile, ".codex", "hooks.json");
        const string disabled = "{\"disableAllHooks\":true,\"hooks\":{\"SessionStart\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"retained --native-hook codex\",\"disabled\":true}]}]}}";
        File.WriteAllText(hooks, disabled);
        File.WriteAllText(CodexNativeSetup.SkillPath(Profile), "user-owned workflow changes");
        var blockedCache = Path.Combine(root, "blocked-cache"); File.WriteAllText(blockedCache, "not a directory");
        using var output = new StringWriter(); using var error = new StringWriter();
        var code = ConnectCommand.Run(["--only", "codex", "--json"], Profile, AppData, "dotnet", Adapter, output, error, nativeCacheRoot: blockedCache);
        Assert.Equal(0, code);
        Assert.Equal(original, File.ReadAllBytes(config));
        Assert.Equal(disabled, File.ReadAllText(hooks));
        Assert.Equal("user-owned workflow changes", File.ReadAllText(CodexNativeSetup.SkillPath(Profile)));
        Assert.Equal("secret", File.ReadAllText(Token));
        using var status = JsonDocument.Parse(output.ToString());
        var readiness = status.RootElement.GetProperty("clients")[0].GetProperty("readiness");
        Assert.Equal("standard", readiness.GetProperty("state").GetString());
        Assert.False(readiness.GetProperty("skillInstalled").GetBoolean());
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

    // ---- A pinned entry for this hub follows the hub's current address and certificate ----

    private const string OldPin = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string NewPin = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";
    private const string NewUrl = "https://127.0.0.1:5443/";
    private string ClaudeJson => Path.Combine(Profile, ".claude.json");
    private string Desktop => Path.Combine(AppData, "Claude", "claude_desktop_config.json");
    private string Toml => Path.Combine(Profile, ".codex", "config.toml");
    private string[] Backups(string path) => Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".cocern-backup-*");

    /// <summary>The hub's own record beside its token: where its loopback HTTPS is now and which certificate it presents.</summary>
    private void LocalHubRecord(string url = NewUrl, string fingerprint = NewPin, string? httpUrl = null) =>
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(Token)!, "local-hub.json"), httpUrl is null ? JsonSerializer.Serialize(new { url, fingerprint }) : JsonSerializer.Serialize(new { url, fingerprint, httpUrl }));

    private static void WriteFile(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }

    private Dictionary<string, string?> States(string output) =>
        JsonDocument.Parse(output).RootElement.GetProperty("clients").EnumerateArray().ToDictionary(client => client.GetProperty("id").GetString()!, client => client.GetProperty("state").GetString());

    [Fact]
    public void Entries_for_another_hub_or_a_joined_pcs_node_are_left_alone()
    {
        LocalHubRecord();
        var elsewhere = Path.Combine(root, "other-hub", "data", "local-client-token");
        var foreign = new JsonObject { ["mcpServers"] = new JsonObject { ["cocern"] = new JsonObject { ["command"] = "dotnet", ["env"] = new JsonObject { ["COCERN_TOKEN_FILE"] = elsewhere, ["COCERN_URL"] = "https://127.0.0.1:6000/", ["COCERN_FINGERPRINT"] = OldPin } } } }.ToJsonString();
        WriteFile(ClaudeJson, foreign);
        // A remote hub's entry with an inline token, and a node entry (no CoCern variables at all, so it runs on the node's credential).
        var remote = $"[mcp_servers.cocern]\ncommand = 'dotnet'\nenv = {{ COCERN_URL = 'https://hub.example/', COCERN_TOKEN = 'x', COCERN_FINGERPRINT = '{OldPin}' }}\n";
        WriteFile(Toml, remote);
        Directory.CreateDirectory(Path.Combine(AppData, "Claude"));
        var node = "{\"mcpServers\":{\"cocern\":{\"command\":\"C:\\\\CoCern\\\\CoCern.Adapter.exe\",\"args\":[]}}}";
        WriteFile(Desktop, node);

        var states = States(Connect("--json").Output);

        Assert.Equal(3, states.Count);
        Assert.All(states.Values, state => Assert.Equal("other-hub", state));
        Assert.Equal(foreign, File.ReadAllText(ClaudeJson));
        Assert.Equal(remote, File.ReadAllText(Toml));
        Assert.Equal(node, File.ReadAllText(Desktop));
        Assert.Empty(Backups(ClaudeJson));
    }

    private static StringContent JsonContent(object value) => new(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json");
}
