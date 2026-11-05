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
        EnrollTimeout = timeout ?? TimeSpan.FromSeconds(60),
        Out = output,
        Error = error
    };

    private Task<int> Run(RelayHostFixture host, bool skip = false, TimeSpan? timeout = null) =>
        SetupCommand.RunAsync(new SetupOptions(Hub, skip, DataDir), Env(host, timeout), CancellationToken.None);

    [Theory]
    [InlineData("foreign")]
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

    private static void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text, new UTF8Encoding(false)); }

    [Theory]
    [InlineData(true)]
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

    [Theory]
    [InlineData(@"C:\data\")]
    [InlineData("C:\\odd \"quoted\" dir\\")]
    public void Command_lines_parse_back_to_exactly_the_intended_arguments(string folder) =>
        Assert.Equal([Dotnet, Dll, "--node", "--data-dir", folder], ParseCommandLine(LogonStart.Command(Dotnet, Dll, folder)));

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

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new HttpRequestException("unreachable");
    }
}
