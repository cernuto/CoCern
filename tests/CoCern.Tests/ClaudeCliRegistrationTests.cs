using System.Text;
using System.Text.Json.Nodes;
using CoCern.Adapter;
using CoCern.Adapter.Node;
using CoCern.Adapter.Setup;
using Xunit;

namespace CoCern.Tests;

/// <summary>Setup registers CoCern with Claude Code through its own <c>claude mcp</c> command line when one is found, and edits .claude.json only as the fallback.</summary>
public sealed class ClaudeCliRegistrationTests : IDisposable
{
    private static readonly Uri Hub = new("http://127.0.0.1:5271");
    private const string Dotnet = @"C:\Program Files\dotnet\dotnet.exe";
    private const string Dll = @"C:\Apps\CoCern Node\CoCern.Adapter.dll";
    private readonly string root = Path.Combine(Path.GetTempPath(), "cocern-claude-cli-" + Guid.NewGuid().ToString("N"));
    private readonly List<(string Executable, IReadOnlyList<string> Arguments)> calls = [];
    private readonly StringWriter output = new();
    private readonly StringWriter error = new();

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private string Profile => Path.Combine(root, "profile");
    private string DataDir => Path.Combine(root, "node data");
    private string ClaudeJson => Path.Combine(Profile, ".claude.json");
    private string Launchers => Path.Combine(root, "launchers");

    private static RelayHostFixture NewHub() => new(remoteAddress: System.Net.IPAddress.Loopback, settings: new Dictionary<string, string?> { ["Relay:Discovery:Enabled"] = "true", ["Relay:Discovery:Repositories:0"] = "repo" });

    /// <summary>A fake <c>claude</c>: <paramref name="get"/> and <paramref name="add"/> are the exit codes of <c>mcp get</c> and <c>mcp add</c>; null is a timeout.</summary>
    private ClaudeCli Fake(int? get, int? add) => new(@"C:\fake\claude.cmd", (executable, arguments) =>
    {
        calls.Add((executable, arguments));
        var code = arguments[1] == "get" ? get : add;
        return code is null ? null : new CliResult(code.Value, "", "");
    });

    private SetupEnvironment Env(RelayHostFixture host, ClaudeCli? cli, bool running = false) => new()
    {
        Profile = Profile,
        DotnetPath = Dotnet,
        AdapterDll = Dll,
        RunKey = new FakeRunKey(),
        StartNode = (_, _, _) => { },
        ClaudeCli = cli,
        ClaudeCodeRunning = () => running,
        Transport = new NodeTransport
        {
            JoinCode = RelayHostFixture.JoinCode,
            CreateHandler = (_, _) => host.Server.CreateHandler(),
            HubHandler = () => host.Server.CreateHandler(),
            MachineName = "Setup PC",
            Chats = _ => Task.FromResult(new JsonArray()),
            FirstRetry = TimeSpan.FromMilliseconds(10),
            MaxRetry = TimeSpan.FromMilliseconds(20),
            CodexExecutable = () => null
        },
        EnrollTimeout = TimeSpan.FromSeconds(20),
        Out = output,
        Error = error
    };

    private Task<int> Run(RelayHostFixture host, ClaudeCli? cli, bool running = false) =>
        SetupCommand.RunAsync(new SetupOptions(Hub, false, DataDir), Env(host, cli, running), CancellationToken.None);

    private string Launcher(int exitCode)
    {
        Directory.CreateDirectory(Launchers);
        var path = Path.Combine(Launchers, "claude.cmd");
        File.WriteAllText(path, $"@echo off\r\necho %*\r\nexit /b {exitCode}\r\n", new UTF8Encoding(false));
        return path;
    }

    private static string? Variables(string path, string name) => name switch
    {
        "PATH" => path,
        "PATHEXT" => ".COM;.EXE;.BAT;.CMD",
        _ => null
    };

    [Fact]
    public void Resolver_finds_a_claude_launcher_on_the_path()
    {
        var launcher = Launcher(0);
        Directory.CreateDirectory(Path.Combine(root, "empty"));

        Assert.Equal(launcher, ClaudeCli.Locate(name => Variables(Path.Combine(root, "empty") + Path.PathSeparator + Launchers, name)));
        Assert.Null(ClaudeCli.Locate(name => Variables(Path.Combine(root, "empty"), name)));
    }

