using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using CoCern.Adapter;
using CoCern.Adapter.Node;
using CoCern.Adapter.Setup;
using CoCern.Contracts;
using CoCern.Domain;
using CoCern.Host;
using CoCern.Storage;
using Xunit;

namespace CoCern.Tests;

/// <summary>Join code, machine expiry, expected fingerprint, plain-HTTP refusal, local-only API reference and owner-only database.</summary>
public sealed class EnrollmentHardeningTests : IDisposable
{
    private const string Fingerprint = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private static readonly Uri InMemoryHub = new("https://localhost/");
    private readonly string root = Path.Combine(Path.GetTempPath(), "cocern-hardening-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private static RelayHostFixture Hub(IPAddress? remote = null) => new(remoteAddress: remote, settings: new Dictionary<string, string?>
    {
        ["Relay:Discovery:Enabled"] = "true",
        ["Relay:Discovery:Repositories:0"] = "repo"
    });

    private static HttpClient Client(RelayHostFixture host, string? token = null)
    {
        var client = host.CreateClient(new() { BaseAddress = InMemoryHub, AllowAutoRedirect = false });
        if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> EnrollAsync(HttpClient client, string machineId, string? joinCode)
    {
        using var response = await client.PostAsJsonAsync("/api/discovery/enroll", joinCode is null ? new { machineId, machineName = "PC" } : (object)new { machineId, machineName = "PC", joinCode });
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong-join-code")]
    [InlineData(RelayHostFixture.JoinCode + "x")]
    public async Task Enroll_without_the_join_code_is_refused(string? joinCode)
    {
        await using var host = Hub();
        using var client = Client(host);
        var (status, body) = await EnrollAsync(client, "pc-a", joinCode);
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal(RelayErrorCodes.JoinCodeRequired, body.GetProperty("code").GetString());
        Assert.DoesNotContain("credential", body.GetRawText());
        Assert.Equal(HttpStatusCode.OK, (await EnrollAsync(client, "pc-a", RelayHostFixture.JoinCode)).Status);
    }

    [Fact]
    public async Task Wrong_join_codes_spend_the_enroll_rate_limit()
    {
        await using var host = Hub();
        using var client = Client(host);
        for (var i = 0; i < 10; i++) Assert.Equal(HttpStatusCode.Forbidden, (await EnrollAsync(client, "pc-" + i, "guess-" + i)).Status);
        var limited = await EnrollAsync(client, "pc-x", RelayHostFixture.JoinCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.Status);
    }

    [Fact]
    public async Task Enrolled_machine_announces_without_the_join_code()
    {
        await using var host = Hub();
        using var anonymous = Client(host);
        var (status, body) = await EnrollAsync(anonymous, "pc-a", RelayHostFixture.JoinCode);
        Assert.Equal(HttpStatusCode.OK, status);
        using var machine = Client(host, body.GetProperty("credential").GetString());
        using var announced = await machine.PostAsJsonAsync("/api/discovery/announce", new { machineName = "PC", clients = new[] { "codex" }, chats = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.OK, announced.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await machine.GetAsync("/api/tools")).StatusCode);
    }

    [Fact]
    public void Generated_join_code_is_kept_owner_only_next_to_the_database_and_reused()
    {
        var environment = new HostingEnvironment { ContentRootPath = root };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Relay:DatabasePath"] = Path.Combine("store", "relay.db") }).Build();
        var first = HubJoinCode.Load(configuration, environment);
        var path = Path.Combine(root, "store", HubJoinCode.FileName);
        Assert.Equal(path, first.Source);
        Assert.Matches("^[A-Z0-9]{5}(-[A-Z0-9]{5}){3}$", first.Value);
        Assert.Equal(first.Value, File.ReadAllText(path));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        var again = HubJoinCode.Load(configuration, environment);
        Assert.Equal(first.Value, again.Value);
        // Only the start that creates the code logs its value.
        Assert.Equal((true, false), (first.Generated, again.Generated));
        Assert.True(first.Matches(first.Value));
        // People retype it, so letter case and surrounding space never matter; any other change does.
        Assert.True(first.Matches(" " + first.Value.ToLowerInvariant() + " "));
        Assert.False(first.Matches(first.Value.Replace("-", "")));
        Assert.False(first.Matches(null));
    }

    [Fact]
    public void Configured_join_code_wins_and_a_short_one_stops_startup()
    {
        var environment = new HostingEnvironment { ContentRootPath = root };
        IConfiguration With(string code) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Relay:Discovery:JoinCode"] = code }).Build();
        Assert.Equal("configured-code", HubJoinCode.Load(With(" configured-code "), environment).Value);
        Assert.False(File.Exists(Path.Combine(root, "data", HubJoinCode.FileName)));
        Assert.Throws<InvalidOperationException>(() => HubJoinCode.Load(With("short"), environment));
    }

    [Fact]
    public async Task Hub_exposes_the_configured_join_code_as_a_service()
    {
        await using var host = Hub();
        using var _ = Client(host);
        Assert.Equal(RelayHostFixture.JoinCode, host.Services.GetRequiredService<HubJoinCode>().Value);
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-30T00:00:00Z");

    private static RelayReply Enroll(StateEnvelope<CoordinationDocument> state, string id, DateTimeOffset at) =>
        DiscoveryStateMachine.Apply(state, new("hub", true, [], true), DiscoveryOperations.Enroll, new() { ["machineId"] = id, ["machineName"] = "PC", ["credentialHash"] = "hash-" + id }, at);

    private static RelayReply Announce(StateEnvelope<CoordinationDocument> state, string id, DateTimeOffset at) =>
        DiscoveryStateMachine.Apply(state, new("hub", true, [], true), DiscoveryOperations.Announce, new() { ["machineId"] = id, ["machineName"] = "PC" }, at);

    private static JsonObject Machines(StateEnvelope<CoordinationDocument> state) => JsonNode.Parse(state.Value.Json)!["machines"]!.AsObject();

    [Fact]
    public void A_full_machine_table_drops_the_machine_unseen_longest_past_the_expiry()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        var old = Now - TimeSpan.FromDays(RelayLimits.MachineExpiryDays + 5);
        for (var i = 0; i < DiscoveryLimits.MaxMachines; i++) Assert.True(Enroll(state, "m" + i, old).Ok);
        // All but two announced recently; m0 announced 31 days ago, m1 never announced since enrolling 35 days ago, so m1 is unseen longest.
        for (var i = 2; i < DiscoveryLimits.MaxMachines; i++) Assert.True(Announce(state, "m" + i, Now - TimeSpan.FromDays(1)).Ok);
        Assert.True(Announce(state, "m0", Now - TimeSpan.FromDays(RelayLimits.MachineExpiryDays + 1)).Ok);

        Assert.True(Enroll(state, "new-1", Now).Ok);
        Assert.False(Machines(state).ContainsKey("m1"));
        Assert.True(Enroll(state, "new-2", Now).Ok);
        Assert.False(Machines(state).ContainsKey("m0"));
        Assert.Equal(DiscoveryLimits.MaxMachines, Machines(state).Count);
        Assert.Equal(RelayErrorCodes.CapacityExceeded, Enroll(state, "new-3", Now).Code);
        Assert.True(Machines(state).ContainsKey("m2"));
        // A dropped ID is never issued again: its principal still owns actions and sessions.
        Assert.Equal(RelayErrorCodes.MachineExpired, Enroll(state, "m1", Now).Code);
        Assert.Equal(RelayErrorCodes.MachineExpired, Enroll(state, "m0", Now.AddDays(400)).Code);
    }

    [Fact]
    public void A_full_table_of_recently_seen_machines_refuses_new_ones()
    {
        var state = new StateEnvelope<CoordinationDocument>();
        for (var i = 0; i < DiscoveryLimits.MaxMachines; i++) Assert.True(Enroll(state, "m" + i, Now - TimeSpan.FromDays(RelayLimits.MachineExpiryDays - 1)).Ok);
        Assert.Equal(RelayErrorCodes.CapacityExceeded, Enroll(state, "new", Now).Code);
        Assert.Equal(DiscoveryLimits.MaxMachines, Machines(state).Count);
    }

    [Theory]
    [InlineData("http://0.0.0.0:5271")]
    [InlineData("http://+:5271")]
    [InlineData("http://*:5271")]
    [InlineData("http://[::]:5271")]
    [InlineData("http://192.168.1.5:5271")]
    [InlineData("http://hub-pc:5271")]
    [InlineData("http://127.0.0.1:5271;http://0.0.0.0:5272")]
    public void Plain_http_beyond_loopback_stops_startup(string urls)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["urls"] = urls }).Build();
        var error = Assert.Throws<InvalidOperationException>(() => HubNetwork.RefusePlainNetworkHttp(configuration));
        Assert.Contains("clear text", error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://127.0.0.1:5271")]
    [InlineData("http://localhost:5271")]
    [InlineData("http://[::1]:5271")]
    [InlineData("https://0.0.0.0:5273;http://127.0.0.1:5271")]
    public void Loopback_http_and_any_https_are_allowed(string? urls)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["urls"] = urls }).Build();
        HubNetwork.RefusePlainNetworkHttp(configuration);
    }

    [Theory]
    [InlineData("http://0.0.0.0:5271", true)]
    [InlineData("http://127.0.0.1:5272", false)]
    [InlineData("https://0.0.0.0:5273", false)]
    public void An_http_endpoint_in_kestrel_configuration_is_checked_too(string url, bool refused)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["urls"] = "http://127.0.0.1:5271", ["Kestrel:Endpoints:Extra:Url"] = url }).Build();
        var error = Record.Exception(() => HubNetwork.RefusePlainNetworkHttp(configuration));
        Assert.Equal(refused, error is InvalidOperationException);
    }

    [Theory]
    [InlineData("/openapi/v1.json")]
    [InlineData("/scalar")]
    public async Task Api_reference_is_served_to_this_pc_only(string path)
    {
        await using (var remote = new RelayHostFixture(remoteAddress: IPAddress.Parse("203.0.113.9")))
        {
            using var client = remote.CreateClient();
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        }
        await using var local = new RelayHostFixture(remoteAddress: IPAddress.Loopback);
        using var loopback = local.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await loopback.GetAsync(path)).StatusCode);
    }

    [Fact]
    public void Database_and_its_wal_are_owner_only_on_unix()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(root, "relay.db");
        Directory.CreateDirectory(root);
        // An older hub's -wal with default permissions is tightened too.
        File.WriteAllBytes(path + "-wal", []);
        File.SetUnixFileMode(path + "-wal", (UnixFileMode)0b110_100_100);
        using (new SqliteStore(path))
        {
            foreach (var file in new[] { path, path + "-wal", path + "-shm" }.Where(File.Exists))
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        }
        File.SetUnixFileMode(path, (UnixFileMode)0b110_100_100);
        using (new SqliteStore(path)) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void Setup_parses_join_code_and_fingerprint()
    {
        var options = SetupOptions.Parse(["--hub", "https://hub-pc:5273/", "--join-code", "ABCDE-12345", "--fingerprint", string.Join(':', Fingerprint.ToLowerInvariant().Chunk(2).Select(x => new string(x)))]);
        Assert.Equal("ABCDE-12345", options.JoinCode);
        Assert.Equal(Fingerprint, options.Fingerprint);
        Assert.Equal("from-env", SetupOptions.Parse(["--hub", "https://hub-pc:5273/"], "from-env").JoinCode);
        Assert.Equal("typed", SetupOptions.Parse(["--hub", "https://hub-pc:5273/", "--join-code", "typed"], "from-env").JoinCode);
        Assert.Null(SetupOptions.Parse(["--hub", "https://hub-pc:5273/"]).JoinCode);
        Assert.Throws<AdapterConfigurationException>(() => SetupOptions.Parse(["--hub", "https://hub-pc:5273/", "--fingerprint", "ABC"]));
        Assert.Throws<AdapterConfigurationException>(() => SetupOptions.Parse(["--hub", "http://127.0.0.1:5271/", "--fingerprint", Fingerprint]));
    }

    private NodeTransport Transport(RelayHostFixture? host, string presented, string? expected, string? joinCode, Action? handlerCreated = null) => new()
    {
        ProbeFingerprint = (_, _) => Task.FromResult(presented),
        CreateHandler = (_, _) => { handlerCreated?.Invoke(); return host!.Server.CreateHandler(); },
        HubHandler = () => host!.Server.CreateHandler(),
        ExpectedFingerprint = expected,
        JoinCode = joinCode,
        MachineName = "Node PC",
        Chats = _ => Task.FromResult(new JsonArray()),
        FirstRetry = TimeSpan.FromMilliseconds(10),
        MaxRetry = TimeSpan.FromMilliseconds(20),
        CodexExecutable = () => null,
        SingleCycle = true
    };

    [Fact]
    public async Task Node_refuses_a_certificate_other_than_the_expected_fingerprint_and_pins_nothing()
    {
        using var state = NodeState.Open(Path.Combine(root, "node"));
        var handlers = 0;
        using var error = new StringWriter();
        var code = await NodeRunner.RunAsync(state, InMemoryHub, Transport(null, Fingerprint, new string('A', 64), RelayHostFixture.JoinCode, () => handlers++), error, CancellationToken.None);
        Assert.Equal(NodeRunner.MismatchExitCode, code);
        Assert.Null(state.Fingerprint);
        Assert.False(state.Enrolled);
        Assert.Equal(0, handlers);
        Assert.Contains("--fingerprint", error.ToString());
    }

    [Fact]
    public async Task Node_pins_the_expected_fingerprint_and_enrolls_with_the_join_code()
    {
        await using var host = Hub();
        using var state = NodeState.Open(Path.Combine(root, "node"));
        using var error = new StringWriter();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Assert.Equal(0, await NodeRunner.RunAsync(state, InMemoryHub, Transport(host, Fingerprint, Fingerprint.ToLowerInvariant(), RelayHostFixture.JoinCode), error, timeout.Token));
        Assert.Equal(Fingerprint, state.Fingerprint);
        Assert.True(state.Enrolled);
    }

    [Fact]
    public async Task Node_without_the_join_code_stops_and_says_how_to_fix_it()
    {
        await using var host = Hub();
        using var state = NodeState.Open(Path.Combine(root, "node"));
        using var error = new StringWriter();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Assert.Equal(1, await NodeRunner.RunAsync(state, InMemoryHub, Transport(host, Fingerprint, null, null), error, timeout.Token));
        Assert.False(state.Enrolled);
        Assert.Contains("--join-code", error.ToString());
    }
}
