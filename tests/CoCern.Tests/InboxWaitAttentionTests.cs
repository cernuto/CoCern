using System.Diagnostics;
using System.Text.Json;
using CoCern.Adapter;
using Xunit;

namespace CoCern.Tests;

/// <summary>
/// A waiting <c>check_inbox</c> must not hide urgent work from a manager: its polls take no offer, but an urgent item (blocked, failed,
/// interrupted) ends the wait, and the read that returns carries that one offer. Anything less urgent waits for mail or the end of the wait.
/// </summary>
public sealed class InboxWaitAttentionTests
{
    [Fact]
    public async Task Waiting_check_inbox_ends_early_and_offers_an_urgent_item()
    {
        // A long wait and a loose bound: the reply's offer already proves the wait ended early; the time only rules out waiting it out.
        var hub = new AttentionHub { UrgentFromStatusCall = 2 };
        var reply = await Wait(hub, waitSeconds: 30, out var elapsed);
        Assert.True(reply.Ok);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(20), $"The wait ran {elapsed.Elapsed} although an urgent item was waiting.");
        Assert.Equal(0, reply.Data!.Value.GetProperty("total").GetInt32());
        Assert.Equal("blocked", reply.Data!.Value.GetProperty("attention").GetProperty("k").GetString());
        // Only the read that returns may take the offer; every poll before it declined one.
        Assert.Equal(1, hub.OfferingReads);
        Assert.DoesNotContain("\"attention\":false", hub.CheckBodies[^1]);
        Assert.All(hub.CheckBodies[..^1], body => Assert.Contains("\"attention\":false", body));
        Assert.All(hub.StatusBodies, body => Assert.Contains("\"attentionDetail\":true", body));
        Assert.All(hub.CheckBodies[..^1], body => Assert.Contains("\"backgroundCall\":true", body));
        Assert.All(hub.StatusBodies, body => Assert.Contains("\"backgroundCall\":true", body));
        Assert.DoesNotContain("\"backgroundCall\":true", hub.CheckBodies[^1]);
    }

    private static Task<AdapterReply> Wait(AttentionHub hub, int waitSeconds, out Stopwatch elapsed, bool? attention = null)
    {
        var client = new HttpClient(hub);
        var forwarder = new RelayForwarder(client, new AdapterOptions("http://127.0.0.1:5271", "client-secret"));
        var values = new Dictionary<string, object>
        {
            ["repositoryId"] = "repo", ["sessionId"] = "s", ["sessionToken"] = "t", ["waitSeconds"] = waitSeconds
        };
        if (attention is { } enabled) values["attention"] = enabled;
        var arguments = JsonSerializer.SerializeToElement(values);
        elapsed = Stopwatch.StartNew();
        return InboxWait.CheckAsync(forwarder, arguments, TimeSpan.FromMilliseconds(20), CancellationToken.None);
    }

    /// <summary>An empty inbox with one blocked item that turns urgent from a given <c>get_status</c> call; a review_ready item waits throughout.</summary>
    private sealed class AttentionHub : HttpMessageHandler
    {
        public int UrgentFromStatusCall;
        public string Wake = "a-7";
        public string Kind = "blocked";
        public int Urgent = 1;
        public int OfferingReads;
        public readonly List<string> CheckBodies = [];
        public readonly List<string> StatusBodies = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var tool = request.RequestUri!.AbsolutePath.Split('/')[^1];
            string data;
            if (tool == "get_status")
            {
                StatusBodies.Add(body);
                var urgent = StatusBodies.Count >= UrgentFromStatusCall;
                data = JsonSerializer.Serialize(new
                {
                    sessionId = "s",
                    attention = urgent
                        ? (object)new { actionable = 2, urgent = Urgent, inbox = 0, top = new { id = "a-7", k = Kind, t = "T-1" }, wake = Wake }
                        : new { actionable = 1, urgent = 0, inbox = 0, top = new { id = "a-8", k = "review_ready", t = "T-2" } },
                    control = new { paused = false, cancellations = 0, requests = Array.Empty<object>() }
                });
            }
            else
            {
                CheckBodies.Add(body);
                var offers = !body.Contains("\"attention\":false") && StatusBodies.Count >= UrgentFromStatusCall;
                if (offers) OfferingReads++;
                data = offers
                    ? JsonSerializer.Serialize(new { items = Array.Empty<object>(), nextCursor = (int?)null, truncated = false, total = 0, attention = new { id = "a-7", k = Kind, t = "T-1", x = "T-1 attention", more = 1 } })
                    : JsonSerializer.Serialize(new { items = Array.Empty<object>(), nextCursor = (int?)null, truncated = false, total = 0 });
            }
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { ok = true, code = "Ok", dataJson = data, version = 0 }), System.Text.Encoding.UTF8, "application/json") };
        }
    }
}
