using System.Text;
using System.Text.Json.Nodes;
using PkRelay.Adapter;
using PkRelay.Adapter.Node;
using PkRelay.Adapter.Setup;
using Xunit;

namespace PkRelay.Tests;

public sealed class SetupTests : IDisposable
{
    private static readonly Uri Hub = new("http://127.0.0.1:5271");
    private const string Dotnet = @"C:\Program Files\dotnet\dotnet.exe";
    private const string Dll = @"C:\Apps\PkRelay Node\PkRelay.Adapter.dll";
    private const string MalformedToml = "[mcp_servers.other\ncommand = 'x'\n";
    private const string MalformedJson = "{ \"mcpServers\": { ";
    private readonly string root = Path.Combine(Path.GetTempPath(), "pkrelay-setup-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRunKey runKey = new();
    private readonly List<string> started = [];
    private readonly StringWriter output = new();
    private readonly StringWriter error = new();

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private string Profile => Path.Combine(root, "profile");
    private string DataDir => Path.Combine(root, "node data");
    private string Toml => Path.Combine(Profile, ".codex", "config.toml");
    private string ClaudeJson => Path.Combine(Profile, ".claude.json");
    private string[] Backups => Directory.Exists(Profile) ? Directory.GetFiles(Profile, "*.pkrelay-backup-*", SearchOption.AllDirectories) : [];

    private static RelayHostFixture NewHub() => new(remoteAddress: System.Net.IPAddress.Loopback, settings: new Dictionary<string, string?> { ["Relay:Discovery:Enabled"] = "true", ["Relay:Discovery:Repositories:0"] = "repo" });

    private SetupEnvironment Env(RelayHostFixture host, TimeSpan? timeout = null) => new()
    {
        Profile = Profile,
        DotnetPath = Dotnet,
        AdapterDll = Dll,
        RunKey = runKey,
        StartNode = (dotnet, dll, dataDir) => started.Add($"{dotnet}|{dll}|{dataDir}"),
        Transport = new NodeTransport
        {
            CreateHandler = (_, _) => host.Server.CreateHandler(),
            HubHandler = () => host.Server.CreateHandler(),
            MachineName = "Setup PC",
            Chats = _ => Task.FromResult(new JsonArray()),
            FirstRetry = TimeSpan.FromMilliseconds(10),
            MaxRetry = TimeSpan.FromMilliseconds(20),
            CodexExecutable = () => null
        },
        EnrollTimeout = timeout ?? TimeSpan.FromSeconds(20),
        Out = output,
        Error = error
    };

    private Task<int> Run(RelayHostFixture host, bool skip = false, TimeSpan? timeout = null) =>
        SetupCommand.RunAsync(new SetupOptions(Hub, skip, DataDir), Env(host, timeout), CancellationToken.None);

    private static void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text, new UTF8Encoding(false)); }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Malformed_settings_file_is_left_unchanged(bool codex)
    {
        await using var host = NewHub();
        var bad = codex ? Toml : ClaudeJson;
        var good = codex ? ClaudeJson : Toml;
        Write(bad, codex ? MalformedToml : MalformedJson);
        Write(good, codex ? "{\"theme\": \"dark\"}\n" : "# mine\n");
        var (badBytes, goodBytes) = (File.ReadAllBytes(bad), File.ReadAllBytes(good));

        var code = await Run(host);

        Assert.Equal(5, code);
        Assert.Equal(badBytes, File.ReadAllBytes(bad));
        Assert.Equal(goodBytes, File.ReadAllBytes(good));
        Assert.Contains(bad, error.ToString());
        Assert.Empty(Backups);
        Assert.Empty(runKey.Values);
        Assert.Empty(started);
        Assert.False(Directory.Exists(DataDir));
    }

    [Fact]
    public async Task Setup_is_idempotent()
    {
        await using var host = NewHub();
        Write(Toml, "model = \"x\"\n");
        Write(ClaudeJson, "{\n  \"numStartups\": 3\n}\n");

        Assert.Equal(0, await Run(host));
        var backups = Backups;
        Assert.Equal(2, backups.Length);
        var (toml, json, logon) = (File.ReadAllBytes(Toml), File.ReadAllBytes(ClaudeJson), runKey.Values["PkRelayNode"]);

        Assert.Equal(0, await Run(host));

        Assert.Equal(backups.Order(), Backups.Order());
        Assert.Equal(toml, File.ReadAllBytes(Toml));
        Assert.Equal(json, File.ReadAllBytes(ClaudeJson));
        Assert.Equal(logon, runKey.Values["PkRelayNode"]);
        Assert.Single(runKey.Values);
    }

    [Fact]
    public async Task Existing_settings_are_preserved()
    {
        await using var host = NewHub();
        var original = "# my codex settings\r\nmodel = \"gpt\"  # keep me\r\n\r\n[mcp_servers.roslyn]\r\ncommand = 'roslyn.exe'\r\nargs = [\r\n  \"a\",\r\n  \"b\"\r\n]\r\n";
        Write(Toml, original);
        var json = "{\n  \"numStartups\": 12,\n  \"big\": 12345678901234567890,\n  \"projects\": { \"C:\\\\x\": { \"allowedTools\": [] } },\n  \"mcpServers\": { \"other\": { \"type\": \"stdio\", \"command\": \"x\" } }\n}\n";
        Write(ClaudeJson, json);

        Assert.Equal(0, await Run(host));

        var toml = File.ReadAllText(Toml);
        Assert.StartsWith(original, toml);
        Assert.Contains("[mcp_servers.pkrelay]\r\ncommand = 'C:\\Program Files\\dotnet\\dotnet.exe'\r\nargs = ['C:\\Apps\\PkRelay Node\\PkRelay.Adapter.dll']\r\n", toml);
        Assert.DoesNotContain("\n", toml.Replace("\r\n", ""));
        var edited = JsonNode.Parse(File.ReadAllText(ClaudeJson))!;
        var before = JsonNode.Parse(json)!;
        Assert.Equal(12, edited["numStartups"]!.GetValue<int>());
        Assert.Contains("12345678901234567890", File.ReadAllText(ClaudeJson));
        Assert.Equal(before["projects"]!.ToJsonString(), edited["projects"]!.ToJsonString());
        Assert.Equal(before["mcpServers"]!["other"]!.ToJsonString(), edited["mcpServers"]!["other"]!.ToJsonString());
        var entry = edited["mcpServers"]!["pkrelay"]!;
        Assert.Equal("stdio", entry["type"]!.GetValue<string>());
        Assert.Equal(Dotnet, entry["command"]!.GetValue<string>());
        Assert.Equal([Dll], entry["args"]!.AsArray().Select(x => x!.GetValue<string>()));
        // Backups are byte copies of what was there.
        Assert.Contains(Backups, x => File.ReadAllText(x) == original);
        Assert.Contains(Backups, x => File.ReadAllText(x) == json);
    }

    [Fact]
    public async Task Skip_client_config_touches_no_client_files()
    {
        await using var host = NewHub();
        Write(Toml, MalformedToml);
        var before = File.ReadAllBytes(Toml);

        Assert.Equal(0, await Run(host, skip: true));

        Assert.Equal(before, File.ReadAllBytes(Toml));
        Assert.False(File.Exists(ClaudeJson));
        Assert.Empty(Backups);
        Assert.Contains("PkRelayNode", runKey.Values.Keys);
        Assert.Single(started);
    }

    [Fact]
    public async Task Missing_files_are_created_without_backup_and_existing_pkrelay_entries_are_untouched()
    {
        await using var host = NewHub();
        Assert.Equal(0, await Run(host));
        Assert.Empty(Backups);
        Assert.Contains("[mcp_servers.pkrelay]", File.ReadAllText(Toml));
        Assert.Equal("stdio", JsonNode.Parse(File.ReadAllText(ClaudeJson))!["mcpServers"]!["pkrelay"]!["type"]!.GetValue<string>());

        var mine = "[ mcp_servers . \"pkrelay\" ]  # mine\ncommand = 'mine'\n";
        Write(Toml, mine);
        var json = "{\"mcpServers\":{\"pkrelay\":{\"command\":\"mine\"}}}";
        Write(ClaudeJson, json);
        Assert.Equal(0, await Run(host));
        Assert.Equal(mine, File.ReadAllText(Toml));
        Assert.Equal(json, File.ReadAllText(ClaudeJson));
        Assert.Empty(Backups);
    }

    [Fact]
    public async Task Logon_value_runs_the_node_with_the_chosen_data_folder_and_output_names_machine_and_hub_only()
    {
        await using var host = NewHub();
        Assert.Equal(0, await Run(host));

        Assert.Equal($"\"{Dotnet}\" \"{Dll}\" --node --data-dir \"{DataDir}\"", runKey.Values["PkRelayNode"]);
        Assert.Equal([$"{Dotnet}|{Dll}|{DataDir}"], started);
        var enrollment = NodeState.ReadEnrollment(DataDir)!;
        var machineId = JsonNode.Parse(File.ReadAllText(Path.Combine(DataDir, "node.json")))!["machineId"]!.GetValue<string>();
        var text = output.ToString() + error;
        Assert.Contains(machineId, output.ToString());
        Assert.Contains("http://127.0.0.1:5271/", output.ToString());
        Assert.DoesNotContain(enrollment.Credential, text);
    }

    [Fact]
    public async Task Failed_enrollment_changes_no_client_or_logon_settings()
    {
        await using var host = NewHub();
        Write(Toml, "model = \"x\"\n");
        var before = File.ReadAllBytes(Toml);
        var env = Env(host, TimeSpan.FromMilliseconds(300));
        env.Transport = new NodeTransport { CreateHandler = (_, _) => new ThrowingHandler(), FirstRetry = TimeSpan.FromMilliseconds(10), MaxRetry = TimeSpan.FromMilliseconds(20), CodexExecutable = () => null };

        var code = await SetupCommand.RunAsync(new SetupOptions(Hub, false, DataDir), env, CancellationToken.None);

        Assert.NotEqual(0, code);
        Assert.Equal(before, File.ReadAllBytes(Toml));
        Assert.False(File.Exists(ClaudeJson));
        Assert.Empty(Backups);
        Assert.Empty(runKey.Values);
        Assert.Empty(started);
    }

    [Theory]
    [InlineData("[mcp_servers.pkrelay]\n", true)]
    [InlineData("  [ mcp_servers.'pkrelay' ] # c\n", true)]
    [InlineData("[\"mcp_servers\".\"pkrelay\".env]\nA = 'b'\n", true)]
    [InlineData("[mcp_servers]\npkrelay = { command = 'x' }\n", true)]
    [InlineData("[mcp_servers]\npkrelay.command = 'x'\n", true)]
    [InlineData("mcp_servers.pkrelay.command = 'x'\n", true)]
    [InlineData("[mcp_servers.pkrelay2]\n[mcp_servers.other]\nnote = '''\n[mcp_servers.pkrelay]\n'''\n", false)]
    [InlineData("x = [\n '[mcp_servers.pkrelay]',\n]\n", false)]
    [InlineData("# [mcp_servers.pkrelay]\n", false)]
    [InlineData("", false)]
    public void Existing_codex_table_is_detected(string text, bool exists) => Assert.Equal(exists, ClientSettings.CodexHasPkRelay(text));

    [Theory]
    [InlineData("[mcp_servers.pkrelay")]
    [InlineData("[[mcp_servers.pkrelay]]\n")]
    [InlineData("mcp_servers = { pkrelay = { command = 'x' } }\n")]
    [InlineData("a = \"unterminated\n")]
    [InlineData("a = '''never closed\n")]
    [InlineData("[a]] \n")]
    [InlineData("just words\n")]
    [InlineData("a = [1, 2\n")]
    [InlineData("[\"mcp_servers\".\"pk\\u0072elay\"]\n")]
    public void Unconfident_codex_text_is_treated_as_malformed(string text) => Assert.Throws<ClientSettingsException>(() => ClientSettings.CodexHasPkRelay(text));

    private sealed class FakeRunKey : IRunKey
    {
        public Dictionary<string, string> Values { get; } = [];
        public string? Get(string name) => Values.GetValueOrDefault(name);
        public void Set(string name, string value) => Values[name] = value;
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new HttpRequestException("unreachable");
    }
}
