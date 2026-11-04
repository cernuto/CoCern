using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using System.IO.Pipes;
using System.Buffers.Binary;
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
    [InlineData("codex")]
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
    public void Repeated_staging_is_idempotent_and_launcher_changes_refresh_only_the_owned_candidate()
    {
        var folder = Connection().Folder;
        var first = ClientSettings.StageNativeCandidate(folder, "claude", "\"old launcher.exe\"");
        Assert.True(File.Exists(first));
        var bytes = File.ReadAllBytes(first);
        var modified = File.GetLastWriteTimeUtc(first);
        Assert.Equal(first, ClientSettings.StageNativeCandidate(folder, "claude", "\"old launcher.exe\""));
        Assert.Equal(bytes, File.ReadAllBytes(first)); Assert.Equal(modified, File.GetLastWriteTimeUtc(first));
        ClientSettings.StageNativeCandidate(folder, "claude", "\"new launcher.exe\"");
        Assert.Contains("new launcher.exe", File.ReadAllText(first));
        Assert.Single(Directory.EnumerateFiles(folder, "*.candidate.json"));
    }

    [Fact]
    public void Corrupt_inactive_candidate_is_left_untouched_and_cannot_break_raw_setup()
    {
        var folder = Connection().Folder;
        var path = Path.Combine(folder, "native-client-claude.candidate.json");
        NodeState.WritePrivately(path, Encoding.UTF8.GetBytes("{corrupt"));
        Assert.Null(ClientSettings.StageNativeCandidate(folder, "claude", "adapter"));
        Assert.Equal("{corrupt", File.ReadAllText(path));
    }

    [Fact]
    public void Unverified_promotion_keeps_the_single_raw_registration_and_all_settings_bytes()
    {
        var folder = Connection().Folder;
        var path = Path.Combine(root, "raw-config.json");
        const string raw = "{\"mcpServers\":{\"cocern\":{\"command\":\"raw-adapter\"},\"other\":{\"command\":\"mine\"}},\"unknown\":true}";
        File.WriteAllText(path, raw);
        ClientSettings.StageNativeCandidate(folder, "claude", "adapter");
        var plan = ClientSettings.PlanNativePromotion(path, folder, "claude");
        Assert.True(plan.IsUnchanged);
        Assert.Equal(ClientChange.Unchanged, plan.Apply(TimeProvider.System));
        Assert.Equal(raw, File.ReadAllText(path));
        Assert.Single(JsonNode.Parse(File.ReadAllText(path))!["mcpServers"]!.AsObject(), item => item.Key == "cocern");
        Assert.Empty(Directory.EnumerateFiles(root, "*.bak*"));
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
    public void Local_connect_stages_both_candidates_without_loading_native_hooks_or_replacing_raw_launch()
    {
        Assert.Equal(0, ConnectCommand.Run(["--token-file", Token, "--hub", Hub], Profile, null, "dotnet", Adapter, TextWriter.Null, TextWriter.Null, nativeCacheRoot: Cache));
        var folder = Connection().Folder;
        Assert.Equal(2, Directory.EnumerateFiles(folder, "*.candidate.json").Count());
        var codex = File.ReadAllText(Path.Combine(Profile, ".codex", "config.toml"));
        var claude = File.ReadAllText(Path.Combine(Profile, ".claude.json"));
        Assert.DoesNotContain("--native", codex); Assert.DoesNotContain("--native", claude);
        Assert.DoesNotContain("--native", File.ReadAllText(Path.Combine(Profile, ".claude", "settings.json")));
        Assert.False(File.Exists(Path.Combine(Profile, ".codex", "hooks.json")));
    }

    [Fact]
    public void Development_launcher_swap_refreshes_candidates_without_activating_the_provider()
    {
        var config = Path.Combine(Profile, ".claude.json");
        ClientSettings.PlanClaudeCode(config, "dotnet", Adapter, new Dictionary<string, string> { ["COCERN_TOKEN_FILE"] = Token, ["COCERN_URL"] = Hub }).Apply(TimeProvider.System);
        var deployment = Path.Combine(root, "deployment");
        var launcher = Path.Combine(deployment, "launcher path", "CoCern.Adapter.dll");
        DevelopmentSetup.ConfigureClaudeCode(config, "dotnet", launcher, deployment, Token, replaceLauncher: true, nativeCacheRoot: Cache);
        var candidate = Path.Combine(Connection().Folder, "native-client-claude.candidate.json");
        Assert.True(File.Exists(candidate));
        Assert.Contains("launcher path", File.ReadAllText(candidate));
        Assert.DoesNotContain("--native", File.ReadAllText(config));
        Assert.DoesNotContain("--native", File.ReadAllText(Path.Combine(Profile, ".claude", "settings.json")));
    }

    [Fact]
    public async Task Hook_process_refuses_its_missing_private_connection_before_environment_fallback()
    {
        var adapter = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "CoCern.Adapter", "debug", "CoCern.Adapter.dll"));
        Assert.True(File.Exists(adapter));
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (dotnet is null || !Path.IsPathFullyQualified(dotnet) || !File.Exists(dotnet))
            dotnet = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe") : "/usr/bin/dotnet";
        Assert.True(File.Exists(dotnet));
        var local = Directory.CreateDirectory(Path.Combine(root, "child-local")).FullName;
        var sentinel = Path.Combine(local, "sentinel");
        File.WriteAllText(sentinel, "unchanged");
        var start = new ProcessStartInfo(dotnet) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(adapter); start.ArgumentList.Add("--native-hook"); start.ArgumentList.Add("claude"); start.ArgumentList.Add("--connection"); start.ArgumentList.Add(Path.Combine(root, "missing-connection"));
        start.Environment.Clear();
        foreach (var key in new[] { "SystemRoot", "WINDIR", "COMSPEC", "PROCESSOR_ARCHITECTURE" })
            if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
        start.Environment["PATH"] = Path.GetDirectoryName(dotnet)!;
        start.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(dotnet)!;
        start.Environment["DOTNET_CLI_HOME"] = Directory.CreateDirectory(Path.Combine(root, "child-dotnet")).FullName;
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["USERPROFILE"] = Directory.CreateDirectory(Path.Combine(root, "child-profile")).FullName;
        start.Environment["HOME"] = start.Environment["USERPROFILE"];
        start.Environment["LOCALAPPDATA"] = local;
        start.Environment["APPDATA"] = Directory.CreateDirectory(Path.Combine(root, "child-roaming")).FullName;
        start.Environment["TEMP"] = Directory.CreateDirectory(Path.Combine(root, "child-temp")).FullName;
        start.Environment["TMP"] = start.Environment["TEMP"];
        start.Environment["COCERN_URL"] = "deliberately-invalid-origin";
        start.Environment["COCERN_TOKEN"] = "synthetic-never-used";
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        Assert.Equal(1, process.ExitCode);
        Assert.Equal("Native hooks need their configured private connection.", (await error).Trim());
        Assert.Empty(await output);
        Assert.Equal("unchanged", File.ReadAllText(sentinel));
        Assert.False(Directory.Exists(Path.Combine(local, "CoCern", "local-wake")));
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

    [Fact]
    public void Registration_candidate_binds_exact_profile_provider_connection_and_definition()
    {
        var (registration, candidate) = Registration();
        Assert.Equal("codex", registration.Provider);
        Assert.Equal(Profile, registration.Profile);
        Assert.Equal(64, registration.Definition.Length);
        Assert.Null(NativeRegistration.Read(candidate, "claude", Connection().Folder, Profile));
        var otherProfile = Directory.CreateDirectory(Path.Combine(root, "different-profile")).FullName;
        Assert.Null(NativeRegistration.Read(candidate, "codex", Connection().Folder, otherProfile));
        Assert.Null(NativeRegistration.Read(candidate, "codex", root, Profile));
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("credentialScope")]
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
    public async Task Registration_without_a_trusted_helper_is_bounded_and_cannot_select_a_native_context()
    {
        var (registration, _) = Registration();
        var elapsed = Stopwatch.StartNew();
        Assert.Null(await registration.AcceptAsync(default, TimeSpan.FromMilliseconds(150)));
        Assert.InRange(elapsed.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData("oversize")]
    [InlineData("wrong-provider")]
    [InlineData("wrong-mac")]
    [InlineData("stale-nonce")]
    [InlineData("expired")]
    [InlineData("unknown-field")]
    public async Task Registration_refuses_invalid_bounded_wire_before_a_native_context_exists(string form)
    {
        var (registration, _) = Registration();
        Assert.NotEmpty(registration.PipeName);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accepting = registration.AcceptAsync(timeout.Token, TimeSpan.FromSeconds(2));
        await using var client = new NamedPipeClientStream(".", registration.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(timeout.Token);
        var challenge = await ReadFrame(client, timeout.Token);
        Assert.Equal(new[] { "deadline", "definition", "mac", "nonce", "profile", "provider", "scope", "version" }.Order(), challenge.EnumerateObject().Select(item => item.Name).Order());
        Assert.DoesNotContain("synthetic-setup-secret", challenge.GetRawText());
        Assert.DoesNotContain("session", challenge.GetRawText(), StringComparison.OrdinalIgnoreCase);
        if (form == "oversize")
        {
            var prefix = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(prefix, NativeRegistration.MaximumFrameBytes + 1);
            await client.WriteAsync(prefix, timeout.Token); await client.FlushAsync(timeout.Token);
        }
        else
        {
            var response = JsonNode.Parse(challenge.GetRawText())!.AsObject();
            if (form == "wrong-provider") response["provider"] = "claude";
            if (form == "wrong-mac") response["mac"] = new string('0', 64);
            if (form == "stale-nonce") response["nonce"] = new string('0', 64);
            if (form == "expired") response["deadline"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds();
            if (form == "unknown-field") response["tool_input"] = new JsonObject { ["untrusted"] = true };
            await WriteFrame(client, JsonSerializer.SerializeToElement(response), timeout.Token);
        }
        Assert.Null(await accepting);
    }

    [Fact]
    public async Task Registration_cancellation_keeps_the_original_token_and_no_listener()
    {
        var (registration, _) = Registration();
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var serverError = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registration.AcceptAsync(canceled.Token));
        var clientError = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registration.AnswerAsync(canceled.Token));
        Assert.Equal(canceled.Token, serverError.CancellationToken); Assert.Equal(canceled.Token, clientError.CancellationToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accepting = registration.AcceptAsync(timeout.Token, TimeSpan.FromSeconds(2));
        Assert.True(await registration.AnswerAsync(timeout.Token, TimeSpan.FromSeconds(2)));
        Assert.NotNull(await accepting);
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
    public async Task Registration_concurrent_instances_are_bounded_and_capacity_is_released()
    {
        var (registration, _) = Registration();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waiting = Enumerable.Range(0, NativeRegistration.MaximumInstances).Select(_ => registration.AcceptAsync(timeout.Token, TimeSpan.FromMilliseconds(300))).ToArray();
        var refused = registration.AcceptAsync(timeout.Token, TimeSpan.FromMilliseconds(150));
        Assert.True(refused.IsCompleted);
        Assert.Null(await refused);
        Assert.All(await Task.WhenAll(waiting), value => Assert.Null(value));
        var fresh = registration.AcceptAsync(timeout.Token, TimeSpan.FromSeconds(2));
        Assert.True(await registration.AnswerAsync(timeout.Token, TimeSpan.FromSeconds(2)));
        Assert.NotNull(await fresh);
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
