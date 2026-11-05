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

    private static RelayHostFixture NewHub() => new(remoteAddress: System.Net.IPAddress.Loopback, settings: new Dictionary<string, string?> { ["Relay:Discovery:Enabled"] = "true", ["Relay:Discovery:Repositories:0"] = "repo" });

    /// <summary>What <c>claude mcp get</c> prints for an entry in the user's own settings.</summary>
    private const string UserScoped = "cocern:\n  Scope: User config (available in all your projects)\n  Status: ✓ Connected\n  Type: stdio\n\nTo remove this server, run: claude mcp remove \"cocern\" -s user\n";

    /// <summary>
    /// A fake <c>claude</c>: <paramref name="get"/> and <paramref name="add"/> are the exit codes of <c>mcp get</c> and <c>mcp add</c>; null is a timeout.
    /// A found entry prints <paramref name="getOutput"/>, a user entry unless told otherwise.
    /// </summary>
    private ClaudeCli Fake(int? get, int? add, string getOutput = UserScoped, string executable = @"C:\fake\claude.cmd") => new(executable, (exe, arguments) =>
    {
        calls.Add((exe, arguments));
        var code = arguments[1] == "get" ? get : add;
        return code is null ? null : new CliResult(code.Value, arguments[1] == "get" && code == 0 ? getOutput : "", "");
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
        EnrollTimeout = TimeSpan.FromSeconds(60),
        Out = output,
        Error = error
    };

    private Task<int> Run(RelayHostFixture host, ClaudeCli? cli, bool running = false) =>
        SetupCommand.RunAsync(new SetupOptions(Hub, false, DataDir), Env(host, cli, running), CancellationToken.None);

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

    // ---- A batch-file claude never gets arguments cmd.exe would misread ----

    [Theory]
    [InlineData(@"C:\Program Files (x86)\CoCern\CoCern.Adapter.dll", true)]
    [InlineData(@"C:\Tools & More\CoCern.Adapter.dll", false)]
    public void Arguments_for_a_batch_file_are_checked_for_cmd_metacharacters(string adapterDll, bool safe)
    {
        Assert.Equal(safe, ClaudeCli.SafeForBatch(ClaudeCli.AddArguments(Dotnet, adapterDll, null)));
        Assert.Equal(safe, ClaudeCli.SafeForBatch(ClaudeCli.AddArguments(Dotnet, @"C:\Apps\CoCern.Adapter.dll", new Dictionary<string, string> { ["COCERN_TOKEN_FILE"] = adapterDll })));
    }
}
