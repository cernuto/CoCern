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
    public void The_diagnostics_profile_offers_the_read_only_diagnosis_and_neither_setup_tool()
    {
        Assert.True(CoordinationTools.IsAllowed(RelayTools.HubDiagnose, admin: true, diagnostics: true));
        Assert.False(CoordinationTools.IsAllowed(RelayTools.HubDiagnose, admin: false, diagnostics: true));
        Assert.Contains(RelayTools.HubDiagnose, CoordinationTools.NamesForRole(true, diagnostics: true));
        Assert.DoesNotContain(RelayTools.HubDiagnose, CoordinationTools.NamesForRole(false, diagnostics: true));
        foreach (var setup in new[] { RelayTools.AddPc, RelayTools.ConnectApp })
        {
            Assert.False(CoordinationTools.IsAllowed(setup, admin: true, diagnostics: true));
            Assert.DoesNotContain(setup, CoordinationTools.NamesForRole(true, diagnostics: true));
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
    public void Being_off_the_hubs_pc_is_a_denial_of_its_own()
    {
        Assert.Equal(RelayErrorKind.Denied, RelayErrorCodes.Classify(RelayErrorCodes.HubPcRequired));
        Assert.NotEqual(RelayErrorCodes.AdministratorRequired, RelayErrorCodes.HubPcRequired);
    }

    [Fact]
    public void Connect_app_takes_a_known_client_and_an_optional_boolean_admin()
    {
        Assert.True(RelayToolCatalog.TryGet(RelayTools.ConnectApp, out var tool));
        Assert.True(tool.IsMutation);
        Assert.Equal(["client"], tool.RequiredFields);
        foreach (var schema in new[] { RelayToolCatalog.InputSchema(tool), RelayToolCatalog.ForwardingSchema(tool) })
        {
            var properties = schema.GetProperty("properties");
            Assert.Equal("boolean", properties.GetProperty("admin").GetProperty("type").GetString());
            Assert.DoesNotContain("admin", schema.GetProperty("required").EnumerateArray().Select(field => field.GetString()));
            Assert.Equal(ConnectClients.All, properties.GetProperty("client").GetProperty("enum").EnumerateArray().Select(client => client.GetString()!));
        }
    }

    /// <summary>A hub with many network adapters has many addresses, each a long command; past the wire limit the whole reply would be refused, and past the token budget compaction would cut a command short.</summary>
    [Fact]
    public void An_add_pc_reply_keeps_whole_commands_within_the_wire_limit_and_says_when_it_left_addresses_out()
    {
        var addresses = Enumerable.Range(1, 20).Select(index => $"https://192.0.2.{index}:5273/").Select(url => new DashboardHubAddress { Url = url, Command = DashboardAddPc.Command(url, "JOIN-CODE-1234", new string('A', 64)) }).ToArray();
        var counter = new O200kTokenCounter();
        var data = AdminToolsService.AddPcReply(new DashboardAddPcStatus { AdapterFolder = "folder", Addresses = addresses, FirewallCommand = "firewall" }, counter);

        Assert.True(JsonSerializer.SerializeToUtf8Bytes(Replies.Ok(data), ToolResults.JsonOptions).Length <= RelayLimits.MaxPayloadBytes);
        Assert.True(RelayToolCatalog.TryGet(RelayTools.AddPc, out var tool));
        Assert.True(counter.Count(data.ToJsonString()) <= tool.TokenBudget);
        var kept = data["status"]!["addresses"]!.AsArray();
        Assert.InRange(kept.Count, 1, 19);
        Assert.True(data["truncated"]!.GetValue<bool>());
        Assert.Equal(20 - kept.Count, data["moreAddresses"]!.GetValue<int>());
        Assert.All(kept, (address, index) => Assert.Equal(addresses[index].Command, address!["command"]!.GetValue<string>()));
    }

    [Fact]
    public void An_add_pc_reply_warns_of_the_secret_only_when_it_holds_one()
    {
        var counter = new O200kTokenCounter();
        var filled = AdminToolsService.AddPcReply(new DashboardAddPcStatus { Addresses = [new() { Url = "https://hub:5273/", Command = "command" }] }, counter);
        Assert.Contains("secret", filled["secretWarning"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        Assert.False(filled["truncated"]!.GetValue<bool>());
        var unavailable = AdminToolsService.AddPcReply(new DashboardAddPcStatus { Unavailable = "Not yet." }, counter);
        Assert.Null(unavailable["secretWarning"]);
        Assert.Equal("Not yet.", unavailable["status"]!["unavailable"]!.GetValue<string>());
    }

    [Fact]
    public void The_certificate_backup_is_reported_missing_matching_or_mismatched_without_a_path_or_key()
    {
        var path = Path.Combine(root, "hub.pfx");
        Assert.Equal(CertificateBackupStates.Off, AdminToolsService.BackupState(null, null));
        using var certificate = HubCertificate.LoadOrCreate(path, "hub");
        var fingerprint = CertificateFingerprint.Of(certificate);
        Assert.Equal(CertificateBackupStates.Matches, AdminToolsService.BackupState(path, fingerprint));

        var other = Path.Combine(root, "other.pfx");
        using (HubCertificate.LoadOrCreate(other, "other")) { }
        File.Copy(other, path + ".backup", overwrite: true);
        Assert.Equal(CertificateBackupStates.Mismatch, AdminToolsService.BackupState(path, fingerprint));

        File.WriteAllBytes(path + ".backup", [1, 2, 3]);
        Assert.Equal(CertificateBackupStates.Mismatch, AdminToolsService.BackupState(path, fingerprint));

        File.Delete(path + ".backup");
        Assert.Equal(CertificateBackupStates.Missing, AdminToolsService.BackupState(path, fingerprint));
        Assert.False(File.Exists(path + ".backup"));
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
        var code = ConnectCommand.Run(["--json", "--token-file", token, "--only", ConnectClients.ClaudeCode], profile, Path.Combine(root, "appdata"), "dotnet", Path.Combine(root, "CoCern.Adapter.dll"), output, error);

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

    /// <summary>The cutoff is the label rule's own minutes plus how often activity reaches the directory, so the page and the docs state the sum.</summary>
    [Fact]
    public void The_close_by_label_copy_states_the_real_cutoff()
    {
        var minutes = RelayLimits.LabelCloseActiveMinutes + RelayLimits.AttentionActivityReportMinutes;
        Assert.Equal(70, minutes);
        var checkout = Checkout();
        var page = File.ReadAllText(Path.Combine(checkout, "src", "CoCern.Dashboard", "Components", "CloseSessionsControl.razor"));
        Assert.Contains($"about the last {minutes} minutes", page);
        Assert.DoesNotContain("last 10 minutes", page);
        var docs = File.ReadAllText(Path.Combine(checkout, "docs", "reference.md"));
        Assert.Contains($"about the last {minutes} minutes", docs);
        Assert.DoesNotContain("a call in the last 10 minutes", docs);
        Assert.True(RelayToolCatalog.TryGet(RelayTools.ControlRequest, out var control));
        var description = RelayToolCatalog.InputSchema(control).GetProperty("properties").GetProperty("labelPrefix").GetProperty("description").GetString();
        Assert.Contains($"about the last {minutes} minutes", description);
    }

    /// <summary>The worktree this test was compiled from, since a build under an artifacts path has no checkout above it.</summary>
    private static string Checkout([CallerFilePath] string file = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", ".."));
}
