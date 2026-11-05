using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using System.IO.Pipes;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.IO.Compression;
using System.Security.Cryptography;
using CoCern.Adapter;
using CoCern.Adapter.Development;
using CoCern.Adapter.Node;
using CoCern.Adapter.Setup;
using Xunit;

namespace CoCern.Tests;

public sealed class NativeJoinSetupTests : IDisposable
{
    private readonly string root = Path.Combine(Workspace(), "artifacts", "n4s", Guid.NewGuid().ToString("N")[..12]);
    private string Cache => Path.Combine(root, "cache");
    private string Token => Path.Combine(root, "token");
    private string Profile => Path.Combine(root, "profile");
    private string Adapter => Path.Combine(root, "launch path with spaces", "CoCern.Adapter.dll");
    private const string Hub = "http://127.0.0.1:4321/";

    public NativeJoinSetupTests()
    {
        Directory.CreateDirectory(root);
        NodeState.WritePrivately(Token, Encoding.UTF8.GetBytes("synthetic-setup-secret"));
    }

    [Theory]
    [InlineData("claude")]
    public void Staging_is_private_inactive_and_keeps_raw_entries_and_unknown_disabled_hooks(string provider)
    {
        var connection = Connection();
        var active = Path.Combine(root, "active-hooks.json");
        const string untouched = "{\"hooks\":{\"Stop\":[{\"matcher\":\"mine\",\"unknown\":17,\"hooks\":[{\"type\":\"command\",\"command\":\"other --stop-hook\",\"disabled\":true}]}]},\"other\":true}";
        File.WriteAllText(active, untouched);
        var command = SetupEnvironment.AdapterCommandLine("dotnet", Adapter);
        var path = ClientSettings.StageNativeCandidate(connection.Folder, provider, command);
        Assert.True(File.Exists(path));
        Assert.True(NodeState.IsPrivateFile(path));
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var candidate = document.RootElement;
        Assert.Equal(provider, candidate.GetProperty("provider").GetString());
        Assert.Equal("inactive", candidate.GetProperty("state").GetString());
        Assert.Equal(connection.Folder, candidate.GetProperty("connection").GetString());
        var hooks = candidate.GetProperty("hooks");
        Assert.Equal("^mcp__cocern__.*$", hooks.GetProperty("PreToolUse")[0].GetProperty("matcher").GetString());
        var pre = hooks.GetProperty("PreToolUse")[0].GetProperty("hooks")[0];
        Assert.Contains("--native-hook " + provider, pre.GetProperty("command").GetString());
        Assert.Contains("--connection", pre.GetProperty("command").GetString());
        Assert.False(pre.TryGetProperty("permissionDecision", out _));
        Assert.DoesNotContain("synthetic-setup-secret", File.ReadAllText(path));
        Assert.DoesNotContain("sessionToken", File.ReadAllText(path));
        Assert.Equal(untouched, File.ReadAllText(active));
        if (provider == "claude") Assert.True(hooks.GetProperty("Stop")[0].GetProperty("hooks")[0].GetProperty("asyncRewake").GetBoolean());
    }

    [Fact]
    public void Helper_reads_only_the_explicit_private_connection_and_never_discovers_another()
    {
        var connection = Connection();
        var options = NativeJoinHook.ReadConnection(connection.Folder);
        Assert.NotNull(options);
        Assert.Equal("synthetic-setup-secret", options.Token);
        Assert.Equal(Hub, options.BaseUri.AbsoluteUri);
        Assert.Null(NativeJoinHook.ReadConnection(Path.Combine(root, "missing")));
        Assert.Null(NativeJoinHook.ReadConnection("relative"));
    }

    [Fact]
    public async Task Registration_pair_authenticates_only_configured_scope_and_persists_no_receipt()
    {
        var (registration, candidate) = Registration();
        var original = File.ReadAllBytes(candidate);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accepting = registration.AcceptAsync(timeout.Token, TimeSpan.FromSeconds(2));
        Assert.True(await registration.AnswerAsync(timeout.Token, TimeSpan.FromSeconds(2)));
        var verified = await accepting;
        Assert.NotNull(verified);
        Assert.Equal("codex", verified.Provider);
        Assert.Equal(Connection().Folder, verified.Folder);
        Assert.Equal(Profile, verified.Profile);
        Assert.DoesNotContain("synthetic-setup-secret", verified.ToString());
        Assert.Equal(original, File.ReadAllBytes(candidate));
        Assert.Empty(Directory.EnumerateFiles(verified.Folder, "*.receipt*"));
        Assert.Null(await registration.AcceptAsync(timeout.Token, TimeSpan.FromMilliseconds(100)));
        Assert.False(await registration.AnswerAsync(timeout.Token, TimeSpan.FromMilliseconds(100)));
    }

