using System.Security.Cryptography;
using System.Text.Json;
using CoCern.Adapter;
using CoCern.Adapter.Node;
using CoCern.Adapter.Setup;
using CoCern.Contracts;
using CoCern.Setup;
using Xunit;

namespace CoCern.Tests;

public sealed class InstallerRepairTests
{
    [Fact]
    public void A_corrupt_package_cannot_replace_an_installation_or_its_credentials()
    {
        var temporary = Path.Combine(Path.GetTempPath(), "cocern-package-fence-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = Path.Combine(temporary, "source"); var install = Path.Combine(temporary, "installed");
            Directory.CreateDirectory(source);
            var names = new[] { "CoCern.Setup.exe", "CoCern.Host.exe", "CoCern.Adapter.exe" };
            foreach (var name in names) { var path = SetupPackage.Contained(source, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "synthetic package content"); }
            foreach (var name in new[] { "CoCern.Setup.runtimeconfig.json", "CoCern.Host.runtimeconfig.json", "CoCern.Adapter.runtimeconfig.json" }) File.WriteAllText(SetupPackage.Contained(source, name), "{\"runtimeOptions\":{\"includedFrameworks\":[]}}");
            var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Select(path => new PackageFile(Path.GetRelativePath(source, path), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))).ToArray();
            File.WriteAllBytes(Path.Combine(source, SetupPackage.ManifestName), JsonSerializer.SerializeToUtf8Bytes(new PackageManifest(1, "win-x64", true, files), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var current = SetupPackage.Install(source, install);
            var token = Path.Combine(install, "data", "synthetic-token"); Directory.CreateDirectory(Path.GetDirectoryName(token)!); File.WriteAllText(token, "existing-credential");
            File.WriteAllText(SetupPackage.Contained(source, "CoCern.Adapter.exe"), "corrupt");
            Assert.Throws<IOException>(() => SetupPackage.Install(source, install));
            Assert.Equal(current, SetupPackage.Current(install));
            Assert.Equal("existing-credential", File.ReadAllText(token));
            Assert.Throws<IOException>(() => SetupPackage.Contained(install, "../outside"));
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }

    [Fact]
    public async Task Repair_preview_is_read_only_and_keeps_credentials_and_tool_permissions()
    {
        var root = Path.Combine(Path.GetTempPath(), "cocern-repair-fence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var token = Path.Combine(root, "token"); NodeState.WritePrivately(token, System.Text.Encoding.UTF8.GetBytes("synthetic-credential"));
            var profile = Path.Combine(root, "profile"); var adapter = typeof(AdapterOptions).Assembly.Location;
            var config = Path.Combine(profile, ".codex", "config.toml");
            ClientSettings.PlanCodex(config, "old-dotnet", Path.Combine(root, "missing-codex-adapter.dll"), new Dictionary<string, string> { ["COCERN_TOKEN_FILE"] = token, ["COCERN_ROLE"] = "admin" }).Apply(TimeProvider.System);
            var claude = Path.Combine(profile, ".claude.json");
            ClientSettings.PlanClaudeCode(claude, "old-dotnet", Path.Combine(root, "missing-adapter.dll"), new Dictionary<string, string> { ["COCERN_TOKEN_FILE"] = token, ["COCERN_ROLE"] = "admin", ["COCERN_URL"] = "http://127.0.0.1:5270/" }).Apply(TimeProvider.System);
            var before = File.ReadAllBytes(config); using var output = new StringWriter();
            var env = new SetupEnvironment { Profile = profile, DotnetPath = "dotnet", AdapterDll = adapter, RunKey = new NoRunKey(), Transport = new NodeTransport { HubHandler = () => new HealthyHub() }, StartNode = (_, _, _) => throw new InvalidOperationException("No process operation is permitted in preview."), Out = output, Error = TextWriter.Null };
            var exit = await RepairCommand.RunAsync(["--check", "--json", "--token-file", token, "--hub", "http://127.0.0.1:5271/"], env, CancellationToken.None, Path.Combine(root, "cache"));
            Assert.Equal(0, exit);
            Assert.Equal(before, File.ReadAllBytes(config));
            Assert.Equal("synthetic-credential", File.ReadAllText(token));
            Assert.False(Directory.Exists(Path.Combine(root, "cache")));
            Assert.Contains("admin", File.ReadAllText(config));
            Assert.False(Directory.Exists(Path.Combine(profile, ".agents")));
            Assert.Contains("plannedChanges", output.ToString());
            Assert.False(RepairCommand.OwnsNodeEntry(new Dictionary<string, string> { ["COCERN_TOKEN_FILE"] = token }, Path.Combine(root, "node")));
            output.GetStringBuilder().Clear();
            exit = await RepairCommand.RunAsync(["--json", "--token-file", token, "--hub", "http://127.0.0.1:5271/"], env, CancellationToken.None, Path.Combine(root, "cache"));
            Assert.Equal(0, exit);
            Assert.Equal("synthetic-credential", File.ReadAllText(token));
            Assert.Contains("admin", File.ReadAllText(config));
            Assert.True(File.Exists(CodexNativeSetup.SkillPath(profile)));
            Assert.False(File.Exists(Path.Combine(profile, ".codex", "hooks.json")));
            Assert.DoesNotContain("--native-client", File.ReadAllText(config));
            Assert.DoesNotContain("missing-codex-adapter", File.ReadAllText(config));
            Assert.True(ClientSettings.IsStandardCodexLaunchAvailable(config));
            Assert.Contains("appliedChanges", output.ToString());
            using var status = JsonDocument.Parse(output.ToString());
            Assert.True(status.RootElement.GetProperty("repairPerformed").GetBoolean());
            Assert.NotEmpty(status.RootElement.GetProperty("appliedChanges").EnumerateArray());
            Assert.Empty(status.RootElement.GetProperty("plannedChanges").EnumerateArray());
            var repairedChecks = status.RootElement.GetProperty("checks").EnumerateArray().ToArray();
            Assert.Equal("passed", repairedChecks.Single(item => item.GetProperty("id").GetString() == "codex").GetProperty("state").GetString());
            Assert.Equal("passed", repairedChecks.Single(item => item.GetProperty("id").GetString() == "workflow-skill").GetProperty("state").GetString());
            Assert.DoesNotContain(repairedChecks, item => item.GetProperty("id").GetString() is "native-setup" or "native-startup");
            using var repaired = JsonDocument.Parse(File.ReadAllBytes(claude));
            var entry = repaired.RootElement.GetProperty("mcpServers").GetProperty("cocern");
            Assert.Equal("http://127.0.0.1:5271/", entry.GetProperty("env").GetProperty("COCERN_URL").GetString());
            Assert.Equal("admin", entry.GetProperty("env").GetProperty("COCERN_ROLE").GetString());
            Assert.DoesNotContain("missing-adapter", entry.GetRawText());
            File.WriteAllText(CodexNativeSetup.SkillPath(profile), "custom workflow guidance");
            var hooks = Path.Combine(profile, ".codex", "hooks.json");
            const string disabled = "{\"disableAllHooks\":true,\"hooks\":{\"SessionStart\":[{\"hooks\":[{\"command\":\"retained --native-hook codex\",\"disabled\":true}]}]}}";
            File.WriteAllText(hooks, disabled);
            output.GetStringBuilder().Clear();
            Assert.Equal(0, await RepairCommand.RunAsync(["--json", "--token-file", token, "--hub", "http://127.0.0.1:5271/"], env, CancellationToken.None, Path.Combine(root, "cache")));
            using var retained = JsonDocument.Parse(output.ToString());
            Assert.Empty(retained.RootElement.GetProperty("plannedChanges").EnumerateArray());
            Assert.Empty(retained.RootElement.GetProperty("appliedChanges").EnumerateArray());
            var customSkill = retained.RootElement.GetProperty("checks").EnumerateArray().Single(item => item.GetProperty("id").GetString() == "workflow-skill");
            Assert.Equal("warning", customSkill.GetProperty("state").GetString());
            Assert.False(customSkill.GetProperty("repairable").GetBoolean());
            Assert.Equal("custom workflow guidance", File.ReadAllText(CodexNativeSetup.SkillPath(profile)));
            Assert.Equal(disabled, File.ReadAllText(hooks));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Local_host_node_setup_keeps_enrollment_and_app_credentials_separate()
    {
        var root = Path.Combine(Path.GetTempPath(), "cocern-local-node-fence-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data"); var node = Path.Combine(root, "node"); var profile = Path.Combine(root, "profile");
        Directory.CreateDirectory(data);
        try
        {
            var pin = new string('a', 64); var enrollments = new List<string>(); var starts = 0;
            File.WriteAllText(Path.Combine(data, LocalHubRecord.FileName), new LocalHubRecord("https://localhost:5443/", pin).ToJson());
            NodeState.WritePrivately(Path.Combine(data, "join-code"), "synthetic-local-code"u8.ToArray());
            var config = Path.Combine(profile, ".codex", "config.toml");
            ClientSettings.PlanCodex(config, "dotnet", typeof(AdapterOptions).Assembly.Location, new Dictionary<string, string> { ["COCERN_TOKEN_FILE"] = Path.Combine(data, "token"), ["COCERN_ROLE"] = "admin" }).Apply(TimeProvider.System);
            var original = File.ReadAllBytes(config);
            var environment = new SetupEnvironment
            {
                Profile = profile, DotnetPath = "dotnet", AdapterDll = typeof(AdapterOptions).Assembly.Location, RunKey = new FakeRunKey(),
                Transport = new NodeTransport { ProbeFingerprint = (_, _) => Task.FromResult(pin), CreateHandler = (_, _) => new LocalNodeHub(enrollments), HubHandler = () => new HealthyHub(), Chats = _ => Task.FromResult(new System.Text.Json.Nodes.JsonArray()) },
                StartNode = (_, _, _) => starts++, Out = TextWriter.Null, Error = TextWriter.Null
            };
            Assert.Equal(0, await LocalNodeSetup.RunAsync(data, node, environment, CancellationToken.None));
            var enrolled = NodeState.ReadEnrollment(node)!;
            Assert.Single(enrollments);
            Assert.False(File.Exists(Path.Combine(node, "join-code")));
            Assert.Equal(original, File.ReadAllBytes(config));
            Assert.Equal(0, await LocalNodeSetup.RunAsync(data, node, environment, CancellationToken.None));
            Assert.Single(enrollments);
            Assert.Equal(enrolled.Credential, NodeState.ReadEnrollment(node)!.Credential);
            Assert.Equal(2, starts);
            File.WriteAllText(Path.Combine(data, LocalHubRecord.FileName), new LocalHubRecord("https://localhost:5444/", pin).ToJson());
            await Assert.ThrowsAsync<AdapterConfigurationException>(() => LocalNodeSetup.RunAsync(data, node, environment, CancellationToken.None));
            Assert.Equal(enrolled.Credential, NodeState.ReadEnrollment(node)!.Credential);
            Assert.Equal(original, File.ReadAllBytes(config));
            Assert.Equal(2, starts);
            // Exercise Repair's enrolled-node branch, including a second preview after repairing
            // the actual stale launch. Optional native wake proof never makes standard setup broken.
            File.Delete(config);
            ClientSettings.PlanCodex(config, "removed-dotnet", Path.Combine(root, "removed-node-adapter.dll"), new Dictionary<string, string>
                { ["COCERN_NODE_DIR"] = node, ["COCERN_ROLE"] = "admin", ["COCERN_PROFILE"] = "diagnostics" }).Apply(TimeProvider.System);
            var stale = File.ReadAllBytes(config);
            var skill = CodexNativeSetup.SkillPath(profile); Directory.CreateDirectory(Path.GetDirectoryName(skill)!); File.WriteAllText(skill, "custom enrolled workflow");
            var hooks = Path.Combine(profile, ".codex", "hooks.json");
            const string disabled = "{\"disableAllHooks\":true,\"hooks\":{\"SessionStart\":[{\"hooks\":[{\"command\":\"retained --native-hook codex\",\"disabled\":true}]}]}}";
            File.WriteAllText(hooks, disabled);
            using var output = new StringWriter();
            var repair = new SetupEnvironment { Profile = profile, DotnetPath = environment.DotnetPath, AdapterDll = environment.AdapterDll, RunKey = environment.RunKey,
                Transport = environment.Transport, StartNode = (_, _, _) => starts++, Out = output, Error = TextWriter.Null };
            Assert.Equal(0, await RepairCommand.RunAsync(["--check", "--json", "--data-dir", node], repair, CancellationToken.None));
            using var preview = JsonDocument.Parse(output.ToString());
            Assert.Equal("needs_repair", preview.RootElement.GetProperty("checks").EnumerateArray().Single(item => item.GetProperty("id").GetString() == "codex").GetProperty("state").GetString());
            Assert.Equal(stale, File.ReadAllBytes(config));
            Assert.Equal(2, starts);
            output.GetStringBuilder().Clear();
            Assert.Equal(0, await RepairCommand.RunAsync(["--json", "--data-dir", node], repair, CancellationToken.None));
            Assert.Equal(3, starts);
            Assert.True(ClientSettings.IsStandardCodexLaunchAvailable(config));
            Assert.Contains("COCERN_ROLE = 'admin'", File.ReadAllText(config));
            Assert.Contains("COCERN_PROFILE = 'diagnostics'", File.ReadAllText(config));
            output.GetStringBuilder().Clear();
            Assert.Equal(0, await RepairCommand.RunAsync(["--check", "--json", "--data-dir", node], repair, CancellationToken.None));
            using var repeated = JsonDocument.Parse(output.ToString());
            Assert.Empty(repeated.RootElement.GetProperty("plannedChanges").EnumerateArray());
            Assert.Equal("passed", repeated.RootElement.GetProperty("checks").EnumerateArray().Single(item => item.GetProperty("id").GetString() == "codex").GetProperty("state").GetString());
            Assert.DoesNotContain(repeated.RootElement.GetProperty("checks").EnumerateArray(), item => item.GetProperty("id").GetString() is "native-setup" or "native-startup");
            Assert.Equal(disabled, File.ReadAllText(hooks));
            Assert.Equal("custom enrolled workflow", File.ReadAllText(skill));
            Assert.Equal(enrolled.Credential, NodeState.ReadEnrollment(node)!.Credential);
            Assert.Equal(3, starts);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class LocalNodeHub(List<string> enrollments) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = "{}";
            if (request.RequestUri!.AbsolutePath == "/api/discovery/enroll")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var machineId = body.RootElement.GetProperty("machineId").GetString()!;
                enrollments.Add(machineId);
                Assert.Equal("synthetic-local-code", body.RootElement.GetProperty("joinCode").GetString());
                content = JsonSerializer.Serialize(new { machineId, credential = new string('b', 64) });
            }
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(content) };
        }
    }

    private sealed class NoRunKey : IRunKey
    {
        public string? Get(string name) => throw new InvalidOperationException("No registry operation is permitted in this local preview.");
        public void Set(string name, string value) => throw new InvalidOperationException("No registry operation is permitted in preview.");
    }

    private sealed class HealthyHub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
