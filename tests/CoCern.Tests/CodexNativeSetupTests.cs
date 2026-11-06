using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Adapter;
using CoCern.Adapter.Node;
using CoCern.Adapter.Setup;
using Xunit;

namespace CoCern.Tests;

public sealed class CodexNativeSetupTests
{
    [Fact]
    public void Explicit_native_setup_restores_owned_token_privacy_without_rotating_its_bytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "cocern-native-permissions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "synthetic-token");
            var bytes = System.Text.Encoding.UTF8.GetBytes("synthetic-existing-token\r\n");
            File.WriteAllBytes(path, bytes);
            if (!OperatingSystem.IsWindows() && !NodeState.IsPrivateFile(path))
            {
                Assert.Throws<IOException>(() => NodeState.ProtectOwnedFile(path));
                Assert.Equal(bytes, File.ReadAllBytes(path));
                NodeState.WritePrivately(path, bytes);
            }
            NodeState.ProtectOwnedFile(path);
            Assert.True(NodeState.IsPrivateFile(path));
            Assert.Equal(bytes, File.ReadAllBytes(path));
            NodeState.ProtectOwnedFile(path);
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Native_catalog_preferences_survive_development_launch_without_changing_verified_credentials()
    {
        var start = new System.Diagnostics.ProcessStartInfo();
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("COCERN_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["COCERN_ROLE"] = "admin";
        start.Environment["COCERN_PROFILE"] = "diagnostics";
        start.Environment["COCERN_TOKEN"] = "untrusted-override";
        start.Environment["COCERN_URL"] = "http://127.0.0.1:6666";
        NativeRegistration.RestrictPrivateEnvironment(start);
        var connection = new AdapterOptions("http://127.0.0.1:4321", "verified-token", signalR: true) { NodeFolder = "verified-private-folder" };
        var options = NativeRegistration.ClientOptions(connection, key => start.Environment.TryGetValue(key, out var value) ? value : null);
        Assert.True(options.Admin);
        Assert.True(options.Diagnostics);
        Assert.True(options.SignalR);
        Assert.Equal(connection.BaseUri, options.BaseUri);
        Assert.Equal("verified-token", options.Token);
        Assert.Equal("verified-private-folder", options.NodeFolder);
        var worker = NativeRegistration.ClientOptions(connection, _ => null);
        Assert.False(worker.Admin);
        Assert.False(worker.Diagnostics);
    }

    [Fact]
    public void Native_hook_repair_preserves_unrelated_handlers_and_is_idempotent()
    {
        const string oldCommand = "dotnet old.dll --native-hook codex --connection \"private\" --profile \"profile\"";
        const string command = "dotnet current.dll --native-hook codex --connection \"private\" --profile \"profile\"";
        var existing = JsonNode.Parse(JsonSerializer.Serialize(new { description = "My hooks", hooks = new
        {
            SessionStart = new[] { new { hooks = new[] { new { type = "command", command = oldCommand }, new { type = "command", command = "my-startup-helper" } } } },
            Stop = new[] { new { hooks = new[] { new { type = "command", command = "my-stop-helper" } } } }
        } }))!.AsObject();
        var desired = JsonSerializer.SerializeToElement(new
        {
            SessionStart = new[] { new { hooks = new[] { new { type = "command", command, timeout = 20 } } } },
            PreToolUse = new[] { new { matcher = "^mcp__cocern__.*$", hooks = new[] { new { type = "command", command, timeout = 20 } } } }
        });
        var first = CodexNativeSetup.MergeHooks(existing, desired).ToJsonString();
        Assert.Contains("my-startup-helper", first);
        Assert.Contains("my-stop-helper", first);
        Assert.Contains("My hooks", first);
        Assert.DoesNotContain("old.dll", first);
        var second = CodexNativeSetup.MergeHooks(JsonNode.Parse(first)!.AsObject(), desired).ToJsonString();
        Assert.Equal(first, second);
    }

    [Fact]
    public void A_different_or_unreachable_session_cannot_make_native_readiness_ready()
    {
        var handle = new SessionHandle("own", "synthetic-private-token", "repo", "test", DateTimeOffset.UtcNow);
        AdapterReply Directory(string session, string repository, string provider, bool reachable) => new(true, "Ok", JsonSerializer.SerializeToElement(new
        { items = new[] { new { relaySessionId = session, repositoryId = repository, provider, reachable } } }));
        Assert.True(NativeStartupEvidence.HasReachableRoute(Directory("own", "repo", "codex", true), handle));
        Assert.False(NativeStartupEvidence.HasReachableRoute(Directory("other", "repo", "codex", true), handle));
        Assert.False(NativeStartupEvidence.HasReachableRoute(Directory("own", "another-repo", "codex", true), handle));
        Assert.False(NativeStartupEvidence.HasReachableRoute(Directory("own", "repo", "claude", true), handle));
        Assert.False(NativeStartupEvidence.HasReachableRoute(Directory("own", "repo", "codex", false), handle));
    }

    [Fact]
    public void Read_only_native_inspection_does_not_create_connection_hook_or_skill_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "cocern-native-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var result = CodexNativeSetup.Inspect(root, Path.Combine(root, ".codex"), new Dictionary<string, string>(), Path.Combine(root, "cache"));
            Assert.Equal("setup_required", result.State);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
