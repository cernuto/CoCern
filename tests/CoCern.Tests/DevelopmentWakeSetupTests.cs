using System.Text.Json.Nodes;
using CoCern.Adapter.Development;
using CoCern.Adapter.Setup;
using CoCern.Contracts.Dashboard;
using Xunit;

namespace CoCern.Tests;

public sealed class DevelopmentWakeSetupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "cocern-development-wake-" + Guid.NewGuid().ToString("N"));
    private string Config => Path.Combine(root, ".claude.json");
    private string Hooks => Path.Combine(root, ".claude", "settings.json");
    private string Token => Path.Combine(root, "token");
    private string Deployment => Path.Combine(root, "deployment");
    private string Launcher(char version) => Path.Combine(Deployment, "versions", new string(version, 64), "CoCern.Adapter.dll");

    public DevelopmentWakeSetupTests() => Directory.CreateDirectory(root);

    [Fact]
    public void Development_setup_installs_and_refreshes_the_owned_stop_hook_without_changing_other_hooks()
    {
        WriteConfig();
        Directory.CreateDirectory(Path.GetDirectoryName(Hooks)!);
        File.WriteAllText(Hooks, """{"permissions":{"allow":["Read"]},"hooks":{"Stop":[{"hooks":[{"type":"command","command":"user-owned-check"}]}]}}""");
        Assert.True(Configure('a'));
        var handlers = Handlers();
        Assert.Equal(2, handlers.Length);
        Assert.Contains(handlers, handler => handler["command"]!.GetValue<string>() == "user-owned-check");
        var owned = Assert.Single(handlers, handler => handler["command"]!.GetValue<string>().Contains("--stop-hook", StringComparison.Ordinal));
        Assert.Contains(Launcher('a'), owned["command"]!.GetValue<string>());
        Assert.True(owned["asyncRewake"]!.GetValue<bool>());
        Assert.True(owned["timeout"]!.GetValue<int>() > 60);
        var once = File.ReadAllText(Hooks);
        Assert.False(Configure('a'));
        Assert.Equal(once, File.ReadAllText(Hooks));

        // The persistent MCP launcher stays in place while the one-shot hook follows the newest helper.
        Directory.CreateDirectory(Path.GetDirectoryName(Launcher('a'))!);
        File.WriteAllText(Launcher('a'), "immutable launcher fixture");
        Assert.True(Configure('b'));
        var refreshed = Assert.Single(Handlers(), handler => handler["command"]!.GetValue<string>().Contains("--stop-hook", StringComparison.Ordinal));
        Assert.Contains(Launcher('b'), refreshed["command"]!.GetValue<string>());
        Assert.Equal("Read", JsonNode.Parse(File.ReadAllText(Hooks))!["permissions"]!["allow"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Development_setup_without_a_claude_entry_creates_no_hook_settings()
    {
        File.WriteAllText(Config, "{\"mcpServers\":{\"other\":{\"command\":\"other\"}}}");
        var before = File.ReadAllText(Config);
        Assert.False(Configure('a'));
        Assert.Equal(before, File.ReadAllText(Config));
        Assert.False(File.Exists(Hooks));
    }

    [Fact]
    public void Malformed_hook_settings_leave_the_mcp_configuration_unchanged()
    {
        WriteConfig();
        var before = File.ReadAllText(Config);
        Directory.CreateDirectory(Path.GetDirectoryName(Hooks)!);
        File.WriteAllText(Hooks, "{\"hooks\":false}");
        Assert.Throws<IOException>(() => Configure('a'));
        Assert.Equal(before, File.ReadAllText(Config));
        Assert.Equal("{\"hooks\":false}", File.ReadAllText(Hooks));
    }

    [Fact]
    public void Local_connect_installs_the_stop_hook_but_a_check_and_codex_only_connect_do_not()
    {
        File.WriteAllText(Token, "test-only-connection-token");
        using var output = new StringWriter();
        using var error = new StringWriter();
        int Connect(params string[] args) => ConnectCommand.Run(["--token-file", Token, .. args], root, null, "dotnet-host", Launcher('a'), output, error, nativeCacheRoot: Path.Combine(root, "native-cache"));
        Assert.Equal(0, Connect("--check"));
        Assert.False(File.Exists(Hooks));
        Assert.Equal(0, Connect("--only", ConnectClients.Codex));
        Assert.False(File.Exists(Hooks));
        Assert.Equal(0, Connect("--only", ConnectClients.ClaudeCode));
        var owned = Assert.Single(Handlers());
        Assert.Contains("--stop-hook", owned["command"]!.GetValue<string>());
        var once = File.ReadAllText(Hooks);
        Assert.Equal(0, Connect("--only", ConnectClients.ClaudeCode));
        Assert.Equal(once, File.ReadAllText(Hooks));
    }

    private bool Configure(char version) => DevelopmentSetup.ConfigureClaudeCode(Config, "dotnet-host", Launcher(version), Deployment, Token, nativeCacheRoot: Path.Combine(root, "native-cache"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Local_connect_does_not_install_a_hook_for_another_hub_or_an_existing_node_entry(bool node)
    {
        File.WriteAllText(Token, "test-only-connection-token");
        WriteConfig();
        var config = JsonNode.Parse(File.ReadAllText(Config))!;
        config["mcpServers"]!["cocern"]!["env"] = node ? new JsonObject() : new JsonObject
        {
            ["COCERN_TOKEN_FILE"] = Path.Combine(root, "another-hub", "token"), ["COCERN_URL"] = "https://another.example/"
        };
        File.WriteAllText(Config, config.ToJsonString());
        var before = File.ReadAllText(Config);
        Assert.Equal(0, ConnectCommand.Run(["--token-file", Token, "--only", ConnectClients.ClaudeCode], root, null, "dotnet-host", Launcher('a'), TextWriter.Null, TextWriter.Null, nativeCacheRoot: Path.Combine(root, "native-cache")));
        Assert.Equal(before, File.ReadAllText(Config));
        Assert.False(File.Exists(Hooks));
    }

    private void WriteConfig() => File.WriteAllText(Config, new JsonObject
    {
        ["mcpServers"] = new JsonObject
        {
            ["cocern"] = new JsonObject
            {
                ["command"] = "old-host", ["args"] = new JsonArray("old-adapter.dll"),
                ["env"] = new JsonObject { ["COCERN_TOKEN_FILE"] = Token, ["COCERN_URL"] = "https://127.0.0.1:5272/" }
            }
        }
    }.ToJsonString());

    private JsonObject[] Handlers() => JsonNode.Parse(File.ReadAllText(Hooks))!["hooks"]!["Stop"]!.AsArray()
        .SelectMany(group => group!["hooks"]!.AsArray()).Select(handler => handler!.AsObject()).ToArray();

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
