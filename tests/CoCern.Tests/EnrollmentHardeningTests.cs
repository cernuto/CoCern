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

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-30T00:00:00Z");

    private static RelayReply Enroll(StateEnvelope<CoordinationDocument> state, string id, DateTimeOffset at) =>
        DiscoveryStateMachine.Apply(state, new("hub", true, [], true), DiscoveryOperations.Enroll, new() { ["machineId"] = id, ["machineName"] = "PC", ["credentialHash"] = "hash-" + id }, at);

    private static RelayReply Announce(StateEnvelope<CoordinationDocument> state, string id, DateTimeOffset at) =>
        DiscoveryStateMachine.Apply(state, new("hub", true, [], true), DiscoveryOperations.Announce, new() { ["machineId"] = id, ["machineName"] = "PC" }, at);

    private static JsonObject Machines(StateEnvelope<CoordinationDocument> state) => JsonNode.Parse(state.Value.Json)!["machines"]!.AsObject();

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
    [InlineData("http://0.0.0.0:5271", true)]
    [InlineData("http://127.0.0.1:5272", false)]
    [InlineData("https://0.0.0.0:5273", false)]
    public void An_http_endpoint_in_kestrel_configuration_is_checked_too(string url, bool refused)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["urls"] = "http://127.0.0.1:5271", ["Kestrel:Endpoints:Extra:Url"] = url }).Build();
        var error = Record.Exception(() => HubNetwork.RefusePlainNetworkHttp(configuration));
        Assert.Equal(refused, error is InvalidOperationException);
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

}
