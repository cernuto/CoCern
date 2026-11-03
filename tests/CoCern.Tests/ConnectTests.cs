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

    // ---- A pinned entry for this hub follows the hub's current address and certificate ----

    private const string OldPin = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string NewPin = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";
    private const string NewUrl = "https://127.0.0.1:5443/";
    private string ClaudeJson => Path.Combine(Profile, ".claude.json");
    private string Desktop => Path.Combine(AppData, "Claude", "claude_desktop_config.json");
    private string Toml => Path.Combine(Profile, ".codex", "config.toml");
    private string[] Backups(string path) => Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".cocern-backup-*");

    /// <summary>The hub's own record beside its token: where its loopback HTTPS is now and which certificate it presents.</summary>
    private void LocalHubRecord(string url = NewUrl, string fingerprint = NewPin) =>
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(Token)!, "local-hub.json"), JsonSerializer.Serialize(new { url, fingerprint }));

    private static void WriteFile(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }

    private Dictionary<string, string?> States(string output) =>
        JsonDocument.Parse(output).RootElement.GetProperty("clients").EnumerateArray().ToDictionary(client => client.GetProperty("id").GetString()!, client => client.GetProperty("state").GetString());

    [Fact]
    public void A_claude_code_entry_pinned_to_this_hubs_old_certificate_is_updated_and_reported()
    {
        LocalHubRecord();
        var entry = new JsonObject
        {
            ["type"] = "stdio", ["command"] = "custom-dotnet", ["args"] = new JsonArray("custom.dll"),
            ["env"] = new JsonObject { ["COCERN_TOKEN_FILE"] = Token, ["COCERN_URL"] = "https://127.0.0.1:5272/", ["COCERN_FINGERPRINT"] = OldPin, ["COCERN_PROFILE"] = "chat" }
        };
        var original = new JsonObject { ["numStartups"] = 4, ["mcpServers"] = new JsonObject { ["other"] = new JsonObject { ["command"] = "x" }, ["cocern"] = entry } }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        WriteFile(ClaudeJson, original);

        var check = Connect("--check", "--json", "--only", "claude-code");
        Assert.Equal("outdated", States(check.Output)["claude-code"]);
        Assert.Equal(original, File.ReadAllText(ClaudeJson));

        var (code, output, _) = Connect("--json", "--only", "claude-code");
        Assert.Equal(0, code);
        Assert.Equal("updated", States(output)["claude-code"]);
        var after = JsonNode.Parse(File.ReadAllText(ClaudeJson))!;
        var env = after["mcpServers"]!["cocern"]!["env"]!;
        Assert.Equal((NewUrl, NewPin), (env["COCERN_URL"]!.GetValue<string>(), env["COCERN_FINGERPRINT"]!.GetValue<string>()));
        // Everything else in the entry and the file stays as it was, and the old file is kept beside it.
        Assert.Equal((Token, "chat"), (env["COCERN_TOKEN_FILE"]!.GetValue<string>(), env["COCERN_PROFILE"]!.GetValue<string>()));
        Assert.Equal("custom-dotnet", after["mcpServers"]!["cocern"]!["command"]!.GetValue<string>());
        Assert.Equal(4, after["numStartups"]!.GetValue<int>());
        Assert.Equal("x", after["mcpServers"]!["other"]!["command"]!.GetValue<string>());
        Assert.Equal(original, File.ReadAllText(Assert.Single(Backups(ClaudeJson))));

        // Once current, it is connected again, and the text output says what happened before.
        Assert.Equal("connected", States(Connect("--json", "--only", "claude-code").Output)["claude-code"]);
        WriteFile(ClaudeJson, original);
        var text = Connect("--only", "claude-code").Output;
        Assert.Contains("Claude Code: updated", text);
        Assert.Contains("Restart your chats", text);
    }

    [Fact]
    public void A_desktop_entry_on_the_old_plain_http_address_moves_to_the_pinned_one()
    {
        LocalHubRecord();
        Directory.CreateDirectory(Path.Combine(AppData, "Claude"));
        WriteFile(Desktop, "{\r\n  \"mcpServers\": {\r\n    \"cocern\": {\r\n      \"command\": \"dotnet\",\r\n      \"args\": [\"a.dll\"],\r\n      \"env\": { \"COCERN_URL\": \"http://127.0.0.1:5271/\", \"COCERN_TOKEN\": \"inline-secret\" }\r\n    }\r\n  }\r\n}\r\n");

        var (code, output, _) = Connect("--json", "--only", "claude-desktop");

        Assert.Equal(0, code);
        Assert.Equal("updated", States(output)["claude-desktop"]);
        var text = File.ReadAllText(Desktop);
        var env = JsonNode.Parse(text)!["mcpServers"]!["cocern"]!["env"]!;
        Assert.Equal((NewUrl, NewPin, "inline-secret"), (env["COCERN_URL"]!.GetValue<string>(), env["COCERN_FINGERPRINT"]!.GetValue<string>(), env["COCERN_TOKEN"]!.GetValue<string>()));
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
        Assert.Single(Backups(Desktop));
    }

    [Fact]
    public void A_codex_entry_written_by_connect_is_updated_in_place_byte_for_byte_elsewhere()
    {
        LocalHubRecord();
        var head = "# mine\nmodel = \"gpt\"\n\n[mcp_servers.cocern]\ncommand = 'dotnet'\nargs = ['a.dll']\n";
        var tail = "\n\n[mcp_servers.other]\ncommand = 'other' # keep\n";
        WriteFile(Toml, head + $"env = {{ COCERN_TOKEN_FILE = '{Token}', COCERN_URL = 'https://127.0.0.1:5272/', COCERN_FINGERPRINT = '{OldPin}' }}  # pinned" + tail);

        Assert.Equal("outdated", States(Connect("--check", "--json", "--only", "codex").Output)["codex"]);
        var (code, output, _) = Connect("--json", "--only", "codex");

        Assert.Equal(0, code);
        Assert.Equal("updated", States(output)["codex"]);
        Assert.Equal(head + $"env = {{ COCERN_TOKEN_FILE = '{Token}', COCERN_URL = '{NewUrl}', COCERN_FINGERPRINT = '{NewPin}' }}  # pinned" + tail, File.ReadAllText(Toml));
        Assert.True(ClientSettings.CodexHasCoCern(File.ReadAllText(Toml)));
        Assert.Single(Backups(Toml));
        Assert.Equal("connected", States(Connect("--json", "--only", "codex").Output)["codex"]);
    }

    [Fact]
    public void A_codex_entry_with_an_env_table_gains_the_pin_and_keeps_its_other_lines()
    {
        LocalHubRecord();
        var text = $"[mcp_servers.cocern]\r\ncommand = 'dotnet'\r\n\r\n[mcp_servers.cocern.env]\r\nCOCERN_TOKEN_FILE = \"{Token.Replace("\\", "\\\\")}\"\r\nCOCERN_URL = 'http://127.0.0.1:5271/'\r\nCOCERN_PROFILE = 'chat'\r\n";
        WriteFile(Toml, text);

        Assert.Equal("updated", States(Connect("--json", "--only", "codex").Output)["codex"]);

        Assert.Equal(text.Replace("COCERN_URL = 'http://127.0.0.1:5271/'", $"COCERN_URL = '{NewUrl}'") + $"COCERN_FINGERPRINT = '{NewPin}'\r\n", File.ReadAllText(Toml));
    }

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
        Assert.All(states.Values, state => Assert.Equal("connected", state));
        Assert.Equal(foreign, File.ReadAllText(ClaudeJson));
        Assert.Equal(remote, File.ReadAllText(Toml));
        Assert.Equal(node, File.ReadAllText(Desktop));
        Assert.Empty(Backups(ClaudeJson));
    }

    [Fact]
    public void Without_a_current_address_an_existing_entry_is_left_alone_as_before()
    {
        var entry = "{\"mcpServers\":{\"cocern\":{\"command\":\"dotnet\",\"env\":{\"COCERN_TOKEN_FILE\":" + JsonSerializer.Serialize(Token) + ",\"COCERN_URL\":\"https://127.0.0.1:5272/\",\"COCERN_FINGERPRINT\":\"" + OldPin + "\"}}}}";
        WriteFile(ClaudeJson, entry);

        Assert.Equal("connected", States(Connect("--json", "--only", "claude-code").Output)["claude-code"]);
        Assert.Equal(entry, File.ReadAllText(ClaudeJson));
    }

    [Theory]
    [InlineData("outdated", "available")]
    [InlineData("updated", "added")]
    [InlineData("connected", "connected")]
    [InlineData("error", "error")]
    public void The_connect_page_shows_the_new_outcomes_as_states_it_knows(string adapter, string page)
    {
        var client = new DashboardConnectClient { Id = "codex", Name = "Codex", State = adapter, Detail = "detail" };
        Assert.Equal(client with { State = page }, CoCern.Host.Dashboard.DashboardConnect.PageState(client));
    }

    private static StringContent JsonContent(object value) => new(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json");
}
