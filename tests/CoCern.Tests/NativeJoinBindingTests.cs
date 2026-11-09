using System.Diagnostics;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;
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
        // This test isolates binding state; startup status persistence has its own coverage.
        Assert.Null(LocalWakeConnection.Create(new AdapterOptions("http://127.0.0.1:5271/", "raw-synthetic"), null, folder, report: _ => { }));
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
    public async Task Private_classic_adoption_retries_keep_the_exact_handle_and_never_fall_back_after_it_disappears()
    {
        using var fixture = new ScopeFixture();
        var session = Guid.NewGuid().ToString("N");
        var location = new ChatLocation("codex", Chat);
        var classic = new SessionHandle(session, "synthetic-session-token", "repo", "Retained FC label", DateTimeOffset.UtcNow, location, location);
        NodeState.WriteSessionHandle(fixture.Folder, classic);
        await using var binding = await fixture.Acquire();
        var calls = 0;
        using var handler = new CallbackHandler(async (request, ct) =>
        {
            calls++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var proof = body.RootElement.GetProperty("nativeJoin").GetProperty("adopt");
            Assert.Equal(classic.SessionId, proof.GetProperty("sessionId").GetString());
            Assert.True(classic.SessionToken == proof.GetProperty("sessionToken").GetString());
            Assert.DoesNotContain(classic.SessionToken, binding.FrozenOpenArguments.GetRawText());
            return Reply(calls == 1 ? OpenReply(session) with { Ok = false, Code = "Unavailable" } : OpenReply(session));
        });
        using var http = new HttpClient(handler);
        var forwarder = new RelayForwarder(http, fixture.Options);
        var handoff = SessionHandoff.ForFolder(fixture.Folder, location);
        Assert.False((await handoff.OpenNativeJoinAsync(binding, forwarder, default)).Completed);
        Assert.Null(binding.CurrentHandle);
        var intentPath = Path.ChangeExtension(binding.StatePath, ".adopt");
        Assert.True(NodeState.IsPrivateFile(intentPath));
        Assert.DoesNotContain(classic.SessionToken, File.ReadAllText(intentPath));
        File.Delete(NodeState.SessionHandlePath(fixture.Folder, session));
        Assert.False((await handoff.OpenNativeJoinAsync(binding, forwarder, default)).Completed);
        Assert.Equal(1, calls); // The durable private intent forbids falling through to a new allocating request.
        NodeState.WriteSessionHandle(fixture.Folder, classic);
        Assert.True((await handoff.OpenNativeJoinAsync(binding, forwarder, default, "Generic native title")).Completed);
        Assert.Equal(2, calls);
        var adopted = binding.CurrentHandle!;
        Assert.Equal(classic.SessionId, adopted.SessionId);
        Assert.Equal(classic.SessionToken, adopted.SessionToken);
        Assert.Equal(classic.Label, adopted.Label);
        Assert.Equal(classic.OpenedAt, adopted.OpenedAt);
        Assert.Equal(binding.OwnershipKey, adopted.NativeBindingKey);
        Assert.DoesNotContain(classic.SessionToken, File.ReadAllText(binding.StatePath));
        Assert.False(File.Exists(intentPath));
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
            var openApi = typeof(SessionHandoff).GetMethod("OpenNativeJoinAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.DoesNotContain(openApi.GetParameters(), parameter => parameter.ParameterType == typeof(AdapterReply) || parameter.ParameterType == typeof(JsonElement));
            Assert.False(await Open(handoff, binding, fixture.Options, reply with { Ok = false }));
            Assert.False(await Open(handoff, binding, new AdapterOptions(fixture.Options.BaseUri.AbsoluteUri, "stale-synthetic-principal"), reply));
            Assert.False(await Open(SessionHandoff.ForFolder(fixture.Folder, new("claude", Chat)), binding, fixture.Options, reply));
            Assert.Null(binding.CurrentHandle);
            Assert.True(await Open(handoff, binding, fixture.Options, reply));
            Assert.True(await Open(handoff, binding, fixture.Options, reply));
            Assert.Equal(session, binding.CurrentHandleReference);
            Assert.Equal(session, binding.CurrentHandle!.SessionId);
            Assert.Equal(binding.OwnershipKey, binding.CurrentHandle.NativeBindingKey);
            Assert.True(NodeState.IsPrivateFile(NodeState.SessionHandlePath(fixture.Folder, session)));
            Assert.False(File.ReadAllText(statePath).Contains("synthetic-session-token", StringComparison.Ordinal));
            Assert.False(JsonSerializer.Serialize(binding.CurrentHandle).Contains("nativeBindingKey", StringComparison.OrdinalIgnoreCase));
            // A returned session whose handle another binding owns is still a conflict and leaves the reference alone.
            var foreign = Guid.NewGuid().ToString("N");
            NodeState.WriteSessionHandle(fixture.Folder, binding.CurrentHandle! with { SessionId = foreign, NativeBindingKey = new string('0', 64) });
            Assert.False(await Open(handoff, binding, fixture.Options, OpenReply(foreign)));
            Assert.Equal(session, binding.CurrentHandleReference);
            File.Delete(NodeState.SessionHandlePath(fixture.Folder, foreign));
            // The hub lost its replay record (a coordination reset): the same frozen open now returns a new session. The binding moves and the dead handle retires.
            var arguments = binding.FrozenOpenArguments.GetRawText();
            var replacement = Guid.NewGuid().ToString("N");
            Assert.True(await Open(handoff, binding, fixture.Options, OpenReply(replacement)));
            Assert.Equal(replacement, binding.CurrentHandle!.SessionId);
            Assert.Equal(binding.OwnershipKey, binding.CurrentHandle.NativeBindingKey);
            Assert.True(arguments == binding.FrozenOpenArguments.GetRawText(), "A superseded session changed the frozen opening attempt.");
            Assert.False(File.Exists(NodeState.SessionHandlePath(fixture.Folder, session)));
            session = replacement;
        }
        await using (var reopened = await fixture.Acquire())
        {
            Assert.Equal(session, reopened.CurrentHandle!.SessionId);
            Assert.True(reopened.CurrentHandle.SessionToken == "synthetic-session-token");
            Assert.Single(Directory.EnumerateFiles(Path.Combine(fixture.Folder, "sessions"), "*.json"));
        }
        // Regression: the same chat later joined from a linked worktree. One binding per checkout used to drop the whole thread from dispatch;
        // now the one confirmed last is dispatched and the older checkout's binding stays on disk unchanged.
        var main = Assert.Single(NativeJoinBinding.DispatchSnapshot(fixture.Folder));
        var mainState = File.ReadAllBytes(statePath);
        var linked = fixture.CreateWorktree();
        var moved = Guid.NewGuid().ToString("N");
        var later = SessionHandoff.ForFolder(fixture.Folder, new("codex", Chat));
        await using (var worktree = await fixture.Acquire(linked)) Assert.True(await Open(later, worktree, fixture.Options, OpenReply(moved)));
        var latest = Assert.Single(NativeJoinBinding.DispatchSnapshot(fixture.Folder));
        Assert.Equal(moved, latest.SessionId);
        Assert.True(mainState.SequenceEqual(File.ReadAllBytes(statePath)));
        // The hub answered SessionUnknown for that session: it is no longer dispatched (nor polled after a restart), and the chat's other binding is.
        NativeJoinBinding.MarkSessionUnknown(fixture.Folder, latest);
        Assert.Equal(main, Assert.Single(NativeJoinBinding.DispatchSnapshot(fixture.Folder)));
        // The chat's own authenticated open of that session clears the mark and makes it the latest again.
        await using (var worktree = await fixture.Acquire(linked)) Assert.True(await Open(later, worktree, fixture.Options, OpenReply(moved)));
        Assert.Equal(latest, Assert.Single(NativeJoinBinding.DispatchSnapshot(fixture.Folder)));
    }

    [Fact]
    public async Task Foreign_or_missing_handle_references_never_reopen_a_pending_attempt()
    {
        using var fixture = new ScopeFixture();
        string statePath, handlePath;
        await using (var binding = await fixture.Acquire())
        {
            var session = Guid.NewGuid().ToString("N");
            Assert.True(await Open(SessionHandoff.ForFolder(fixture.Folder, new("codex", Chat)), binding, fixture.Options, OpenReply(session)));
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

    [Theory]
    [InlineData("unknown", Chat, "machine", "repo")]
    [InlineData("codex", "../../other", "machine", "repo")]
    public async Task Unsafe_native_scope_inputs_never_create_binding_state(string provider, string id, string machine, string repo)
    {
        using var fixture = new ScopeFixture();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => NativeJoinBinding.AcquireAsync(fixture.Folder, provider, id, machine, repo, fixture.Workspace, default));
        Assert.False(Directory.Exists(Path.Combine(fixture.Folder, "sessions")));
    }

    private static AdapterReply OpenReply(string session) => new(true, "Ok", JsonSerializer.SerializeToElement(new { sessionId = session, sessionToken = "synthetic-session-token" }), 1);

    [Fact]
    public async Task Native_handles_require_the_checkout_binding_and_cannot_be_borrowed_by_legacy_lookup()
    {
        using var fixture = new ScopeFixture();
        await using var binding = await fixture.Acquire();
        var handoff = SessionHandoff.ForFolder(fixture.Folder, new("codex", Chat));
        var nativeSession = Guid.NewGuid().ToString("N");
        Assert.True(await Open(handoff, binding, fixture.Options, OpenReply(nativeSession)));
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
    public async Task Review1_an_independently_received_reply_cannot_complete_another_binding()
    {
        using var fixture = new ScopeFixture();
        await using var a = await fixture.Acquire();
        await using var b = await NativeJoinBinding.AcquireAsync(fixture.Folder, "codex", "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee", "machine", "repo", fixture.Workspace, default);
        var bSession = Guid.NewGuid().ToString("N");
        Assert.False(File.Exists(NodeState.SessionHandlePath(fixture.Folder, bSession)));
        var bReply = OpenReply(bSession);
        // The former internal callback reproduced the swapped-reply bug in RED. It must now be private,
        // and no open entry point accepts a caller-supplied response or caller-supplied open arguments.
        var callback = typeof(SessionHandoff).GetMethod("CompleteNativeJoin", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True(callback.IsPrivate);
        using var bHandler = new CallbackHandler((_, _) => Task.FromResult(Reply(bReply)));
        using var bClient = new HttpClient(bHandler);
        var independentlyReceived = await new RelayForwarder(bClient, fixture.Options).ForwardAsync("session_open", b.FrozenOpenArguments);
        Assert.True(independentlyReceived.Ok);
        Assert.False(File.Exists(NodeState.SessionHandlePath(fixture.Folder, bSession)));
        var aSession = Guid.NewGuid().ToString("N");
        Assert.True(await Open(SessionHandoff.ForFolder(fixture.Folder, new("codex", Chat)), a, fixture.Options, OpenReply(aSession)));
        Assert.Equal(aSession, a.CurrentHandle!.SessionId);
        Assert.False(a.CurrentHandle.SessionId == bSession);
        Assert.False(File.Exists(NodeState.SessionHandlePath(fixture.Folder, bSession)));
    }

    [Theory]
    [InlineData(true)]
    public async Task Review1_legacy_consumers_never_send_a_marked_native_token_after_reenrollment(bool includeLegacy)
    {
        using var fixture = new ScopeFixture();
        var folder = CreateNode(fixture, "http://127.0.0.1:5271/", new string('a', 64));
        var oldOptions = new AdapterOptions("http://127.0.0.1:5271/", new string('a', 64));
        await using (var binding = await NativeJoinBinding.AcquireAsync(folder, "claude", Chat, "machine", "repo", fixture.Workspace, default))
            Assert.True(await Open(SessionHandoff.ForFolder(folder, new("claude", Chat)), binding, oldOptions, OpenReply("native-from-a")));
        CreateNode(fixture, "http://127.0.0.1:5275/", new string('b', 64));
        if (includeLegacy)
            NodeState.WriteSessionHandle(folder, new("legacy-on-b", "legacy-b-token", "repo", "legacy", DateTimeOffset.UtcNow.AddDays(-1), new("claude", Chat), new("claude", Chat)));
        var seen = new List<string>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var waiter = new CallbackHandler(async (request, ct) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            seen.Add(body.RootElement.GetProperty("sessionToken").GetString()!);
            stop.Cancel();
            throw new OperationCanceledException(stop.Token);
        });
        _ = await Record.ExceptionAsync(() => ClaudeWaiter.RunAsync(folder, Chat, _ => { }, TimeSpan.FromSeconds(2), stop.Token, waiter, null, acknowledge: ["fixture-message"]));
        using var prompt = new CallbackHandler(async (request, ct) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            seen.Add(body.RootElement.GetProperty("sessionToken").GetString()!);
            return WireReply(JsonSerializer.SerializeToElement(new { attention = new { actionable = 0, urgent = 0, inbox = 0, standing = 0 } }));
        });
        _ = await PromptHook.LineAsync(JsonSerializer.Serialize(new { session_id = Chat }), folder, TimeSpan.FromSeconds(2), prompt);
        // Assert the redacted classification, so failed assertions cannot print token values.
        var borrowedNativeToken = seen.Contains("synthetic-session-token", StringComparer.Ordinal);
        Assert.False(borrowedNativeToken, "A native token reached an unscoped legacy consumer.");
        Assert.True(includeLegacy ? seen.Count == 2 && seen.All(token => token == "legacy-b-token") : seen.Count == 0);
        Assert.True(NodeState.ReadSessionHandles(folder).All(handle => handle.NativeBindingKey is null));
    }

    [Theory]
    [InlineData("GIT_DIR")]
    public async Task Review1_inherited_git_selectors_cannot_redirect_an_event_checkout(string selector)
    {
        using var a = new ScopeFixture();
        using var b = new ScopeFixture();
        string key;
        await using (var expected = await b.Acquire()) key = expected.BindingKey;
        var overrides = new Dictionary<string, string> { [selector] = selector == "GIT_DIR" ? Path.Combine(a.Workspace, ".git") : a.Workspace };
        using var process = probe.Start(b, "once", overrides);
        Assert.Equal("attempting", await Line(process));
        using var output = JsonDocument.Parse((await Line(process))!);
        Assert.Equal(key, output.RootElement.GetProperty("key").GetString());
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, process.ExitCode);
    }

    private static string CreateNode(ScopeFixture fixture, string hub, string credential)
    {
        var folder = Directory.CreateDirectory(Path.Combine(fixture.Root, "legacy-consumer-node")).FullName;
        NodeState.WritePrivately(Path.Combine(folder, "credential"), Encoding.UTF8.GetBytes(credential));
        NodeState.WritePrivately(Path.Combine(folder, "node.json"), JsonSerializer.SerializeToUtf8Bytes(new { machineId = "machine", hubUrl = hub, enrolled = true }));
        return folder;
    }
    private static HttpResponseMessage WireReply(JsonElement data) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { ok = true, code = "Ok", dataJson = data.GetRawText(), version = 1 })) };
    private static HttpResponseMessage Reply(AdapterReply reply) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { ok = reply.Ok, code = reply.Code, dataJson = reply.Data?.GetRawText() ?? "{}", version = reply.Version })) };
    private static async Task<bool> Open(SessionHandoff handoff, NativeJoinBinding binding, AdapterOptions options, AdapterReply reply)
    {
        var calls = 0;
        using var handler = new CallbackHandler(async (request, ct) =>
        {
            calls++;
            Assert.Equal(new Uri(options.BaseUri, "api/tools/session_open"), request.RequestUri);
            Assert.True(request.Headers.Authorization?.Parameter == options.Token);
            Assert.True(await request.Content!.ReadAsStringAsync(ct) == binding.FrozenOpenArguments.GetRawText(), "The transport did not send the exact frozen opening attempt.");
            return Reply(reply);
        });
        using var client = new HttpClient(handler);
        var result = await handoff.OpenNativeJoinAsync(binding, new RelayForwarder(client, options), default);
        Assert.InRange(calls, 0, 1);
        if (result.Completed) Assert.Equal(1, calls);
        return result.Completed;
    }
    private sealed class CallbackHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => callback(request, ct);
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
            foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(name);
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
                using CoCern.Adapter;
                using CoCern.Adapter.Node;
                class BindingProbe {
                    static async Task<int> Main(string[] args) {
                        using var cancel = new CancellationTokenSource();
                        if(args[2] == "cancel") cancel.CancelAfter(TimeSpan.FromMilliseconds(500));
                        Console.WriteLine("attempting");
                        try {
                            var raw = args[2] == "discovery" ? CoordinationTools.GitTopLevel(args[1]) : null;
                            await using var lease = await NativeJoinBinding.AcquireAsync(args[0], "codex", "11111111-2222-4333-8444-555555555555", "machine", "repo", args[1], cancel.Token);
                            Console.WriteLine(JsonSerializer.Serialize(new { raw, key=lease.BindingKey, digest=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lease.Secret))), generation=lease.Generation, request=lease.OpenRequestId, arguments=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lease.FrozenOpenArguments.GetRawText()))) }));
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
        internal Process Start(ScopeFixture fixture, string mode, IReadOnlyDictionary<string, string>? environment = null, string? eventDirectory = null)
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            { WorkingDirectory = folder, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            start.ArgumentList.Add(Path.Combine(folder, "CoCern.Tests.dll"));
            start.ArgumentList.Add(fixture.Folder); start.ArgumentList.Add(eventDirectory ?? fixture.Workspace); start.ArgumentList.Add(mode);
            foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("COCERN_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("RELAY__", StringComparison.OrdinalIgnoreCase) || k.StartsWith("CODEX_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("CLAUDE_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
            if (environment is not null) foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
            var child = Process.Start(start)!;
            fixture.Children.Add(child);
            _ = child.StandardError.ReadToEndAsync();
            return child;
        }
        public void Dispose() => Directory.Delete(folder, recursive: true);
    }
}
