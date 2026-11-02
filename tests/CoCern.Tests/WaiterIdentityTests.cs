using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Adapter;
using Xunit;

namespace CoCern.Tests;

[CollectionDefinition("Waiter environment", DisableParallelization = true)]
public sealed class WaiterEnvironmentCollection;

[Collection("Waiter environment")]
public sealed class WaiterIdentityTests
{
    private const string Fingerprint = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Fact]
    public void Manual_waiter_uses_the_environment_pin_and_rejects_another_certificate()
    {
        using var fixture = new ConfigurationFixture();
        using var environment = new FingerprintEnvironment(Fingerprint.ToLowerInvariant());
        var options = WakeConfiguration.LoadForAwait(fixture.Write("https://127.0.0.1:5272")).AdapterOptions();
        Assert.Equal(Fingerprint, options.PinnedFingerprint);
        using var handler = RelayForwarder.CreateHandler(options);
        using var key = ECDsa.Create();
        using var certificate = new CertificateRequest("CN=other-hub", key, HashAlgorithmName.SHA256).CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        Assert.NotNull(handler.ServerCertificateCustomValidationCallback);
        Assert.False(handler.ServerCertificateCustomValidationCallback!(new HttpRequestMessage(), certificate, null, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void Manual_waiter_uses_a_configured_pin_before_the_environment_pin()
    {
        using var fixture = new ConfigurationFixture();
        using var environment = new FingerprintEnvironment("bad-environment-pin");
        var options = WakeConfiguration.LoadForAwait(fixture.Write("https://127.0.0.1:5272", Fingerprint.ToLowerInvariant())).AdapterOptions();
        Assert.Equal(Fingerprint, options.PinnedFingerprint);
    }

    [Theory]
    [InlineData("https://127.0.0.1:5272", "not-a-fingerprint")]
    [InlineData("http://127.0.0.1:5271", Fingerprint)]
    public void Manual_waiter_rejects_invalid_or_plaintext_pins(string url, string fingerprint)
    {
        using var fixture = new ConfigurationFixture();
        using var environment = new FingerprintEnvironment(fingerprint);
        Assert.Throws<AdapterConfigurationException>(() => WakeConfiguration.LoadForAwait(fixture.Write(url)));
    }

    [Fact]
    public void Manual_waiter_preserves_unpinned_loopback_http()
    {
        using var fixture = new ConfigurationFixture();
        using var environment = new FingerprintEnvironment(null);
        var options = WakeConfiguration.LoadForAwait(fixture.Write("http://127.0.0.1:5271")).AdapterOptions();
        Assert.Null(options.PinnedFingerprint);
        using var handler = RelayForwarder.CreateHandler(options);
        Assert.Null(handler.ServerCertificateCustomValidationCallback);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "explicit-client-pc")]
    [InlineData(true, "")]
    [InlineData(true, null)]
    public async Task Session_open_defaults_to_the_adapter_machine_and_preserves_explicit_identity(bool explicitIdentity, string? suppliedMachine)
    {
        JsonElement sent = default;
        using var handler = new CaptureHandler(async request => sent = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync()));
        using var client = new HttpClient(handler);
        var arguments = new JsonObject { ["repositoryId"] = "repo", ["requestId"] = "open-1", ["label"] = "Remote-looking label" };
        if (explicitIdentity) arguments["machineId"] = suppliedMachine;
        var prepared = CoordinationTools.OwnRequestId(JsonSerializer.SerializeToElement(arguments));
        var reply = await new RelayForwarder(client, new AdapterOptions("https://remote-hub.example", "client-secret")).ForwardAsync("session_open", prepared);
        Assert.True(reply.Ok);
        Assert.True(sent.TryGetProperty("machineId", out var machine), "The adapter must identify its own client machine when session_open omits machineId.");
        Assert.Equal(explicitIdentity ? suppliedMachine : Environment.MachineName, machine.GetString());
        Assert.Equal(explicitIdentity, arguments.ContainsKey("machineId"));
        Assert.Equal(prepared.GetRawText(), CoordinationTools.OwnRequestId(JsonSerializer.SerializeToElement(arguments)).GetRawText());
    }

    [Fact]
    public async Task Raw_session_open_keeps_omitted_identity_for_existing_request_replays()
    {
        string? sent = null;
        using var handler = new CaptureHandler(async request => sent = await request.Content!.ReadAsStringAsync());
        using var client = new HttpClient(handler);
        var arguments = JsonSerializer.Deserialize<JsonElement>("{\"repositoryId\":\"repo\",\"requestId\":\"pre-upgrade-open\"}");
        Assert.True((await new RelayForwarder(client, new AdapterOptions("https://remote-hub.example", "client-secret")).ForwardAsync("session_open", arguments)).Ok);
        Assert.Equal(arguments.GetRawText(), sent);
    }

    [Fact]
    public async Task Session_bind_does_not_gain_an_invented_machine_identity()
    {
        string? sent = null;
        using var handler = new CaptureHandler(async request => sent = await request.Content!.ReadAsStringAsync());
        using var client = new HttpClient(handler);
        var arguments = JsonSerializer.Deserialize<JsonElement>("{\"repositoryId\":\"repo\",\"sessionId\":\"s\",\"binding\":{}}");
        Assert.True((await new RelayForwarder(client, new AdapterOptions("https://remote-hub.example", "client-secret")).ForwardAsync("session_bind", arguments)).Ok);
        Assert.Equal(arguments.GetRawText(), sent);
    }

    private sealed class CaptureHandler(Func<HttpRequestMessage, Task> capture) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await capture(request);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"code\":\"ok\",\"dataJson\":\"{}\",\"version\":1}", Encoding.UTF8, "application/json") };
        }
    }

    private sealed class FingerprintEnvironment : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable("COCERN_FINGERPRINT");
        public FingerprintEnvironment(string? fingerprint) => Environment.SetEnvironmentVariable("COCERN_FINGERPRINT", fingerprint);
        public void Dispose() => Environment.SetEnvironmentVariable("COCERN_FINGERPRINT", _previous);
    }

    private sealed class ConfigurationFixture : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "cocern-manual-wait-" + Guid.NewGuid().ToString("N"));
        public ConfigurationFixture()
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(Path.Combine(_folder, "token"), "client-secret");
            File.WriteAllText(Path.Combine(_folder, "session.json"), "{\"sessionId\":\"s\",\"sessionToken\":\"session-secret\"}");
        }
        public string Write(string url, string? fingerprint = null)
        {
            var configuration = new WakeConfiguration(url, Path.Combine(_folder, "token"), "", Path.Combine(_folder, "state.db"), Path.Combine(_folder, "stop"), [new("repo", "board", Path.Combine(_folder, "session.json"), "00000000-0000-0000-0000-000000000001", Environment.MachineName)]);
            var json = JsonSerializer.SerializeToNode(configuration, JsonSerializerOptions.Web)!.AsObject();
            if (fingerprint is not null) json["fingerprint"] = fingerprint;
            var path = Path.Combine(_folder, "await.json");
            File.WriteAllText(path, json.ToJsonString());
            return path;
        }
        public void Dispose() => Directory.Delete(_folder, true);
    }
}