    [Theory]
    [InlineData("command")]
    public void Registration_rejects_a_modified_owned_candidate_without_adopting_it(string field)
    {
        var (_, candidate) = Registration();
        var data = JsonNode.Parse(File.ReadAllBytes(candidate))!.AsObject();
        if (field == "command") data["hooks"]!["SessionStart"]![0]!["hooks"]![0]!["command"] = "unrelated command";
        else data[field] = field == "provider" ? "claude" : new string('0', 64);
        NodeState.WritePrivately(candidate, JsonSerializer.SerializeToUtf8Bytes(data));
        Assert.Null(NativeRegistration.Read(candidate, "codex", Connection().Folder, Profile));
    }

    [Fact]
    public async Task Registration_instances_use_fresh_nonces_and_refuse_cross_instance_replay()
    {
        var (registration, _) = Registration();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = registration.AcceptAsync(timeout.Token, TimeSpan.FromSeconds(2));
        var second = registration.AcceptAsync(timeout.Token, TimeSpan.FromSeconds(2));
        await using var one = new NamedPipeClientStream(".", registration.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var two = new NamedPipeClientStream(".", registration.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await one.ConnectAsync(timeout.Token); await two.ConnectAsync(timeout.Token);
        var challengeOne = await ReadFrame(one, timeout.Token); var challengeTwo = await ReadFrame(two, timeout.Token);
        Assert.NotEqual(challengeOne.GetProperty("nonce").GetString(), challengeTwo.GetProperty("nonce").GetString());
        await WriteFrame(one, challengeTwo, timeout.Token); await WriteFrame(two, challengeOne, timeout.Token);
        Assert.Null(await first); Assert.Null(await second);
    }

    [Fact]
    public async Task Wiring_backend_selects_native_discovery_only_after_a_real_fake_helper_exchange_and_keeps_call_MAC_required()
    {
        await using var relay = new WiringRelayCounter();
        var (registration, args) = WiringCandidate(relay.Origin);
        await using var child = WiringStart(args);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var helper = registration.AnswerAsync(timeout.Token);
        try
        {
            var initialized = await WiringRpc(child, 1, "initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "owned-wiring-fixture", version = "1" } }, timeout.Token);
            Assert.True(await helper);
            Assert.Contains("Your native client supplies this chat's current connection context", initialized.GetProperty("result").GetProperty("instructions").GetString());
            await child.Process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            var listed = await WiringRpc(child, 2, "tools/list", new { }, timeout.Token);
            var tools = listed.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
            Assert.Equal(new[] { "ack", "chat_directory", "check_inbox", "peers", "send_message" }, tools.Select(tool => tool.GetProperty("name").GetString()).Order());
            foreach (var tool in tools)
                foreach (var own in new[] { "sessionId", "sessionToken", "repositoryId", "requestId", "_cocernNative" })
                    Assert.False(tool.GetProperty("inputSchema").GetProperty("properties").TryGetProperty(own, out _));
            var send = tools.Single(tool => tool.GetProperty("name").GetString() == "send_message");
            Assert.True(send.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("recipientSessionId", out _));
            foreach (var arguments in new object[] { new { }, new { _cocernNative = new { mac = "forged", invocation = "forged" } } })
            {
                var result = await WiringRpc(child, 3, "tools/call", new { name = "check_inbox", arguments }, timeout.Token);
                Assert.True(result.TryGetProperty("error", out _) || result.GetProperty("result").TryGetProperty("isError", out var failed) && failed.GetBoolean());
            }
            Assert.Equal(0, relay.Count); Assert.Equal(0, relay.BusinessRequests); Assert.Equal(0, relay.HintAttempts);
        }
        finally
        {
            timeout.Cancel();
            try { await helper; } catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData("no-helper")]
    public async Task Wiring_private_backend_refuses_unverified_scope_without_raw_fallback_or_relay(string refusal)
    {
        await using var relay = new WiringRelayCounter();
        var (_, args) = WiringCandidate(relay.Origin);
        if (refusal == "wrong-profile") args[^1] = Directory.CreateDirectory(Path.Combine(root, "another-profile")).FullName;
        if (refusal == "changed-credential") NodeState.WritePrivately(Token, Encoding.UTF8.GetBytes("different-synthetic-token"));
        await using var child = WiringStart(args);
        child.Process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(refusal == "no-helper" ? 25 : 10));
        await child.Process.WaitForExitAsync(timeout.Token);
        Assert.Equal(1, child.Process.ExitCode);
        Assert.Equal("Native client registration could not be verified.", (await child.Error).Trim());
        Assert.Empty(await child.Process.StandardOutput.ReadToEndAsync(timeout.Token));
        Assert.Equal(0, relay.Count); Assert.Equal(0, relay.BusinessRequests); Assert.Equal(0, relay.HintAttempts);
        Assert.Equal("unchanged", File.ReadAllText(child.Sentinel));
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(child.Sentinel)!, "CoCern", "local-wake")));
    }

