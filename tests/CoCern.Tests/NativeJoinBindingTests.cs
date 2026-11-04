using System.Diagnostics;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Adapter;
using CoCern.Adapter.Node;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CoCern.Tests;

public sealed class NativeJoinBindingTests(NativeJoinBindingTests.ProbeFixture probe) : IClassFixture<NativeJoinBindingTests.ProbeFixture>
{
    private const string Chat = "11111111-2222-4333-8444-555555555555";

    [Fact]
    public async Task Reopen_keeps_one_secret_generation_and_frozen_open_attempt()
    {
        using var fixture = new ScopeFixture();
        string secret, generation, request, arguments;
        await using (var first = await fixture.Acquire())
        {
            secret = first.Secret; generation = first.Generation; request = first.OpenRequestId;
            arguments = first.FrozenOpenArguments.GetRawText();
            Assert.Equal(32, Convert.FromBase64String(secret).Length);
            Assert.False(first.ToString().Contains(secret, StringComparison.Ordinal));
            Assert.False(JsonSerializer.Serialize(first).Contains(secret, StringComparison.Ordinal));
        }
        await using var reopened = await fixture.Acquire();
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reopened.Secret))));
        Assert.Equal(generation, reopened.Generation);
        Assert.Equal(request, reopened.OpenRequestId);
        Assert.True(arguments == reopened.FrozenOpenArguments.GetRawText(), "Frozen open arguments changed.");
        Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(reopened.StatePath)!, "*.json"));
    }

    [Fact]
    public async Task Concurrent_independent_process_leases_resume_one_pending_attempt()
    {
        using var fixture = new ScopeFixture();
        using var first = probe.Start(fixture, "hold");
        Assert.Equal("attempting", await Line(first));
        var original = await Line(first);
        using var originalData = JsonDocument.Parse(original!);
        var key = originalData.RootElement.GetProperty("key").GetString()!;
        Assert.True(key.Length == 64 && key.All(Uri.IsHexDigit));
        var lockPath = Path.Combine(fixture.Folder, "sessions", "native", key + ".lock");
        Assert.ThrowsAny<IOException>(() => { using var forbidden = new FileStream(lockPath, FileMode.Open, FileAccess.Write, FileShare.None); });
        var statePath = Path.ChangeExtension(lockPath, ".json");
        var stateBeforeCancel = File.ReadAllBytes(statePath);
        using var second = probe.Start(fixture, "hold");
        Assert.Equal("attempting", await Line(second));
        var waiting = Line(second);
        var completed = await Task.WhenAny(waiting, Task.Delay(250));
        try
        {
            Assert.NotSame(waiting, completed);
            using var cancelled = probe.Start(fixture, "cancel");
            Assert.Equal("attempting", await Line(cancelled));
            Assert.Equal("cancelled", await Line(cancelled));
            await cancelled.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, cancelled.ExitCode);
            Assert.True(stateBeforeCancel.SequenceEqual(File.ReadAllBytes(statePath)), "A cancelled contender changed the winner's state.");
            Assert.ThrowsAny<IOException>(() => { using var forbidden = new FileStream(lockPath, FileMode.Open, FileAccess.Write, FileShare.None); });

            // Lose the first caller after persistence, without completing an open or returning its handle.
            first.Kill(entireProcessTree: true);
            await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(original, await waiting);
            await second.StandardInput.WriteLineAsync("release");
            await second.StandardInput.FlushAsync();
            await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, second.ExitCode);
            using var reopened = probe.Start(fixture, "once");
            Assert.Equal("attempting", await Line(reopened));
            Assert.Equal(original, await Line(reopened));
            await reopened.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, reopened.ExitCode);
        }
        finally { Stop(first); Stop(second); }
    }

    [Fact]
    public async Task Unrelated_chats_and_checkouts_do_not_share_a_binding()
    {
        using var fixture = new ScopeFixture();
        string original;
        await using (var binding = await fixture.Acquire()) original = binding.BindingKey;
        var variants = new[]
        {
            ("claude", Chat, "machine", "repo", fixture.Workspace),
            ("codex", "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee", "machine", "repo", fixture.Workspace),
            ("codex", Chat, "other-machine", "repo", fixture.Workspace),
            ("codex", Chat, "machine", "other-repo", fixture.Workspace),
            ("codex", Chat, "machine", "repo", fixture.CreateWorktree())
        };
        foreach (var (provider, id, machine, repo, workspace) in variants)
        {
            await using var binding = await NativeJoinBinding.AcquireAsync(fixture.Folder, provider, id, machine, repo, workspace, default);
            Assert.NotEqual(original, binding.BindingKey);
        }
        var anotherFolder = fixture.Connection("another-cache");
        await using var other = await NativeJoinBinding.AcquireAsync(anotherFolder, "codex", Chat, "machine", "repo", fixture.Workspace, default);
        Assert.NotEqual(original, other.BindingKey);
    }

    [Fact]
    public async Task Actual_event_subfolders_and_windows_case_converge_without_using_adapter_cwd()
    {
        using var fixture = new ScopeFixture();
        var subfolder = Directory.CreateDirectory(Path.Combine(fixture.Workspace, "nested")).FullName;
        string key, request;
        await using (var first = await fixture.Acquire()) { key = first.BindingKey; request = first.OpenRequestId; }
        await using (var nested = await fixture.Acquire(subfolder))
        {
            Assert.Equal(key, nested.BindingKey);
            Assert.Equal(request, nested.OpenRequestId);
        }
        if (OperatingSystem.IsWindows())
        {
            await using var casing = await fixture.Acquire(subfolder.ToUpperInvariant());
            Assert.Equal(key, casing.BindingKey);
        }
        await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.Acquire("relative-event-cwd"));
    }

    [Fact]
    public async Task Rotated_credential_never_selects_the_prior_binding()
    {
        using var fixture = new ScopeFixture();
        string key;
        await using (var first = await fixture.Acquire()) key = first.BindingKey;
        File.WriteAllText(fixture.TokenFile, "other-synthetic-principal");
        await using var changed = await fixture.Acquire();
        Assert.NotEqual(key, changed.BindingKey);
        Assert.Null(changed.CurrentHandle);
        Assert.DoesNotContain("other-synthetic-principal", File.ReadAllText(changed.StatePath));
    }

    [Fact]
    public async Task Raw_token_only_or_missing_connection_never_creates_private_binding_state()
    {
        using var fixture = new ScopeFixture();
        var folder = Directory.CreateDirectory(Path.Combine(fixture.Root, "raw-only")).FullName;
        Assert.Null(LocalWakeConnection.Create(new AdapterOptions("http://127.0.0.1:5271/", "raw-synthetic"), null, folder));
        await Assert.ThrowsAnyAsync<IOException>(() => NativeJoinBinding.AcquireAsync(folder, "codex", Chat, "machine", "repo", fixture.Workspace, default));
        Assert.Empty(Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Corrupt_and_publicly_readable_state_are_refused_without_replacement()
    {
        using var fixture = new ScopeFixture();
        string path;
        await using (var first = await fixture.Acquire()) path = first.StatePath;
        var original = File.ReadAllBytes(path);
        File.WriteAllText(path, "corrupt-private-state");
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.Acquire());
        Assert.Equal("corrupt-private-state", File.ReadAllText(path));
        File.WriteAllBytes(path, original);
        MakePublic(path);
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.Acquire());
        Assert.True(original.SequenceEqual(File.ReadAllBytes(path)));
    }

    [Fact]
    public async Task Length_prefixed_components_cannot_alias_and_cancellation_keeps_the_winner()
    {
        using var fixture = new ScopeFixture();
        await using var first = await NativeJoinBinding.AcquireAsync(fixture.Folder, "codex", Chat, "a:b", "c", fixture.Workspace, default);
        await using var other = await NativeJoinBinding.AcquireAsync(fixture.Folder, "codex", Chat, "a", "b:c", fixture.Workspace, default);
        Assert.NotEqual(first.BindingKey, other.BindingKey);
        var bytes = File.ReadAllBytes(first.StatePath);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeJoinBinding.AcquireAsync(fixture.Folder, "codex", Chat, "a:b", "c", fixture.Workspace, cancel.Token));
        Assert.True(bytes.SequenceEqual(File.ReadAllBytes(first.StatePath)));
    }

    [Fact]
    public async Task Only_the_exact_authenticated_open_can_complete_and_reopen_its_private_handle()
    {
        using var fixture = new ScopeFixture();
        var session = Guid.NewGuid().ToString("N");
        var reply = OpenReply(session);
        string statePath;
        await using (var binding = await fixture.Acquire())
        {
            statePath = binding.StatePath;
            var handoff = SessionHandoff.ForFolder(fixture.Folder, new("codex", Chat));
            var wrongArgs = JsonNode.Parse(binding.FrozenOpenArguments.GetRawText())!.AsObject();
            wrongArgs["requestId"] = Guid.NewGuid().ToString("N");
            Assert.False(handoff.CompleteNativeJoin(binding, fixture.Options, JsonSerializer.SerializeToElement(wrongArgs), reply));
            Assert.False(handoff.CompleteNativeJoin(binding, fixture.Options, binding.FrozenOpenArguments, reply with { Ok = false }));
            Assert.False(handoff.CompleteNativeJoin(binding, new AdapterOptions(fixture.Options.BaseUri.AbsoluteUri, "stale-synthetic-principal"), binding.FrozenOpenArguments, reply));
            Assert.False(SessionHandoff.ForFolder(fixture.Folder, new("claude", Chat)).CompleteNativeJoin(binding, fixture.Options, binding.FrozenOpenArguments, reply));
            Assert.Null(binding.CurrentHandle);
            Assert.True(handoff.CompleteNativeJoin(binding, fixture.Options, binding.FrozenOpenArguments, reply));
            Assert.True(handoff.CompleteNativeJoin(binding, fixture.Options, binding.FrozenOpenArguments, reply));
            Assert.Equal(session, binding.CurrentHandleReference);
            Assert.Equal(session, binding.CurrentHandle!.SessionId);
            Assert.Equal(binding.OwnershipKey, binding.CurrentHandle.NativeBindingKey);
            Assert.True(NodeState.IsPrivateFile(NodeState.SessionHandlePath(fixture.Folder, session)));
            Assert.False(File.ReadAllText(statePath).Contains("synthetic-session-token", StringComparison.Ordinal));
            Assert.False(JsonSerializer.Serialize(binding.CurrentHandle).Contains("nativeBindingKey", StringComparison.OrdinalIgnoreCase));
        }
        await using var reopened = await fixture.Acquire();
        Assert.Equal(session, reopened.CurrentHandle!.SessionId);
        Assert.True(reopened.CurrentHandle.SessionToken == "synthetic-session-token");
        Assert.Single(Directory.EnumerateFiles(Path.Combine(fixture.Folder, "sessions"), "*.json"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordinary_records_and_legacy_mappings_never_gain_a_native_ownership_marker(bool mapped)
    {
        using var fixture = new ScopeFixture();
        await using var binding = await fixture.Acquire();
        var session = Guid.NewGuid().ToString("N");
        var handoff = SessionHandoff.ForFolder(fixture.Folder, new("codex", Chat));
        var args = JsonSerializer.SerializeToElement(new { repositoryId = "repo", nativeBindingKey = binding.BindingKey, binding = new { nativeBindingKey = binding.BindingKey } });
        handoff.Record(args, OpenReply(session));
        var path = NodeState.SessionHandlePath(fixture.Folder, session);
        var legacy = NodeState.ReadSessionHandle(path)!;
        if (mapped) NodeState.WriteSessionHandle(fixture.Folder, legacy with { Chat = new("codex", Chat) });
        Assert.Null(NodeState.ReadSessionHandle(path)!.NativeBindingKey);
        var before = File.ReadAllBytes(path);
        Assert.False(handoff.CompleteNativeJoin(binding, fixture.Options, binding.FrozenOpenArguments, OpenReply(session)));
        Assert.True(before.SequenceEqual(File.ReadAllBytes(path)), "Unproven legacy ownership was changed.");
        Assert.Null(binding.CurrentHandleReference);
    }

    [Fact]
    public async Task Foreign_or_missing_handle_references_never_reopen_a_pending_attempt()
    {
        using var fixture = new ScopeFixture();
        string statePath, handlePath;
        await using (var binding = await fixture.Acquire())
        {
            var session = Guid.NewGuid().ToString("N");
            Assert.True(SessionHandoff.ForFolder(fixture.Folder, new("codex", Chat)).CompleteNativeJoin(binding, fixture.Options, binding.FrozenOpenArguments, OpenReply(session)));
            statePath = binding.StatePath; handlePath = NodeState.SessionHandlePath(fixture.Folder, session);
        }
        var originalState = File.ReadAllBytes(statePath);
        var originalHandle = NodeState.ReadSessionHandle(handlePath)!;
        NodeState.WriteSessionHandle(fixture.Folder, originalHandle with { NativeBindingKey = new string('0', 64) });
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.Acquire());
        Assert.True(originalState.SequenceEqual(File.ReadAllBytes(statePath)));
        File.Delete(handlePath);
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.Acquire());
        Assert.True(originalState.SequenceEqual(File.ReadAllBytes(statePath)));
        var corrupt = JsonNode.Parse(originalState)!.AsObject();
        corrupt["currentSessionId"] = "../foreign-session";
        NodeState.WritePrivately(statePath, JsonSerializer.SerializeToUtf8Bytes(corrupt));
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.Acquire());
    }

    [Fact]
    public async Task A_credential_change_during_the_lease_cannot_complete_on_cached_authentication()
    {
        using var fixture = new ScopeFixture();
        await using var binding = await fixture.Acquire();
        var arguments = binding.FrozenOpenArguments;
        File.WriteAllText(fixture.TokenFile, "changed-synthetic-principal");
        Assert.False(SessionHandoff.ForFolder(fixture.Folder, new("codex", Chat)).CompleteNativeJoin(binding, fixture.Options, arguments, OpenReply(Guid.NewGuid().ToString("N"))));
        Assert.ThrowsAny<IOException>(() => binding.CurrentHandle);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Folder, "sessions"), "*.json"));
    }

    [Fact]
    public async Task Node_origin_pin_and_credential_are_separate_private_namespaces()
    {
        using var fixture = new ScopeFixture();
        var folder = Directory.CreateDirectory(Path.Combine(fixture.Root, "node")).FullName;
        var credential = Path.Combine(folder, "credential");
        NodeState.WritePrivately(credential, Encoding.UTF8.GetBytes(new string('a', 64)));
        void Config(string hub, string pin) => NodeState.WritePrivately(Path.Combine(folder, "node.json"),
            JsonSerializer.SerializeToUtf8Bytes(new { machineId = "machine", hubUrl = hub, fingerprint = pin, enrolled = true }));
        Config("https://127.0.0.1:5273/", new string('A', 64));
        string first;
        await using (var original = await NativeJoinBinding.AcquireAsync(folder, "codex", Chat, "machine", "repo", fixture.Workspace, default)) first = original.BindingKey;
        Config("https://127.0.0.1:5274/", new string('A', 64));
        await using (var origin = await NativeJoinBinding.AcquireAsync(folder, "codex", Chat, "machine", "repo", fixture.Workspace, default)) Assert.NotEqual(first, origin.BindingKey);
        Config("https://127.0.0.1:5273/", new string('B', 64));
        await using (var pin = await NativeJoinBinding.AcquireAsync(folder, "codex", Chat, "machine", "repo", fixture.Workspace, default)) Assert.NotEqual(first, pin.BindingKey);
        Config("https://127.0.0.1:5273/", new string('A', 64));
        NodeState.WritePrivately(credential, Encoding.UTF8.GetBytes(new string('b', 64)));
        await using var changed = await NativeJoinBinding.AcquireAsync(folder, "codex", Chat, "machine", "repo", fixture.Workspace, default);
        Assert.NotEqual(first, changed.BindingKey);
    }

    [Theory]
    [InlineData("", Chat, "machine", "repo")]
    [InlineData("unknown", Chat, "machine", "repo")]
    [InlineData("codex", "../../other", "machine", "repo")]
    [InlineData("codex", Chat, "bad\nmachine", "repo")]
    [InlineData("codex", Chat, "machine", "")]
    public async Task Unsafe_native_scope_inputs_never_create_binding_state(string provider, string id, string machine, string repo)
    {
        using var fixture = new ScopeFixture();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => NativeJoinBinding.AcquireAsync(fixture.Folder, provider, id, machine, repo, fixture.Workspace, default));
        Assert.False(Directory.Exists(Path.Combine(fixture.Folder, "sessions")));
    }

    [Fact]
    public async Task Oversized_duplicate_and_denied_private_state_remain_unchanged()
    {
        using var fixture = new ScopeFixture();
        string path;
        await using (var lease = await fixture.Acquire()) path = lease.StatePath;
        var original = File.ReadAllBytes(path);
        foreach (var content in new[] { "{\"version\":1," + Encoding.UTF8.GetString(original)[1..], new string(' ', 17000) })
        {
            NodeState.WritePrivately(path, Encoding.UTF8.GetBytes(content));
            await Assert.ThrowsAnyAsync<IOException>(() => fixture.Acquire());
            Assert.True(Encoding.UTF8.GetBytes(content).SequenceEqual(File.ReadAllBytes(path)));
        }
        NodeState.WritePrivately(path, original);
        using var denied = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.Acquire());
    }

    private static AdapterReply OpenReply(string session) => new(true, "Ok", JsonSerializer.SerializeToElement(new { sessionId = session, sessionToken = "synthetic-session-token" }), 1);

    [Fact]
    public async Task Native_handles_require_the_checkout_binding_and_cannot_be_borrowed_by_legacy_lookup()
    {
        using var fixture = new ScopeFixture();
        await using var binding = await fixture.Acquire();
        var handoff = SessionHandoff.ForFolder(fixture.Folder, new("codex", Chat));
        var nativeSession = Guid.NewGuid().ToString("N");
        Assert.True(handoff.CompleteNativeJoin(binding, fixture.Options, binding.FrozenOpenArguments, OpenReply(nativeSession)));
        Assert.Null(handoff.FindOwn("repo"));
        var oldSession = Guid.NewGuid().ToString("N");
        NodeState.WriteSessionHandle(fixture.Folder, new(oldSession, "legacy-synthetic-token", "repo", "legacy", DateTimeOffset.UtcNow.AddDays(-1), new("codex", Chat)));
        Assert.Equal(oldSession, handoff.FindOwn("repo")!.SessionId);
        var path = NodeState.SessionHandlePath(fixture.Folder, nativeSession);
        var malformed = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        malformed["nativeBindingKey"] = "invalid";
        NodeState.WritePrivately(path, JsonSerializer.SerializeToUtf8Bytes(malformed));
        Assert.Null(NodeState.ReadSessionHandle(path));
        Assert.Equal(oldSession, handoff.FindOwn("repo")!.SessionId);
    }

    [Fact]
    public async Task Changed_generation_or_proof_cannot_select_the_previous_active_handle()
    {
        using var fixture = new ScopeFixture();
        string path;
        await using (var lease = await fixture.Acquire())
        {
            Assert.True(SessionHandoff.ForFolder(fixture.Folder, new("codex", Chat)).CompleteNativeJoin(lease, fixture.Options, lease.FrozenOpenArguments, OpenReply(Guid.NewGuid().ToString("N"))));
            path = lease.StatePath;
        }
        var original = File.ReadAllBytes(path);
        foreach (var property in new[] { "generation", "bindingSecret" })
        {
            var changed = JsonNode.Parse(original)!.AsObject();
            changed["openArguments"]!["nativeJoin"]![property] = property == "generation" ? Guid.NewGuid().ToString("N") : Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            NodeState.WritePrivately(path, JsonSerializer.SerializeToUtf8Bytes(changed));
            await Assert.ThrowsAnyAsync<IOException>(() => fixture.Acquire());
        }
    }

    [Fact]
    public async Task Cancelled_or_non_git_events_never_create_pending_state()
    {
        using var fixture = new ScopeFixture();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeJoinBinding.AcquireAsync(fixture.Folder, "codex", Chat, "machine", "repo", fixture.Workspace, cancelled.Token));
        var noGit = Directory.CreateDirectory(Path.Combine(fixture.Root, "not-a-checkout")).FullName;
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.Acquire(noGit));
        Assert.False(Directory.Exists(Path.Combine(fixture.Folder, "sessions")));
    }

    [Fact]
    public async Task Invalid_unicode_identifiers_cannot_alias_through_utf8_replacement()
    {
        using var fixture = new ScopeFixture();
        var invalid = new string((char)0xD800, 1);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => NativeJoinBinding.AcquireAsync(fixture.Folder, "codex", Chat, "machine", invalid, fixture.Workspace, default));
        Assert.False(Directory.Exists(Path.Combine(fixture.Folder, "sessions")));
    }

    private static Task<string?> Line(Process process) => process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
    private static void Stop(Process process)
    {
        try { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(10000); } }
        catch (InvalidOperationException) { }
    }
    private static void MakePublic(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var info = new FileInfo(path);
            var security = info.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
    }

    internal sealed class ScopeFixture : IDisposable
    {
        internal readonly List<Process> Children = [];
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "cocern-native-binding-" + Guid.NewGuid().ToString("N"));
        public string Workspace { get; }
        public string TokenFile { get; }
        public string Folder { get; }
        internal AdapterOptions Options => new("http://127.0.0.1:5271/", "synthetic-principal");
        public ScopeFixture()
        {
            Directory.CreateDirectory(Root);
            Workspace = Directory.CreateDirectory(Path.Combine(Root, "checkout")).FullName;
            Git(Workspace, "init", "-q");
            TokenFile = Path.Combine(Root, "synthetic-token");
            NodeState.WritePrivately(TokenFile, Encoding.UTF8.GetBytes("synthetic-principal"));
            Folder = Connection("cache");
        }
        public string Connection(string name) => LocalWakeConnection.Create(new AdapterOptions("http://127.0.0.1:5271/", "synthetic-principal"), TokenFile, Path.Combine(Root, name))!.Folder;
        internal Task<NativeJoinBinding> Acquire(string? workspace = null) => NativeJoinBinding.AcquireAsync(Folder, "codex", Chat, "machine", "repo", workspace ?? Workspace, default);
        public string CreateWorktree()
        {
            var hooks = Directory.CreateDirectory(Path.Combine(Root, "empty-hooks")).FullName;
            Git(Workspace, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false", "-c", "core.hooksPath=" + hooks, "commit", "--allow-empty", "-qm", "fixture");
            var path = Path.Combine(Root, "linked-worktree");
            Git(Workspace, "-c", "core.hooksPath=" + hooks, "worktree", "add", "--detach", "-q", path);
            return path;
        }
        private static void Git(string cwd, params string[] arguments)
        {
            var start = new ProcessStartInfo(ExecutableSearch.Find("git") ?? throw new InvalidOperationException("Installed Git required"))
            { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var arg in arguments) start.ArgumentList.Add(arg);
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10000)) { process.Kill(entireProcessTree: true); throw new InvalidOperationException("Fixture Git timeout"); }
            Task.WhenAll(output, error).GetAwaiter().GetResult();
            Assert.Equal(0, process.ExitCode);
        }
        public void Dispose()
        {
            foreach (var child in Children) Stop(child);
            // Git stores loose objects read-only on Windows; clear only this generated fixture's files.
            var root = Path.GetFullPath(Root);
            if (!root.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith("cocern-native-binding-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture cleanup root");
            foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                if (!Path.GetFullPath(file).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture file escaped cleanup root");
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            }
            Directory.Delete(root, recursive: true);
        }
    }

    public sealed class ProbeFixture : IDisposable
    {
        private readonly string folder = Path.Combine(Path.GetTempPath(), "cocern-native-probe-" + Guid.NewGuid().ToString("N"));
        public ProbeFixture()
        {
            Directory.CreateDirectory(folder);
            foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory)) File.Copy(path, Path.Combine(folder, Path.GetFileName(path)));
            var source = """
                using System;
                using System.Text;
                using System.Text.Json;
                using System.Security.Cryptography;
                using System.Threading;
                using System.Threading.Tasks;
                using CoCern.Adapter.Node;
                class BindingProbe {
                    static async Task<int> Main(string[] args) {
                        using var cancel = new CancellationTokenSource();
                        if(args[2] == "cancel") cancel.CancelAfter(TimeSpan.FromMilliseconds(500));
                        Console.WriteLine("attempting");
                        try {
                            await using var lease = await NativeJoinBinding.AcquireAsync(args[0], "codex", "11111111-2222-4333-8444-555555555555", "machine", "repo", args[1], cancel.Token);
                            Console.WriteLine(JsonSerializer.Serialize(new { key=lease.BindingKey, digest=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lease.Secret))), generation=lease.Generation, request=lease.OpenRequestId, arguments=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lease.FrozenOpenArguments.GetRawText()))) }));
                            if(args[2] == "hold") await Console.In.ReadLineAsync();
                            return 0;
                        } catch(OperationCanceledException) { Console.WriteLine("cancelled"); return 0; }
                        catch { Console.WriteLine("failed"); return 2; }
                    }
                }
                """;
            var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Append(typeof(NativeJoinBinding).Assembly.Location).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(path => Path.GetFileName(path) != "CoCern.Tests.dll");
            var compilation = CSharpCompilation.Create("CoCern.Tests", [CSharpSyntaxTree.ParseText(source)],
                trusted.Select(path => MetadataReference.CreateFromFile(path)), new CSharpCompilationOptions(OutputKind.ConsoleApplication));
            var result = compilation.Emit(Path.Combine(folder, "CoCern.Tests.dll"));
            Assert.True(result.Success, string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        }
        internal Process Start(ScopeFixture fixture, string mode)
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            { WorkingDirectory = folder, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            start.ArgumentList.Add(Path.Combine(folder, "CoCern.Tests.dll"));
            start.ArgumentList.Add(fixture.Folder); start.ArgumentList.Add(fixture.Workspace); start.ArgumentList.Add(mode);
            foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("COCERN_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("RELAY__", StringComparison.OrdinalIgnoreCase) || k.StartsWith("CODEX_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("CLAUDE_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
            var child = Process.Start(start)!;
            fixture.Children.Add(child);
            _ = child.StandardError.ReadToEndAsync();
            return child;
        }
        public void Dispose() => Directory.Delete(folder, recursive: true);
    }
}
