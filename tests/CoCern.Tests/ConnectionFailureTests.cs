using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using CoCern.Adapter;
using CoCern.Adapter.Node;
using CoCern.Contracts;
using Xunit;

namespace CoCern.Tests;

public sealed class ConnectionFailureTests
{
    private const string IdentityMessage = "The hub's identity changed. Check with the person who runs the hub before reconnecting this PC.";
    private const string UnavailableMessage = "Can't reach the hub. Check that it is running and try again.";

    [Fact]
    public async Task Pinned_identity_refusal_never_sends_HTTP_and_cannot_taint_another_request()
    {
        await using var hub = new TlsHub();
        var pin = CertificateFingerprint.Of(hub.Certificate);
        var wrongPin = (pin[0] == 'A' ? "B" : "A") + pin[1..];
        var options = new AdapterOptions(hub.Url, "synthetic-credential");
        using var wrongHandler = PinnedCertificate.CreateHandler(wrongPin);
        using var wrongClient = new HttpClient(wrongHandler) { Timeout = TimeSpan.FromSeconds(5) };
        var forwarder = new RelayForwarder(wrongClient, options);

        var refused = await forwarder.ForwardAsync("task_get", Arguments());
        Assert.Equal(0, hub.ReceivedHttpRequests);
        AssertFailure(refused, "hub_identity_changed", IdentityMessage);

        using var matchingHandler = PinnedCertificate.CreateHandler(pin);
        using var matchingClient = new HttpClient(matchingHandler) { Timeout = TimeSpan.FromSeconds(5) };
        var positive = await new RelayForwarder(matchingClient, options).ForwardAsync("task_get", Arguments());
        Assert.True(positive.Ok);
        Assert.Equal("Ok", positive.Code);
        Assert.Equal(1, hub.ReceivedHttpRequests);

        await hub.StopAsync();
        var unavailable = await forwarder.ForwardAsync("task_get", Arguments());
        AssertFailure(unavailable, "hub_unavailable", UnavailableMessage);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Concurrent_requests_keep_identity_evidence_local(bool mismatchFinishesFirst, bool ioFailure)
    {
        using var handler = new BarrierHandler(ioFailure);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var forwarder = new RelayForwarder(client, new AdapterOptions("https://127.0.0.1:1/", "synthetic-credential"));
        var mismatch = forwarder.ForwardAsync("task_get", JsonSerializer.SerializeToElement(new { identityMismatch = true }));
        var outage = forwarder.ForwardAsync("task_get", JsonSerializer.SerializeToElement(new { identityMismatch = false }));
        await handler.BothEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var first = mismatchFinishesFirst ? mismatch : outage;
        handler.Release(mismatchFinishesFirst);
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        handler.Release(!mismatchFinishesFirst);
        await Task.WhenAll(mismatch, outage).WaitAsync(TimeSpan.FromSeconds(5));

        AssertFailure(await mismatch, "hub_identity_changed", IdentityMessage);
        AssertFailure(await outage, "hub_unavailable", UnavailableMessage);
    }

    [Fact]
    public void Pin_callback_marks_only_a_presented_mismatch_and_preserves_observer_and_handler_safety()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var pin = CertificateFingerprint.Of(certificate);
        var observed = new List<string>();
        using var handler = PinnedCertificate.CreateHandler(pin, observed.Add);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);

        using var missing = new HttpRequestMessage();
        Assert.False(handler.ServerCertificateCustomValidationCallback!(missing, null, null, SslPolicyErrors.RemoteCertificateNotAvailable));
        Assert.False(missing.Options.TryGetValue(PinnedCertificate.IdentityMismatch, out _));
        Assert.Equal("none", Assert.Single(observed));

        using var matching = new HttpRequestMessage();
        Assert.True(handler.ServerCertificateCustomValidationCallback!(matching, certificate, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(matching.Options.TryGetValue(PinnedCertificate.IdentityMismatch, out _));
        Assert.Single(observed);

        var wrongPin = (pin[0] == 'A' ? "B" : "A") + pin[1..];
        using var wrongHandler = PinnedCertificate.CreateHandler(wrongPin, observed.Add);
        using var mismatch = new HttpRequestMessage();
        Assert.False(wrongHandler.ServerCertificateCustomValidationCallback!(mismatch, certificate, null, SslPolicyErrors.None));
        Assert.True(mismatch.Options.TryGetValue(PinnedCertificate.IdentityMismatch, out var marked) && marked);
        Assert.Equal(pin, observed[1]);
    }

    private static JsonElement Arguments() => JsonSerializer.SerializeToElement(new { repositoryId = "fixture" });

    private static void AssertFailure(AdapterReply reply, string code, string message)
    {
        Assert.False(reply.Ok);
        Assert.Equal(code, reply.Code);
        Assert.Equal(message, reply.Data?.GetProperty("message").GetString());
        Assert.Null(reply.Version);
        var serialized = JsonSerializer.Serialize(reply);
        Assert.DoesNotContain("synthetic-credential", serialized);
        Assert.DoesNotContain("exception-sentinel", serialized);
    }

    private sealed class BarrierHandler(bool ioFailure) : HttpMessageHandler
    {
        private readonly TaskCompletionSource mismatch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource outage = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int entered;
        public TaskCompletionSource BothEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release(bool identityMismatch) => (identityMismatch ? mismatch : outage).TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var identityMismatch = body.RootElement.GetProperty("identityMismatch").GetBoolean();
            if (identityMismatch) request.Options.Set(PinnedCertificate.IdentityMismatch, true);
            if (Interlocked.Increment(ref entered) == 2) BothEntered.TrySetResult();
            await (identityMismatch ? mismatch : outage).Task.WaitAsync(cancellationToken);
            if (ioFailure) throw new IOException("exception-sentinel");
            throw new HttpRequestException("exception-sentinel");
        }
    }

    private sealed class TlsHub : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly List<Task> connections = [];
        private readonly Task acceptLoop;
        private int receivedHttpRequests;
        public X509Certificate2 Certificate { get; }
        public string Url { get; }
        public int ReceivedHttpRequests => Volatile.Read(ref receivedHttpRequests);

        public TlsHub()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            Certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null);
            listener.Start();
            Url = $"https://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
            acceptLoop = AcceptAsync();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var socket = await listener.AcceptTcpClientAsync(stop.Token);
                    connections.Add(RespondAsync(socket));
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (SocketException) when (stop.IsCancellationRequested) { }
        }

        private async Task RespondAsync(TcpClient socket)
        {
            using (socket)
            using (var tls = new SslStream(socket.GetStream()))
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = Certificate }, timeout.Token);
                    using var reader = new StreamReader(tls, Encoding.ASCII, leaveOpen: true);
                    var first = await reader.ReadLineAsync(timeout.Token);
                    if (first is null) return;
                    while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 }) { }
                    Interlocked.Increment(ref receivedHttpRequests);
                    const string body = "{\"ok\":true,\"code\":\"Ok\",\"dataJson\":\"{}\",\"version\":1}";
                    var reply = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");
                    await tls.WriteAsync(reply, timeout.Token);
                    await tls.FlushAsync(timeout.Token);
                }
                catch (AuthenticationException) { }
                catch (IOException) { }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
            }
        }

        public async Task StopAsync()
        {
            await stop.CancelAsync();
            listener.Stop();
            await acceptLoop.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(connections).WaitAsync(TimeSpan.FromSeconds(5));
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            Certificate.Dispose();
            stop.Dispose();
        }
    }
}