    private static string WiringOutput() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "CoCern.Adapter", new DirectoryInfo(AppContext.BaseDirectory).Name));

    private (NativeRegistration Registration, string[] Args) WiringCandidate(string origin)
    {
        Directory.CreateDirectory(Profile);
        var connection = LocalWakeConnection.Create(new AdapterOptions(origin, "synthetic-setup-secret"), Token, Cache)!;
        var candidate = ClientSettings.StageNativeCandidate(connection.Folder, "codex", "fixture-helper", clientProfile: Profile)!;
        return (NativeRegistration.Read(candidate, "codex", connection.Folder, Profile)!, ["--native-client", "codex", "--connection", connection.Folder, "--profile", Profile]);
    }

    private WiringChild WiringStart(string[] args, string? rawOrigin = null, string? adapter = null)
    {
        var (start, sentinel) = WiringStartInfo(args, rawOrigin, adapter);
        return new(Process.Start(start)!, sentinel);
    }

    private (ProcessStartInfo Start, string Sentinel) WiringStartInfo(string[] args, string? rawOrigin = null, string? adapter = null)
    {
        adapter ??= Path.Combine(WiringOutput(), "CoCern.Adapter.dll");
        Assert.True(File.Exists(adapter));
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (dotnet is null || !Path.IsPathFullyQualified(dotnet) || !File.Exists(dotnet))
            dotnet = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe") : "/usr/bin/dotnet";
        Assert.True(File.Exists(dotnet));
        var home = Directory.CreateDirectory(Path.Combine(root, "wiring-child")).FullName;
        var local = Directory.CreateDirectory(Path.Combine(home, "local")).FullName;
        var sentinel = Path.Combine(local, "sentinel"); File.WriteAllText(sentinel, "unchanged");
        var start = new ProcessStartInfo(dotnet) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(adapter); foreach (var argument in args) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        foreach (var key in new[] { "SystemRoot", "WINDIR", "COMSPEC", "PROCESSOR_ARCHITECTURE" })
            if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
        var gitFolder = OperatingSystem.IsWindows() ? "C:/Program Files/Git/cmd" : "/usr/bin";
        Assert.True(File.Exists(Path.Combine(gitFolder, OperatingSystem.IsWindows() ? "git.exe" : "git")));
        start.Environment["PATH"] = Path.GetDirectoryName(dotnet)! + Path.PathSeparator + gitFolder;
        start.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(dotnet)!;
        start.Environment["DOTNET_CLI_HOME"] = Directory.CreateDirectory(Path.Combine(home, "dotnet")).FullName;
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"; start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["USERPROFILE"] = home; start.Environment["HOME"] = home; start.Environment["LOCALAPPDATA"] = local;
        start.Environment["APPDATA"] = Directory.CreateDirectory(Path.Combine(home, "roaming")).FullName;
        start.Environment["TEMP"] = Directory.CreateDirectory(Path.Combine(home, "temp")).FullName; start.Environment["TMP"] = start.Environment["TEMP"];
        start.Environment["COCERN_URL"] = rawOrigin ?? "deliberately-invalid-origin";
        start.Environment["COCERN_TOKEN"] = "synthetic-never-used";
        return (start, sentinel);
    }

    private static async Task<JsonElement> WiringRpc(WiringChild child, int id, string method, object args, CancellationToken ct)
    {
        await child.Process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = args }));
        while (await child.Process.StandardOutput.ReadLineAsync(ct) is { } line)
        {
            using var json = JsonDocument.Parse(line);
            if (json.RootElement.TryGetProperty("id", out var returned) && returned.TryGetInt32(out var actual) && actual == id) return json.RootElement.Clone();
        }
        throw new InvalidOperationException("Own adapter ended before the requested MCP response; fixed error classes: " + WiringClassifyError(await child.Error));
    }

    private static string WiringClassifyError(string line)
    {
        // Fixed vocabulary only. Unknown text, paths, payloads, environment values and credentials never reach a report.
        foreach (var (fragment, classification) in new[]
        {
            ("COCERN_URL", "invalid-origin"), ("CoCern adapter stopped unexpectedly", "adapter-fixed-unexpected"),
            ("not recognized as an internal or external command", "cmd-command-tokenization"), ("operable program or batch file", "cmd-command-tokenization-continuation"),
            ("specified command or file was not found", "dotnet-command-file-missing"), ("does not exist", "path-does-not-exist"),
            ("hostpolicy", "hostpolicy-load"), ("hostfxr", "hostfxr-load"), ("runtimeconfig", "runtimeconfig"),
            ("dependencies manifest", "dependency-manifest"), ("JSON parsing", "host-json-parse"), ("too long", "path-too-long"),
            ("CoreCLR", "coreclr-load"), (".NET to run this application", "framework-missing"), ("error", "host-error-other")
        }) if (line.Contains(fragment, StringComparison.OrdinalIgnoreCase)) return classification;
        return "other-line";
    }

    private sealed class WiringChild(Process process, string sentinel) : IAsyncDisposable
    {
        internal Process Process { get; } = process;
        internal string Sentinel { get; } = sentinel;
        internal Task<string> Error { get; } = process.StandardError.ReadToEndAsync();
        public async ValueTask DisposeAsync()
        {
            if (!Process.HasExited) { Process.Kill(entireProcessTree: true); await Process.WaitForExitAsync(); }
            await Error; Process.Dispose();
        }
    }

    private sealed class WiringRelayCounter : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stopping = new();
        private readonly Task watching;
        private int count;
        private int hintAttempts;
        internal int Count => Volatile.Read(ref count);
        internal int HintAttempts => Volatile.Read(ref hintAttempts);
        internal int BusinessRequests => Count - HintAttempts;
        internal string Origin { get; }
        internal WiringRelayCounter()
        {
            listener.Start(); Origin = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/";
            watching = Watch();
        }
        private async Task Watch()
        {
            try
            {
                while (!stopping.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stopping.Token);
                    Interlocked.Increment(ref count); // Count every relay connection, retaining no request data or headers.
                    using var stream = client.GetStream();
                    var firstLine = new byte[4096]; var length = 0;
                    while (length < firstLine.Length)
                    {
                        var read = await stream.ReadAsync(firstLine.AsMemory(length, 1), stopping.Token);
                        if (read == 0) break;
                        if (firstLine[length++] == '\n') break;
                    }
                    var parts = Encoding.ASCII.GetString(firstLine, 0, length).Split(' ');
                    if (parts.Length >= 2 && parts[1].Split('?')[0] == "/hubs/coordination/negotiate") Interlocked.Increment(ref hintAttempts);
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync() { stopping.Cancel(); listener.Stop(); await watching; stopping.Dispose(); }
    }

    private (NativeRegistration Registration, string Candidate) Registration()
    {
        Directory.CreateDirectory(Profile);
        var connection = Connection();
        var candidate = ClientSettings.StageNativeCandidate(connection.Folder, "codex", "\"fixture launcher.exe\"", clientProfile: Profile);
        Assert.NotNull(candidate);
        var registration = NativeRegistration.Read(candidate, "codex", connection.Folder, Profile);
        Assert.NotNull(registration);
        return (registration, candidate);
    }

    private static async Task<JsonElement> ReadFrame(Stream stream, CancellationToken ct)
    {
        var prefix = new byte[4]; await stream.ReadExactlyAsync(prefix, ct);
        var count = BinaryPrimitives.ReadInt32BigEndian(prefix);
        Assert.InRange(count, 1, NativeRegistration.MaximumFrameBytes);
        var bytes = new byte[count]; await stream.ReadExactlyAsync(bytes, ct);
        using var json = JsonDocument.Parse(bytes); return json.RootElement.Clone();
    }

    private static async Task WriteFrame(Stream stream, JsonElement value, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(value.GetRawText());
        var prefix = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, ct); await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct);
    }

    private LocalWakeConnection Connection() => LocalWakeConnection.Create(new AdapterOptions(Hub, "synthetic-setup-secret"), Token, Cache)!;

    private static string Workspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "CoCern.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Native setup fixtures require their isolated workspace.");
    }

    public void Dispose()
    {
        var parent = Path.GetFullPath(Path.Combine(Workspace(), "artifacts", "n4s")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(root).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture cleanup escaped its artifact folder.");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
