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

    private SetupEnvironment Env(RelayHostFixture host, TimeSpan? timeout = null, Action? duringEnrollment = null) => new()
    {
        Profile = Profile,
        DotnetPath = Dotnet,
        AdapterDll = Dll,
        RunKey = runKey,
        StartNode = (dotnet, dll, dataDir) => started.Add($"{dotnet}|{dll}|{dataDir}"),
        Transport = new NodeTransport
        {
            CreateHandler = (_, _) => new HookHandler(host.Server.CreateHandler(), duringEnrollment),
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
    public async Task Prompt_hook_is_installed_only_when_asked_keeping_other_hooks_and_only_once()
    {
        await using var host = NewHub();
        var claudeSettings = Path.Combine(Profile, ".claude", "settings.json");
        var codexHooks = Path.Combine(Profile, ".codex", "hooks.json");
        Assert.Equal(0, await Run(host));
        // Off by default: no hook file is created or changed.
        Assert.False(File.Exists(claudeSettings));
        Assert.False(File.Exists(codexHooks));

        Write(claudeSettings, "{\n  \"model\": \"opus\",\n  \"hooks\": { \"UserPromptSubmit\": [ { \"hooks\": [ { \"type\": \"command\", \"command\": \"echo mine\" } ] } ] }\n}\n");
        var options = SetupOptions.Parse(["--hub", Hub.AbsoluteUri, "--prompt-hook", "--data-dir", DataDir]);
        Assert.True(options.PromptHook);
        Assert.Equal(0, await SetupCommand.RunAsync(options, Env(host), CancellationToken.None));
        foreach (var path in new[] { claudeSettings, codexHooks })
        {
            var groups = JsonNode.Parse(File.ReadAllText(path))!["hooks"]!["UserPromptSubmit"]!.AsArray();
            var command = groups.Select(group => group!["hooks"]![0]!["command"]!.GetValue<string>()).Single(text => text.Contains("--prompt-hook"));
            Assert.Equal($"{LogonStart.Quote(Dotnet)} {LogonStart.Quote(Dll)} --prompt-hook --data-dir {LogonStart.Quote(SetupCommand.NormalizeFolder(DataDir))}", command);
            Assert.Equal(ClientSettings.PromptHookTimeoutSeconds, groups.Last()!["hooks"]![0]!["timeout"]!.GetValue<int>());
        }
        Assert.Contains("echo mine", File.ReadAllText(claudeSettings));
        Assert.Contains("\"model\": \"opus\"", File.ReadAllText(claudeSettings));
        var once = File.ReadAllBytes(claudeSettings);
        Assert.Equal(0, await SetupCommand.RunAsync(options, Env(host), CancellationToken.None));
        Assert.Equal(once, File.ReadAllBytes(claudeSettings));
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

    [Fact]
    public async Task A_client_file_changed_during_enrollment_keeps_the_change_and_gets_the_entry()
    {
        await using var host = NewHub();
        Write(ClaudeJson, "{\n  \"numStartups\": 1\n}\n");
        var env = Env(host, null, () => Write(ClaudeJson, "{\n  \"numStartups\": 2,\n  \"fresh\": true\n}\n"));

        Assert.Equal(0, await SetupCommand.RunAsync(new SetupOptions(Hub, false, DataDir), env, CancellationToken.None));

        var final = JsonNode.Parse(File.ReadAllText(ClaudeJson))!;
        Assert.Equal(2, final["numStartups"]!.GetValue<int>());
        Assert.True(final["fresh"]!.GetValue<bool>());
        Assert.Equal("stdio", final["mcpServers"]!["pkrelay"]!["type"]!.GetValue<string>());
        // The backup is of the file as it was just before the write, not of the early snapshot.
        Assert.Contains(Backups, x => File.ReadAllText(x).Contains("\"fresh\""));
    }

    [Fact]
    public async Task A_client_file_that_became_malformed_or_gained_the_entry_during_enrollment_is_not_overwritten()
    {
        await using var host = NewHub();
        Write(ClaudeJson, "{}");
        var env = Env(host, null, () => Write(ClaudeJson, MalformedJson));
        Assert.Equal(5, await SetupCommand.RunAsync(new SetupOptions(Hub, false, DataDir), env, CancellationToken.None));
        Assert.Equal(MalformedJson, File.ReadAllText(ClaudeJson));
        Assert.Empty(Backups);
        Assert.False(File.Exists(Toml));
        Assert.Empty(runKey.Values);
        Assert.Empty(started);

        var mine = "{\"mcpServers\":{\"pkrelay\":{\"command\":\"mine\"}}}";
        Write(ClaudeJson, "{}");
        Assert.Equal(0, await SetupCommand.RunAsync(new SetupOptions(Hub, false, DataDir), Env(host, null, () => Write(ClaudeJson, mine)), CancellationToken.None));
        Assert.Equal(mine, File.ReadAllText(ClaudeJson));
        Assert.Empty(Backups);
    }

    [Theory]
    [InlineData(@"C:\data\")]
    [InlineData(@"D:\")]
    [InlineData(@"C:\Program Files\My Node")]
    [InlineData(@"C:\Program Files\My Node\\")]
    [InlineData("C:\\odd \"quoted\" dir\\")]
    public void Command_lines_parse_back_to_exactly_the_intended_arguments(string folder) =>
        Assert.Equal([Dotnet, Dll, "--node", "--data-dir", folder], ParseCommandLine(LogonStart.Command(Dotnet, Dll, folder)));

    [Theory]
    [InlineData(@"C:\data\", "--data-dir \"C:\\data\\\\\"")]
    [InlineData(@"D:\", "--data-dir \"D:\\\\\"")]
    [InlineData(@"C:\with space\x", "--data-dir \"C:\\with space\\x\"")]
    public void Command_doubles_backslashes_before_the_closing_quote(string folder, string expectedTail) =>
        Assert.EndsWith(expectedTail, LogonStart.Command(Dotnet, Dll, folder));

    [Theory]
    [InlineData(@"C:\data\", @"C:\data")]
    [InlineData(@"C:\data\sub\\", @"C:\data\sub")]
    [InlineData(@"D:\", @"D:\")]
    [InlineData(@"C:\data", @"C:\data")]
    public void Data_folder_is_normalized_without_a_trailing_separator_except_for_a_drive_root(string typed, string expected) => Assert.Equal(expected, SetupCommand.NormalizeFolder(typed));

    [Fact]
    public async Task Data_folder_with_trailing_backslash_gives_a_working_logon_value_and_start()
    {
        await using var host = NewHub();
        var typed = DataDir + Path.DirectorySeparatorChar;
        Assert.Equal(0, await SetupCommand.RunAsync(new SetupOptions(Hub, true, typed), Env(host), CancellationToken.None));
        Assert.Equal([Dotnet, Dll, "--node", "--data-dir", DataDir], ParseCommandLine(runKey.Values["PkRelayNode"]));
        Assert.Equal([$"{Dotnet}|{Dll}|{DataDir}"], started);
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string commandLine, out int count);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    /// <summary>What a Windows program receives as argv for this command line (the real CommandLineToArgvW).</summary>
    private static string[] ParseCommandLine(string command)
    {
        var list = CommandLineToArgvW(command, out var count);
        try { return Enumerable.Range(0, count).Select(i => System.Runtime.InteropServices.Marshal.PtrToStringUni(System.Runtime.InteropServices.Marshal.ReadIntPtr(list, i * IntPtr.Size))!).ToArray(); }
        finally { LocalFree(list); }
    }

    private sealed class HookHandler(HttpMessageHandler inner, Action? onFirst) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Interlocked.Exchange(ref onFirst, null)?.Invoke(); return base.SendAsync(request, ct); }
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
