using System.Text.Json.Nodes;
using CoCern.Adapter.Setup;
using Xunit;

namespace CoCern.Tests;

/// <summary>
/// A joined PC that got the self-contained zip may have no dotnet at all: when <c>CoCern.Adapter.exe</c> is beside the dll, every place that starts the
/// adapter (the Claude Code and Codex entries, <c>claude mcp add</c>, the Run key) names the exe alone; without it, they keep <c>dotnet &lt;dll&gt;</c>.
/// </summary>
public sealed class AdapterExeRegistrationTests : IDisposable
{
    private const string Dotnet = @"C:\Program Files\dotnet\dotnet.exe";
    private readonly string root = Path.Combine(Path.GetTempPath(), "cocern-adapter-exe-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private string Profile => Path.Combine(root, "profile");
    private string ClaudeJson => Path.Combine(Profile, ".claude.json");
    private string Toml => Path.Combine(Profile, ".codex", "config.toml");

    /// <summary>A published adapter folder with the dll, and the exe when <paramref name="exe"/>; the files are empty, only their presence matters.</summary>
    private (string Dll, string Exe) Adapter(bool exe)
    {
        var folder = Path.Combine(root, "adapter");
        Directory.CreateDirectory(folder);
        var dll = Path.Combine(folder, "CoCern.Adapter.dll");
        var exePath = Path.Combine(folder, "CoCern.Adapter.exe");
        File.WriteAllBytes(dll, []);
        if (exe) File.WriteAllBytes(exePath, []);
        return (dll, exePath);
    }

    private static (string Command, string[] Args) ClaudeEntry(string path)
    {
        var entry = JsonNode.Parse(File.ReadAllText(path))!["mcpServers"]!["cocern"]!;
        return (entry["command"]!.GetValue<string>(), entry["args"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Claude_code_entry_names_the_exe_alone_when_it_is_beside_the_dll(bool exe)
    {
        var (dll, exePath) = Adapter(exe);

        ClientSettings.PlanClaudeCode(ClaudeJson, Dotnet, dll).Apply(TimeProvider.System);

        var (command, args) = ClaudeEntry(ClaudeJson);
        Assert.Equal(exe ? exePath : Dotnet, command);
        Assert.Equal(exe ? [] : [dll], args);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Codex_entry_names_the_exe_alone_when_it_is_beside_the_dll(bool exe)
    {
        var (dll, exePath) = Adapter(exe);

        ClientSettings.PlanCodex(Toml, Dotnet, dll).Apply(TimeProvider.System);

        var toml = File.ReadAllText(Toml);
        Assert.Contains(exe ? $"command = '{exePath}'{Environment.NewLine}args = []{Environment.NewLine}" : $"command = '{Dotnet}'{Environment.NewLine}args = ['{dll}']{Environment.NewLine}", toml);
        if (exe) Assert.DoesNotContain("dotnet", toml, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Claude_cli_add_arguments_name_the_exe_alone_when_it_is_beside_the_dll(bool exe)
    {
        var (dll, exePath) = Adapter(exe);

        string[] tail = exe ? [exePath] : [Dotnet, dll];
        Assert.Equal(["mcp", "add", "--scope", "user", "--transport", "stdio", "cocern", "--", .. tail], ClaudeCli.AddArguments(Dotnet, dll, null));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Run_key_command_names_the_exe_alone_when_it_is_beside_the_dll(bool exe)
    {
        var (dll, exePath) = Adapter(exe);
        var key = new FakeRunKey();

        LogonStart.Register(Dotnet, dll, @"C:\data", key);

        var expected = exe ? $"{LogonStart.Quote(exePath)} --node --data-dir {LogonStart.Quote(@"C:\data")}" : $"{LogonStart.Quote(Dotnet)} {LogonStart.Quote(dll)} --node --data-dir {LogonStart.Quote(@"C:\data")}";
        Assert.Equal(expected, key.Values[LogonStart.ValueName]);
    }

    /// <summary>
    /// The one-command join starts setup as the downloaded CoCern.Adapter.exe, often on a PC with no dotnet: setup must then accept the exe beside
    /// the dll as the way to start CoCern, and must refuse to guess a host when neither a dotnet host nor that exe is there.
    /// </summary>
    [Fact]
    public void Setup_started_as_the_exe_beside_the_dll_needs_no_dotnet()
    {
        var folder = Path.Combine(root, "adapter");
        var (dll, exe) = (Path.Combine(folder, "CoCern.Adapter.dll"), Path.Combine(folder, "CoCern.Adapter.exe"));
        var present = new HashSet<string>(StringComparer.Ordinal) { dll, exe };

        Assert.Equal(("dotnet", dll), SetupEnvironment.Host(exe, null, folder, present.Contains));
        // Whatever entry setup then writes names the exe alone, so the placeholder host is never started.
        Assert.Equal((exe, Array.Empty<string>()), (SetupEnvironment.AdapterLaunch("dotnet", dll, present.Contains).Command, SetupEnvironment.AdapterLaunch("dotnet", dll, present.Contains).Arguments.ToArray()));
    }

    [Fact]
    public void Setup_started_as_an_exe_without_the_exe_beside_the_dll_is_refused()
    {
        var folder = Path.Combine(root, "adapter");
        var dll = Path.Combine(folder, "CoCern.Adapter.dll");
        var present = new HashSet<string>(StringComparer.Ordinal) { dll };

        var refusal = Assert.Throws<CoCern.Adapter.AdapterConfigurationException>(() => SetupEnvironment.Host(Path.Combine(root, "elsewhere", "CoCern.Adapter.exe"), null, folder, present.Contains));
        Assert.Contains("CoCern.Adapter.exe", refusal.Message);
        // A DOTNET_HOST_PATH that names no file does not help either.
        Assert.Throws<CoCern.Adapter.AdapterConfigurationException>(() => SetupEnvironment.Host(Path.Combine(root, "CoCern.Adapter.exe"), Path.Combine(root, "missing", "dotnet"), folder, present.Contains));
        // Nor does anything run without the adapter dll itself.
        Assert.Contains("was not found", Assert.Throws<CoCern.Adapter.AdapterConfigurationException>(() => SetupEnvironment.Host(Path.Combine(root, "dotnet"), null, Path.Combine(root, "empty"), present.Contains)).Message);
    }

    [Theory]
    [InlineData("dotnet")]
    [InlineData("dotnet.exe")]
    [InlineData("DOTNET.EXE")]
    public void Setup_started_by_dotnet_uses_that_host(string hostName)
    {
        var folder = Path.Combine(root, "adapter");
        var dll = Path.Combine(folder, "CoCern.Adapter.dll");
        var host = Path.Combine(root, "sdk", hostName);
        var present = new HashSet<string>(StringComparer.Ordinal) { dll };

        Assert.Equal((host, dll), SetupEnvironment.Host(host, Path.Combine(root, "other", "dotnet"), folder, present.Contains));
        // Started some other way, an existing DOTNET_HOST_PATH is the host.
        var named = Path.Combine(root, "named", "dotnet");
        present.Add(named);
        Assert.Equal((named, dll), SetupEnvironment.Host(Path.Combine(root, "tool"), named, folder, present.Contains));
    }

    private sealed class FakeRunKey : IRunKey
    {
        public Dictionary<string, string> Values { get; } = [];
        public string? Get(string name) => Values.GetValueOrDefault(name);
        public void Set(string name, string value) => Values[name] = value;
    }
}
