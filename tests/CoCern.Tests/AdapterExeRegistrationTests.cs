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

    private sealed class FakeRunKey : IRunKey
    {
        public Dictionary<string, string> Values { get; } = [];
        public string? Get(string name) => Values.GetValueOrDefault(name);
        public void Set(string name, string value) => Values[name] = value;
    }
}
