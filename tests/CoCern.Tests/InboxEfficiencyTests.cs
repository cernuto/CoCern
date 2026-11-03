using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;
using CoCern.Adapter;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
using Xunit;

namespace CoCern.Tests;

/// <summary>
/// A background reader that only needs to know which messages wait (the wake watcher) reads them as headers, and an inbox it has
/// already seen costs one tiny reply. A load test re-read the whole inbox with every message's text on every new message: about
/// 3.9 MB of replies to take in 160 KB of messages.
/// </summary>
public sealed class InboxEfficiencyTests
{
    [Fact]
    public async Task A_headers_only_read_drops_the_texts_and_an_unchanged_inbox_answers_with_its_etag_alone()
    {
        using var host = new HostBuilder().ConfigureLogging(log => log.ClearProviders()).UseOrleans(silo => silo
            .UseLocalhostClustering(siloPort: FreePort(), gatewayPort: FreePort(), serviceId: "inbox-efficiency-tests", clusterId: Guid.NewGuid().ToString("N"))
            .AddMemoryGrainStorage("relay"))
            .ConfigureServices(services => services.AddCoCernCore()).Build();
        await host.StartAsync();
        try
        {
            var application = host.Services.GetRequiredService<RelayApplication>();
            var caller = new RelayCaller("alice", false, ["repo"]);
            async Task<JsonNode> Call(string tool, object args)
            {
                var reply = await application.ExecuteAsync(caller, tool, JsonSerializer.SerializeToElement(args));
                Assert.True(reply.Ok, reply.Code);
                return JsonNode.Parse(reply.DataJson)!;
            }
            var sender = await Call(RelayTools.SessionOpen, new { repositoryId = "repo", requestId = "open-sender", label = "Sender" });
            var reader = await Call(RelayTools.SessionOpen, new { repositoryId = "repo", requestId = "open-reader", label = "Reader" });
            string Id(JsonNode session) => session["sessionId"]!.GetValue<string>();
            string Token(JsonNode session) => session["sessionToken"]!.GetValue<string>();
            Task Send(int n, bool wake) => Call(RelayTools.SendMessage, new { repositoryId = "repo", sessionId = Id(sender), sessionToken = Token(sender), recipientSessionId = Id(reader), requestId = $"send-{n}", text = $"message {n}: " + new string('x', 400), wake });
            Task<JsonNode> Read(object extra) => Call(RelayTools.CheckInbox, Merge(new { repositoryId = "repo", sessionId = Id(reader), sessionToken = Token(reader), attention = false }, extra));
            await Send(1, true);
            await Send(2, false);
            await Send(3, true);
            // Delivery to the reader's inbox follows the send: the host's background dispatch runs it, as here.
            var factory = host.Services.GetRequiredService<Orleans.IGrainFactory>();
            async Task Delivered() => Assert.True((await factory.GetGrain<ISessionGrain>(Id(sender)).ExecuteAsync(caller with { IsInternal = true }, "__dispatch", "{}")).Ok);
            await Delivered();

            var full = await Read(new { });
            Assert.All(full["items"]!.AsArray(), item => Assert.NotNull(item!["text"]));
            Assert.Null(full["etag"]);

            var headers = await Read(new { headersOnly = true });
            var items = headers["items"]!.AsArray();
            Assert.Equal(3, items.Count);
            Assert.All(items, item => { Assert.Null(item!["text"]); Assert.NotNull(item["messageId"]); Assert.Equal("repo", item["repositoryId"]!.GetValue<string>()); });
            Assert.False(items[1]!["wake"]!.GetValue<bool>());
            var etag = headers["etag"]!.GetValue<string>();
            Assert.True(headers.ToJsonString().Length * 2 < full.ToJsonString().Length);

            var same = await Read(new { headersOnly = true, knownInboxEtag = etag });
            Assert.True(same["unchanged"]!.GetValue<bool>());
            Assert.Equal(etag, same["etag"]!.GetValue<string>());
            Assert.Equal(3, same["total"]!.GetValue<int>());
            Assert.Null(same["items"]);

            await Send(4, true);
            await Delivered();
            var changed = await Read(new { headersOnly = true, knownInboxEtag = etag });
            Assert.Null(changed["unchanged"]);
            Assert.Equal(4, changed["items"]!.AsArray().Count);
            Assert.NotEqual(etag, changed["etag"]!.GetValue<string>());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Hints_that_keep_arriving_are_coalesced_until_a_quiet_gap_but_never_longer_than_the_cap()
    {
        var hints = Channel.CreateUnbounded<bool>();
        // One hint: the wait ends one quiet gap after it.
        hints.Writer.TryWrite(true);
        var started = System.Diagnostics.Stopwatch.StartNew();
        await HintCoalescer.WaitAsync(hints.Reader, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.InRange(started.Elapsed, TimeSpan.FromMilliseconds(90), TimeSpan.FromSeconds(2));
        Assert.False(hints.Reader.TryRead(out _));

        // A quiet gap longer than the cap isolates the deadline from delays in scheduling the producer.
        using var stream = new CancellationTokenSource();
        var writer = Task.Run(async () => { while (!stream.IsCancellationRequested) { hints.Writer.TryWrite(true); await Task.Delay(30); } });
        try
        {
            started.Restart();
            await HintCoalescer.WaitAsync(hints.Reader, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(400), CancellationToken.None);
            Assert.InRange(started.Elapsed, TimeSpan.FromMilliseconds(380), TimeSpan.FromSeconds(3));
        }
        finally
        {
            await stream.CancelAsync();
            await writer;
        }
    }

    private static object Merge(object first, object second)
    {
        var merged = JsonSerializer.SerializeToNode(first)!.AsObject();
        foreach (var (key, value) in JsonSerializer.SerializeToNode(second)!.AsObject()) merged[key] = value?.DeepClone();
        return merged;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
