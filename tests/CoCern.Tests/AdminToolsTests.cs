using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Adapter;
using CoCern.Adapter.Setup;
using CoCern.Contracts;
using CoCern.Contracts.Dashboard;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Host;
using CoCern.Host.Dashboard;
using Xunit;

namespace CoCern.Tests;

/// <summary><c>hub_diagnose</c>, <c>add_pc</c> and <c>connect_app</c> away from a host: their catalog entries, and the parts of their replies that need none.</summary>
public sealed class AdminToolsTests : IDisposable
{
    private static readonly string[] Names = [RelayTools.HubDiagnose, RelayTools.AddPc, RelayTools.ConnectApp];
    private readonly string root = Path.Combine(Path.GetTempPath(), "cocern-admin-tools-" + Guid.NewGuid().ToString("N"));

    public AdminToolsTests() => Directory.CreateDirectory(root);

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    [Fact]
    public void The_three_tools_are_administrator_only_and_workers_never_list_them()
    {
        foreach (var name in Names)
        {
            Assert.True(RelayToolCatalog.TryGet(name, out var tool), name);
            Assert.True(tool.AdministratorOnly, name);
            Assert.False(tool.RequiresSession, name);
            Assert.DoesNotContain(name, RelayToolCatalog.ForRole(false).Select(worker => worker.Name));
            Assert.DoesNotContain(name, ToolResults.Tools(false).Select(worker => worker.Name));
            Assert.DoesNotContain(name, CoordinationTools.NamesForRole(false));
            Assert.Contains(name, CoordinationTools.NamesForRole(true));
        }
    }

    [Fact]
    public void Add_pc_is_not_a_mutation_so_no_reply_holding_the_join_code_is_kept_for_replay()
    {
        Assert.True(RelayToolCatalog.TryGet(RelayTools.AddPc, out var addPc));
        Assert.False(addPc.IsMutation);
        Assert.True(RelayToolCatalog.TryGet(RelayTools.HubDiagnose, out var diagnose));
        Assert.False(diagnose.IsMutation);
        Assert.DoesNotContain("requestId", RelayToolCatalog.ForwardingSchema(addPc).GetProperty("properties").EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public void Connecting_with_no_admin_choice_keeps_an_apps_other_settings_and_grants_no_administrator_tools()
    {
        var profile = Path.Combine(root, "profile");
        var token = Path.Combine(root, "data", "local-client-token");
        Directory.CreateDirectory(profile);
        Directory.CreateDirectory(Path.GetDirectoryName(token)!);
        File.WriteAllText(token, "isolated-test-token");
        var settings = Path.Combine(profile, ".claude.json");
        File.WriteAllText(settings, new JsonObject { ["numStartups"] = 4, ["mcpServers"] = new JsonObject { ["other"] = new JsonObject { ["command"] = "x" } } }.ToJsonString());
        using var output = new StringWriter();
        using var error = new StringWriter();

        // What DashboardConnect.ConnectAsync(client, null, ...) passes the adapter: no --role.
        var code = ConnectCommand.Run(["--json", "--token-file", token, "--only", ConnectClients.ClaudeCode], profile, Path.Combine(root, "appdata"), "dotnet", Path.Combine(root, "CoCern.Adapter.dll"), output, error, nativeCacheRoot: Path.Combine(root, "native-cache"));

        Assert.Equal(0, code);
        var written = JsonNode.Parse(File.ReadAllText(settings))!;
        Assert.Equal(4, written["numStartups"]!.GetValue<int>());
        Assert.Equal("x", written["mcpServers"]!["other"]!["command"]!.GetValue<string>());
        var variables = written["mcpServers"]!["cocern"]!["env"]!.AsObject();
        Assert.Equal(token, variables["COCERN_TOKEN_FILE"]!.GetValue<string>());
        Assert.False(variables.ContainsKey("COCERN_ROLE"));
        var client = Assert.Single(JsonNode.Parse(output.ToString())!["clients"]!.AsArray());
        Assert.Equal(ConnectStates.Added, client!["state"]!.GetValue<string>());
        Assert.False(client["admin"]!.GetValue<bool>());
    }

    /// <summary>The worktree this test was compiled from, since a build under an artifacts path has no checkout above it.</summary>
    private static string Checkout([CallerFilePath] string file = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", ".."));
}
