using System.Text;
using System.Text.Json.Nodes;
using CoCern.Adapter;
using CoCern.Adapter.Node;
using CoCern.Adapter.Setup;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CoCern.Tests;

public sealed class SetupTests : IDisposable
{
    private static readonly Uri Hub = new("http://127.0.0.1:5271");
    private const string Dotnet = @"C:\Program Files\dotnet\dotnet.exe";
    private const string Dll = @"C:\Apps\CoCern Node\CoCern.Adapter.dll";
    private const string MalformedToml = "[mcp_servers.other\ncommand = 'x'\n";
    private const string MalformedJson = "{ \"mcpServers\": { ";
    private readonly string root = Path.Combine(Path.GetTempPath(), "cocern-setup-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRunKey runKey = new();
    private readonly List<string> started = [];
    private readonly StringWriter output = new();
    private readonly StringWriter error = new();

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private string Profile => Path.Combine(root, "profile");
    private string DataDir => Path.Combine(root, "node data");
    private string Toml => Path.Combine(Profile, ".codex", "config.toml");
    private string ClaudeJson => Path.Combine(Profile, ".claude.json");
    private string[] Backups => Directory.Exists(Profile) ? Directory.GetFiles(Profile, "*.cocern-backup-*", SearchOption.AllDirectories) : [];

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
            Clients = () => [], Locators = [],
            JoinCode = RelayHostFixture.JoinCode,
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

    [Fact]
    public async Task Normal_node_start_selects_supervisor_after_enrollment_releases_the_original_singleton()
    {
        await using var host = NewHub();
        var installedDll = BootstrapPackage();
        var transport = Env(host).Transport;
        transport.SingleCycle = true; // The pre-updater implementation must return, so RED is an assertion, never an unbounded node loop.
        var selected = 0;
        var result = await SetupEnvironment.RunNodeAsync(DataDir, Hub, transport, installedDll, Dotnet, error, CancellationToken.None,
            (folder, dll, dotnet, _) =>
            {
                Assert.Equal(Path.GetFullPath(DataDir), folder);
                Assert.Equal(installedDll, dll);
                Assert.Equal(Dotnet, dotnet);
                using var unlocked = NodeState.OpenExisting(folder);
                Assert.True(unlocked.Enrolled);
                Assert.NotNull(unlocked.ReadCredential());
                selected++;
                return Task.FromResult(0);
            });
        Assert.Equal(0, result);
        Assert.Equal(1, selected);
        Assert.Null(transport.CreateUpdater);
    }

    [Fact]
    public async Task Valid_incompatible_installed_bootstrap_keeps_direct_node_working_and_reports_bootstrap_needed()
    {
        await using var host = NewHub();
        var dll = BootstrapPackage(protocol: 0);
        var before = File.ReadAllBytes(dll);
        var transport = Env(host).Transport;
        transport.SingleCycle = true;
        Assert.Equal(0, await SetupEnvironment.RunNodeAsync(DataDir, Hub, transport, dll, Dotnet, error, CancellationToken.None,
            (_, _, _, _) => throw new InvalidOperationException("An incompatible bootstrap must never start a supervisor.")));
        Assert.Contains("node-update:bootstrap-needed", error.ToString());
        Assert.Null(transport.CreateUpdater);
        Assert.Equal(before, File.ReadAllBytes(dll));
        Assert.False(File.Exists(Path.Combine(DataDir, "updates", "state.json")));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("foreign")]
    [InlineData("inside-updates")]
    [InlineData("unsupported-corrupt")]
    public async Task Invalid_known_bootstrap_is_refused_without_enrollment_or_direct_fallback(string defect)
    {
        await using var host = NewHub();
        var dll = BootstrapPackage(protocol: defect == "unsupported-corrupt" ? 0 : 1);
        if (defect == "missing") File.Delete(Path.ChangeExtension(dll, ".runtimeconfig.json"));
        if (defect == "unsupported-corrupt") File.WriteAllText(Path.ChangeExtension(dll, ".runtimeconfig.json"), "damaged");
        if (defect == "corrupt") File.WriteAllText(dll, "damaged");
        if (defect == "foreign") { var foreign = Path.Combine(Path.GetDirectoryName(dll)!, "Foreign.dll"); File.Copy(dll, foreign); dll = foreign; }
        if (defect == "inside-updates")
        {
            var nested = Path.Combine(DataDir, "updates", "versions", new string('A', 64));
            Directory.CreateDirectory(nested);
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(dll)!)) File.Copy(file, Path.Combine(nested, Path.GetFileName(file)));
            dll = Path.Combine(nested, "CoCern.Adapter.dll");
        }
        var transport = Env(host).Transport;
        transport.SingleCycle = true;
        Assert.Equal(1, await SetupEnvironment.RunNodeAsync(DataDir, Hub, transport, dll, Dotnet, error, CancellationToken.None,
            (_, _, _, _) => throw new InvalidOperationException("Invalid bootstrap must never be selected.")));
        Assert.False(File.Exists(Path.Combine(DataDir, "credential")));
        Assert.DoesNotContain("bootstrap-needed", error.ToString());
    }

    [Fact]
    public void Installed_apphost_and_framework_dependent_startup_keep_the_known_source_and_original_folder()
    {
        var installed = Path.Combine(root, "installed package");
        var dll = Path.Combine(installed, "CoCern.Adapter.dll");
        var exe = Path.Combine(installed, "CoCern.Adapter.exe");
        var fdd = SetupEnvironment.Host(Dotnet, null, installed, path => path == dll);
        Assert.Equal((Dotnet, dll), fdd);
        var selfContained = SetupEnvironment.Host(exe, null, installed, path => path == dll || path == exe);
        Assert.Equal(("dotnet", dll), selfContained);
        var apphost = SetupEnvironment.AdapterLaunch(selfContained.Dotnet, dll, path => path == exe);
        Assert.Equal(exe, apphost.Command);
        Assert.Empty(apphost.Arguments);
        Assert.Equal(new[] { dll }, SetupEnvironment.AdapterLaunch(fdd.Dotnet, dll, path => path == dll).Arguments);
    }

    private string BootstrapPackage(int protocol = 1)
    {
        var folder = Path.Combine(root, "installed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var code = "using System.Reflection; [assembly: AssemblyInformationalVersion(\"3.0.0+installed\")] [assembly: AssemblyMetadata(\"CoCernNodeSupervisorProtocol\", \"" + protocol + "\")] public static class InertPackage { }";
        var compilation = CSharpCompilation.Create("CoCern.Adapter", [CSharpSyntaxTree.ParseText(code)], [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)], new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var dll = Path.Combine(folder, "CoCern.Adapter.dll");
        var result = compilation.Emit(dll);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        File.WriteAllText(Path.Combine(folder, "CoCern.Adapter.deps.json"), "{\"runtimeTarget\":{\"name\":\".NETCoreApp,Version=v10.0\"}}");
        File.WriteAllText(Path.Combine(folder, "CoCern.Adapter.runtimeconfig.json"), "{\"runtimeOptions\":{\"tfm\":\"net10.0\",\"framework\":{\"name\":\"Microsoft.NETCore.App\",\"version\":\"10.0.0\"}}}");
        return dll;
    }

    [Fact]
    public async Task Repeated_setup_keeps_credentials_hooks_and_unrelated_startup_values()
    {
        await using var host = NewHub();
        runKey.Values["OtherProgram"] = "unrelated.exe";
        Write(Path.Combine(Profile, ".claude", "settings.json"), "{\"hooks\":{\"SessionStart\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"other-hook\"}]}]}}");
        Assert.Equal(0, await Run(host));
        var identity = File.ReadAllBytes(Path.Combine(DataDir, "node.json"));
        var credential = File.ReadAllBytes(Path.Combine(DataDir, "credential"));
        var hooks = File.ReadAllBytes(Path.Combine(Profile, ".claude", "settings.json"));
        var owned = runKey.Values["CoCernNode"];
        Assert.Equal(0, await Run(host));
        Assert.Equal(identity, File.ReadAllBytes(Path.Combine(DataDir, "node.json")));
        Assert.Equal(credential, File.ReadAllBytes(Path.Combine(DataDir, "credential")));
        Assert.Equal(hooks, File.ReadAllBytes(Path.Combine(Profile, ".claude", "settings.json")));
        Assert.Equal(owned, runKey.Values["CoCernNode"]);
        Assert.Equal("unrelated.exe", runKey.Values["OtherProgram"]);
        Assert.Contains("other-hook", File.ReadAllText(Path.Combine(Profile, ".claude", "settings.json")));
    }

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
        var (toml, json, logon) = (File.ReadAllBytes(Toml), File.ReadAllBytes(ClaudeJson), runKey.Values["CoCernNode"]);

        Assert.Equal(0, await Run(host));

        Assert.Equal(backups.Order(), Backups.Order());
        Assert.Equal(toml, File.ReadAllBytes(Toml));
        Assert.Equal(json, File.ReadAllBytes(ClaudeJson));
        Assert.Equal(logon, runKey.Values["CoCernNode"]);
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
        Assert.Contains("[mcp_servers.cocern]\r\ncommand = 'C:\\Program Files\\dotnet\\dotnet.exe'\r\nargs = ['C:\\Apps\\CoCern Node\\CoCern.Adapter.dll']\r\n", toml);
        Assert.DoesNotContain("\n", toml.Replace("\r\n", ""));
        var edited = JsonNode.Parse(File.ReadAllText(ClaudeJson))!;
        var before = JsonNode.Parse(json)!;
        Assert.Equal(12, edited["numStartups"]!.GetValue<int>());
        Assert.Contains("12345678901234567890", File.ReadAllText(ClaudeJson));
        Assert.Equal(before["projects"]!.ToJsonString(), edited["projects"]!.ToJsonString());
        Assert.Equal(before["mcpServers"]!["other"]!.ToJsonString(), edited["mcpServers"]!["other"]!.ToJsonString());
        var entry = edited["mcpServers"]!["cocern"]!;
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
        // The prompt hook is off by default; only Claude Code's Stop hook, which wakes an idle chat, is installed.
        Assert.DoesNotContain("--prompt-hook", File.ReadAllText(claudeSettings));
        Assert.Contains("--stop-hook", File.ReadAllText(claudeSettings));
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
        Assert.Contains("CoCernNode", runKey.Values.Keys);
        Assert.Single(started);
    }

    [Fact]
    public async Task Missing_files_are_created_without_backup_and_existing_cocern_entries_are_untouched()
    {
        await using var host = NewHub();
        Assert.Equal(0, await Run(host));
        Assert.Empty(Backups);
        Assert.Contains("[mcp_servers.cocern]", File.ReadAllText(Toml));
        Assert.Equal("stdio", JsonNode.Parse(File.ReadAllText(ClaudeJson))!["mcpServers"]!["cocern"]!["type"]!.GetValue<string>());

        var mine = "[ mcp_servers . \"cocern\" ]  # mine\ncommand = 'mine'\n";
        Write(Toml, mine);
        var json = "{\"mcpServers\":{\"cocern\":{\"command\":\"mine\"}}}";
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

        Assert.Equal($"\"{Dotnet}\" \"{Dll}\" --node --data-dir \"{DataDir}\"", runKey.Values["CoCernNode"]);
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
        env.Transport = new NodeTransport { Clients = () => [], Locators = [], CreateHandler = (_, _) => new ThrowingHandler(), FirstRetry = TimeSpan.FromMilliseconds(10), MaxRetry = TimeSpan.FromMilliseconds(20), CodexExecutable = () => null };

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
        Assert.Equal("stdio", final["mcpServers"]!["cocern"]!["type"]!.GetValue<string>());
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

        var mine = "{\"mcpServers\":{\"cocern\":{\"command\":\"mine\"}}}";
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
        Assert.Equal([Dotnet, Dll, "--node", "--data-dir", DataDir], ParseCommandLine(runKey.Values["CoCernNode"]));
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
    [InlineData("[mcp_servers.cocern]\n", true)]
    [InlineData("  [ mcp_servers.'cocern' ] # c\n", true)]
    [InlineData("[\"mcp_servers\".\"cocern\".env]\nA = 'b'\n", true)]
    [InlineData("[mcp_servers]\ncocern = { command = 'x' }\n", true)]
    [InlineData("[mcp_servers]\ncocern.command = 'x'\n", true)]
    [InlineData("mcp_servers.cocern.command = 'x'\n", true)]
    [InlineData("[mcp_servers.cocern2]\n[mcp_servers.other]\nnote = '''\n[mcp_servers.cocern]\n'''\n", false)]
    [InlineData("x = [\n '[mcp_servers.cocern]',\n]\n", false)]
    [InlineData("# [mcp_servers.cocern]\n", false)]
    [InlineData("", false)]
    public void Existing_codex_table_is_detected(string text, bool exists) => Assert.Equal(exists, ClientSettings.CodexHasCoCern(text));

    [Theory]
    [InlineData("[mcp_servers.cocern")]
    [InlineData("[[mcp_servers.cocern]]\n")]
    [InlineData("mcp_servers = { cocern = { command = 'x' } }\n")]
    [InlineData("a = \"unterminated\n")]
    [InlineData("a = '''never closed\n")]
    [InlineData("[a]] \n")]
    [InlineData("just words\n")]
    [InlineData("a = [1, 2\n")]
    [InlineData("[\"mcp_servers\".\"pk\\u0072elay\"]\n")]
    public void Unconfident_codex_text_is_treated_as_malformed(string text) => Assert.Throws<ClientSettingsException>(() => ClientSettings.CodexHasCoCern(text));

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