    [Fact]
    public void Resolver_falls_back_to_the_usual_windows_locations()
    {
        var appData = Path.Combine(root, "appdata");
        var launcher = Path.Combine(appData, "npm", "claude.cmd");
        Directory.CreateDirectory(Path.GetDirectoryName(launcher)!);
        File.WriteAllText(launcher, "@exit /b 0\r\n");

        Assert.Equal(launcher, ClaudeCli.Locate(name => name == "APPDATA" ? appData : null));
        Assert.Null(ClaudeCli.Locate(_ => null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Launcher_runs_with_the_arguments_and_reports_its_exit_code(int exitCode)
    {
        var launcher = Launcher(exitCode);

        var result = ClaudeCli.Run(launcher, ["mcp", "get", "cocern"], TimeSpan.FromSeconds(30));

        Assert.NotNull(result);
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Contains("mcp get cocern", result.Output);
    }

    [Fact]
    public void A_launcher_that_cannot_start_is_unavailable() =>
        Assert.Null(ClaudeCli.Run(Path.Combine(root, "missing", "claude.exe"), ["mcp", "get", "cocern"], TimeSpan.FromSeconds(30)));

    [Fact]
    public void Add_arguments_carry_the_same_command_args_and_env_as_the_file_plan()
    {
        var environment = new Dictionary<string, string> { ["COCERN_URL"] = "https://hub.example:5001/", ["COCERN_FINGERPRINT"] = new string('A', 64) };
        var plan = ClientSettings.PlanClaudeCode(ClaudeJson, Dotnet, Dll, environment);
        plan.Apply(TimeProvider.System);
        var entry = JsonNode.Parse(File.ReadAllText(ClaudeJson))!["mcpServers"]!["cocern"]!;

        var arguments = ClaudeCli.AddArguments(Dotnet, Dll, environment);

        var separator = arguments.ToList().IndexOf("--");
        Assert.Equal(["mcp", "add", "--scope", "user", "--transport", entry["type"]!.GetValue<string>(), "cocern"], arguments.Take(7));
        Assert.Equal([entry["command"]!.GetValue<string>(), .. entry["args"]!.AsArray().Select(x => x!.GetValue<string>())], arguments.Skip(separator + 1));
        var env = Enumerable.Range(7, separator - 7).Where(i => arguments[i] == "--env").Select(i => arguments[i + 1]).Order();
        Assert.Equal(entry["env"]!.AsObject().Select(pair => $"{pair.Key}={pair.Value!.GetValue<string>()}").Order(), env);
        Assert.Equal(["mcp", "add", "--scope", "user", "--transport", "stdio", "cocern", "--", Dotnet, Dll], ClaudeCli.AddArguments(Dotnet, Dll, null));
    }

    [Fact]
    public async Task Cli_registers_the_entry_and_the_file_planner_is_not_used()
    {
        await using var host = NewHub();

        Assert.Equal(0, await Run(host, Fake(get: 1, add: 0)));

        Assert.False(File.Exists(ClaudeJson));
        Assert.Equal(2, calls.Count);
        Assert.Equal(["mcp", "get", "cocern"], calls[0].Arguments);
        Assert.Equal(ClaudeCli.AddArguments(Dotnet, Dll, null), calls[1].Arguments);
        Assert.Contains("claude mcp add", output.ToString());
        Assert.DoesNotContain(RelayHostFixture.JoinCode, output.ToString() + error);
    }

    [Fact]
    public async Task An_entry_reported_by_mcp_get_is_left_alone()
    {
        await using var host = NewHub();

        Assert.Equal(0, await Run(host, Fake(get: 0, add: 0)));

        Assert.False(File.Exists(ClaudeJson));
        Assert.Single(calls);
        Assert.Contains("already has a CoCern entry", output.ToString());
    }

    [Fact]
    public async Task File_planner_is_used_when_no_launcher_is_found()
    {
        await using var host = NewHub();

        Assert.Equal(0, await Run(host, null));

        Assert.Empty(calls);
        Assert.Equal("stdio", JsonNode.Parse(File.ReadAllText(ClaudeJson))!["mcpServers"]!["cocern"]!["type"]!.GetValue<string>());
        Assert.Contains(".claude.json", output.ToString());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(null)]
    public async Task File_planner_is_used_when_the_launcher_fails_or_times_out(int? addExitCode)
    {
        await using var host = NewHub();

        Assert.Equal(0, await Run(host, Fake(get: 1, add: addExitCode)));

        Assert.Equal(2, calls.Count);
        Assert.Equal(Dll, JsonNode.Parse(File.ReadAllText(ClaudeJson))!["mcpServers"]!["cocern"]!["args"]![0]!.GetValue<string>());
        Assert.Contains("claude mcp add", output.ToString());
        Assert.Contains(".claude.json", output.ToString());
    }

    [Fact]
    public async Task A_running_claude_code_gets_a_restart_warning_only_when_the_file_was_edited()
    {
        await using var host = NewHub();

        Assert.Equal(0, await Run(host, null, running: true));
        Assert.Contains("restart", output.ToString() + error, StringComparison.OrdinalIgnoreCase);

        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        File.Delete(ClaudeJson);
        Assert.Equal(0, await Run(host, Fake(get: 1, add: 0), running: true));
        Assert.DoesNotContain("restart", output.ToString() + error, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeRunKey : IRunKey
    {
        public Dictionary<string, string> Values { get; } = [];
        public string? Get(string name) => Values.GetValueOrDefault(name);
        public void Set(string name, string value) => Values[name] = value;
    }
}
