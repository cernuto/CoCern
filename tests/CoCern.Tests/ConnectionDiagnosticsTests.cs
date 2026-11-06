using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CoCern.Adapter;
using CoCern.Contracts;
using CoCern.Contracts.Dashboard;
using CoCern.Host.Dashboard;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CoCern.Tests;

public sealed class ConnectionDiagnosticsTests
{
    [Fact]
    public async Task Transport_phases_share_a_correlation_without_recording_arguments_credentials_or_reply()
    {
        using var store = new ConnectionTraceStore();
        using var capture = ConnectionTrace.Capture(store);
        using var handler = new ProbeHandler();
        using var client = new HttpClient(handler);
        var forwarder = new RelayForwarder(client, new AdapterOptions("http://127.0.0.1:45678/", "header-private"));
        var reply = await forwarder.ForwardAsync(RelayTools.GetStatus, JsonSerializer.SerializeToElement(new
        { repositoryId = "fixture", sessionId = "session-private", sessionToken = "token-private", privatePayload = "body-private" }));
        Assert.True(reply.Ok, reply.Code);
        var events = store.Read().Events;
        Assert.Contains(events, row => row.Stage == "http_headers");
        Assert.Contains(events, row => row.Stage == "http_body");
        Assert.Contains(events, row => row.Stage == "hub_to_headers" && row.DurationMs == 2.5);
        Assert.All(events, row => Assert.Equal(handler.TraceId, row.CorrelationId));
        Assert.True(ConnectionTraceStore.ValidId(handler.TraceId));
        var serialized = JsonSerializer.Serialize(events);
        foreach (var secret in new[] { "header-private", "session-private", "token-private", "body-private", "reply-private", "http://127.0.0.1:45678/" })
            Assert.DoesNotContain(secret, serialized);
    }

    [Fact]
    public async Task A_body_timeout_keeps_its_phase_original_error_and_support_id()
    {
        using var store = new ConnectionTraceStore();
        using var capture = ConnectionTrace.Capture(store);
        using var handler = new ProbeHandler(delayBody: true);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(40) };
        var forwarder = new RelayForwarder(client, new AdapterOptions("http://127.0.0.1:45678/", "synthetic"));
        var reply = await forwarder.ForwardAsync(RelayTools.GetStatus, JsonSerializer.SerializeToElement(new { repositoryId = "fixture" }));
        Assert.False(reply.Ok);
        Assert.Equal("hub_timeout", reply.Code);
        Assert.Equal(handler.TraceId, reply.ConnectionTraceId);
        Assert.Contains(store.Read().Events, row => row.Stage == "http_headers" && row.Outcome == "ok");
        Assert.Contains(store.Read().Events, row => row.Stage == "http_body" && row.ErrorCode == "hub_timeout");
        Assert.Contains(store.Read().Events, row => row.Stage == "total" && row.ErrorCode == "hub_timeout");
    }

    [Fact]
    public async Task Report_ingestion_requires_authentication_and_rejects_private_or_unbounded_fields()
    {
        using var host = new RelayHostFixture(settings: new Dictionary<string, string?>
        { ["Relay:ConnectionDiagnostics:IncludeLocalClients"] = "false" });
        using var client = host.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        var row = new ConnectionDiagnosticEvent { CorrelationId = "cc-" + Guid.NewGuid().ToString("N"), Tool = RelayTools.GetStatus,
            Source = "adapter", Stage = "http_headers", StartedAt = DateTimeOffset.UtcNow, DurationMs = 1, Outcome = "ok" };
        var batch = new ConnectionDiagnosticsBatch { Events = [row] };
        using var anonymous = await client.PostAsJsonAsync("/api/connection-diagnostics", batch);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", RelayHostFixture.Token);
        using var spoofed = await client.PostAsJsonAsync("/api/connection-diagnostics", new ConnectionDiagnosticsBatch { Events = [row with { Source = "hub" }] });
        Assert.Equal(HttpStatusCode.BadRequest, spoofed.StatusCode);
        var privateJson = JsonSerializer.Serialize(batch).Replace("\"Events\":", "\"sessionToken\":\"private-payload\",\"Events\":", StringComparison.Ordinal);
        using var privateFields = await client.PostAsync("/api/connection-diagnostics", new StringContent(privateJson, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, privateFields.StatusCode);
        using var excessive = await client.PostAsync("/api/connection-diagnostics", new StringContent(new string('x', 16385), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, excessive.StatusCode);
        using var accepted = await client.PostAsJsonAsync("/api/connection-diagnostics", batch);
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        var saved = host.Services.GetRequiredService<DashboardConnectionDiagnostics>().Read();
        Assert.Contains(saved.Events, item => item.CorrelationId == row.CorrelationId);
        Assert.DoesNotContain("private-payload", JsonSerializer.Serialize(saved));
    }

    private sealed class ProbeHandler(bool delayBody = false) : HttpMessageHandler
    {
        internal string? TraceId { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            TraceId = request.Headers.GetValues(ConnectionTrace.Header).Single();
            var result = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = delayBody ? new StreamContent(new WaitingStream()) :
                    new StringContent("{\"ok\":true,\"code\":\"Ok\",\"dataJson\":\"{\\\"secret\\\":\\\"reply-private\\\"}\",\"version\":1}", Encoding.UTF8, "application/json")
            };
            result.Headers.TryAddWithoutValidation(ConnectionTrace.HubTimingHeader, "2.5");
            return Task.FromResult(result);
        }
    }

    private sealed class WaitingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
