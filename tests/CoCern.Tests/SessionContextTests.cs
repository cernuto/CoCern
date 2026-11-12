using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Host;
using CoCern.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace CoCern.Tests;

public sealed class SessionContextTests
{
    [Fact]
    public async Task Committed_context_survives_delivery_lifecycle_and_reactivation()
    {
        var database = Path.Combine(Path.GetTempPath(), "cocern-retained-" + Guid.NewGuid().ToString("N"), "relay.db");
        var tokens = new FaultingTokens();
        string uri;
        await using (var host = new RelayHostFixture(database, configureServices: services => services.Replace(ServiceDescriptor.Singleton<ITokenCounter>(tokens))))
        {
            using var http = host.AuthorizedClient();
            var source = await Open(host, http, "source");
            var saved = await Call(http, RelayTools.SessionContextPut, Envelope(source, new JsonObject
            {
                ["contextId"] = "recovery", ["expectedVersion"] = 0, ["requestId"] = "save-context", ["patch"] = Patch("circuit-breaker-evidence")
            }));
            Assert.True(saved.Ok); uri = Data(saved)["uri"]!.GetValue<string>();
            var sent = await Call(http, RelayTools.SendMessage, Envelope(source, new JsonObject { ["recipientSessionId"] = source["sessionId"]!.GetValue<string>(), ["text"] = "Selected evidence was retained.", ["requestId"] = "source-message" }));
            Assert.True(sent.Ok);
            var messageId = Data(sent)["messageId"]!.GetValue<string>();
            var delivered = false;
            for (var n = 0; n < 100; n++)
            {
                var inbox = await Call(http, RelayTools.CheckInbox, Envelope(source, new JsonObject()));
                if (Data(inbox)["items"]!.AsArray().Any(item => item?["messageId"]?.GetValue<string>() == messageId)) { delivered = true; break; }
                await Task.Delay(50);
            }
            Assert.True(delivered);
            Assert.True((await Call(http, RelayTools.Ack, Envelope(source, new JsonObject { ["messageId"] = messageId, ["requestId"] = "ack-source" }))).Ok);
            var closed = await host.Services.GetRequiredService<IRelayApplication>().ExecuteAsync(new RelayCaller("test-admin", true, ["repo"]), RelayTools.ControlRequest,
                JsonSerializer.SerializeToElement(new { repositoryId = "repo", requestId = "close-source", command = "close_sessions", targetSessionId = source["sessionId"]!.GetValue<string>() }));
            Assert.True(closed.Ok);
        }

        await using var restarted = new RelayHostFixture(database, configureServices: services => services.Replace(ServiceDescriptor.Singleton<ITokenCounter>(tokens)));
        using var client = restarted.AuthorizedClient();
        var successor = await Open(restarted, client, "successor");
        var discovery = await Call(client, RelayTools.SessionContextGet, Envelope(successor, new JsonObject { ["taskId"] = "T-context" }));
        Assert.Equal("recovery", Data(discovery)["items"]![0]!["contextId"]!.GetValue<string>());
        var service = restarted.Services.GetRequiredService<SessionContextTools>();
        var caller = new RelayCaller("test-user", false, ["repo"]);
        var manifest = await service.RunAsync(caller, RelayTools.SessionContextGet, new JsonObject { ["repositoryId"] = "repo", ["contextId"] = "recovery" });
        Assert.True(manifest.Ok); Assert.Equal(uri, Data(manifest)["uri"]!.GetValue<string>());
        var partUri = Data(manifest)["pinnedEntries"]![0]!["uri"]!.GetValue<string>();
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri("https://localhost/mcp"), TransportMode = HttpTransportMode.StreamableHttp, EnableStandaloneGetStream = false }, client);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var mcp = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
        var resource = await mcp.ReadResourceAsync(partUri, cancellationToken: timeout.Token);
        Assert.Contains("circuit-breaker-evidence", Assert.IsType<TextResourceContents>(Assert.Single(resource.Contents)).Text);
        Assert.True((await service.RunAsync(caller, RelayTools.SessionContextSearch, Query("circuit-breaker-evidence"))).Ok);
        var store = restarted.Services.GetRequiredService<SqliteStore>();
        store.ArchiveFault = () => throw new IOException("Injected transaction failure.");
        try
        {
            var failed = await service.RunAsync(caller, RelayTools.SessionContextPut, new JsonObject
            {
                ["repositoryId"] = "repo", ["sessionId"] = successor["sessionId"]!.GetValue<string>(), ["requestId"] = "failed-patch", ["contextId"] = "recovery", ["expectedVersion"] = 1,
                ["patch"] = Patch("ghostquuxuncommitted")
            });
            Assert.Equal(RelayErrorCodes.StorageFailure, failed.Code);
        }
        finally { store.ArchiveFault = null; }
        Assert.Empty(Data(await service.RunAsync(caller, RelayTools.SessionContextSearch, Query("ghostquuxuncommitted")))["hits"]!.AsArray());
        Assert.Equal(1, Data(await store.ReadSessionContextsAsync("repo", "T-context", null, 10))["items"]![0]!["revision"]!.GetValue<long>());
        tokens.FailSnippet = true;
        try
        {
            Assert.Equal(SessionContextLimits.SearchUnavailable, (await service.RunAsync(caller, RelayTools.SessionContextSearch, Query("circuit-breaker-evidence"))).Code);
            Assert.True((await service.ReadResourceAsync(caller, partUri)).Ok);
        }
        finally { tokens.FailSnippet = false; }
        Assert.NotEmpty(Data(await service.RunAsync(caller, RelayTools.SessionContextSearch, Query("circuit-breaker-evidence")))["hits"]!.AsArray());
    }

    [Fact]
    public async Task Replay_versions_and_forget_preserve_reference_integrity()
    {
        await using var host = new RelayHostFixture();
        using var http = host.AuthorizedClient();
        var session = await Open(host, http, "replay");
        var save = Envelope(session, new JsonObject { ["contextId"] = "recovery", ["expectedVersion"] = 0, ["requestId"] = "create", ["patch"] = Patch("original-marker") });
        var first = await Call(http, RelayTools.SessionContextPut, save);
        Assert.True(first.Ok); Assert.Equal(first.DataJson, (await Call(http, RelayTools.SessionContextPut, save)).DataJson);
        var changedReplay = (JsonObject)save.DeepClone(); changedReplay["patch"] = Patch("different-marker");
        Assert.Equal(RelayErrorCodes.RequestConflict, (await Call(http, RelayTools.SessionContextPut, changedReplay)).Code);
        var service = host.Services.GetRequiredService<SessionContextTools>();
        var caller = new RelayCaller("test-user", false, ["repo"]);
        var manifest = Data(await service.RunAsync(caller, RelayTools.SessionContextGet, new JsonObject { ["repositoryId"] = "repo", ["contextId"] = "recovery" }));
        var oldUri = manifest["pinnedEntries"]![0]!["uri"]!.GetValue<string>();
        Assert.True((await service.ReadResourceAsync(caller, oldUri)).Ok);
        var update = Envelope(session, new JsonObject { ["contextId"] = "recovery", ["expectedVersion"] = 1, ["requestId"] = "replace", ["patch"] = Patch("replacement-marker") });
        Assert.True((await Call(http, RelayTools.SessionContextPut, update)).Ok);
        Assert.Equal(SessionContextLimits.VersionUnavailable, (await service.ReadResourceAsync(caller, oldUri)).Code);
        update["requestId"] = "stale-update";
        Assert.Equal(RelayErrorCodes.VersionConflict, (await Call(http, RelayTools.SessionContextPut, update)).Code);
        var delete = Envelope(session, new JsonObject { ["contextId"] = "recovery", ["expectedVersion"] = 2, ["requestId"] = "delete-entry", ["patch"] = new JsonObject { ["deleteEntryIds"] = new JsonArray("evidence"), ["pinnedEntryIds"] = new JsonArray() } });
        Assert.True((await Call(http, RelayTools.SessionContextPut, delete)).Ok);
        var recreate = Envelope(session, new JsonObject { ["contextId"] = "recovery", ["expectedVersion"] = 3, ["requestId"] = "recreate-entry", ["patch"] = Patch("recreated-marker") });
        Assert.True((await Call(http, RelayTools.SessionContextPut, recreate)).Ok);
        var now = Data(await service.RunAsync(caller, RelayTools.SessionContextGet, new JsonObject { ["repositoryId"] = "repo", ["contextId"] = "recovery" }));
        Assert.Equal(4, now["pinnedEntries"]![0]!["version"]!.GetValue<long>());
        Assert.Equal(SessionContextLimits.VersionUnavailable, (await service.ReadResourceAsync(caller, oldUri)).Code);
        var forget = Envelope(session, new JsonObject { ["contextId"] = "recovery", ["expectedVersion"] = 4, ["requestId"] = "forget", ["forget"] = true });
        Assert.True((await Call(http, RelayTools.SessionContextPut, forget)).Ok);
        Assert.False((await service.ReadResourceAsync(caller, now["uri"]!.GetValue<string>())).Ok);
        Assert.False((await service.RunAsync(caller, RelayTools.SessionContextSearch, Query("recreated-marker"))).Ok);
        Assert.Empty(Data(await host.Services.GetRequiredService<SqliteStore>().ReadSessionContextsAsync("repo", null, null, 10))["items"]!.AsArray());
        Assert.True((await Call(http, RelayTools.SessionContextPut, forget)).Ok);
        recreate["expectedVersion"] = 5; recreate["requestId"] = "reuse-forgotten-id";
        Assert.Equal(RelayErrorCodes.NotFound, (await Call(http, RelayTools.SessionContextPut, recreate)).Code);
    }

    internal static JsonObject Patch(string marker) => new()
    {
        ["title"] = "Recovery handoff", ["summary"] = "Preserve the circuit breaker decision.", ["nextSteps"] = new JsonArray("Inspect current evidence before changing the breaker."),
        ["taskIds"] = new JsonArray("T-context"), ["pinnedEntryIds"] = new JsonArray("evidence"),
        ["upserts"] = new JsonArray(new JsonObject { ["entryId"] = "evidence", ["kind"] = "evidence", ["title"] = "Circuit breaker evidence", ["text"] = string.Join(' ', Enumerable.Repeat(marker + " measured evidence remains available.", 40)), ["sourceRefs"] = new JsonArray("task:T-context") })
    };
    internal static JsonObject Query(string query) => new() { ["repositoryId"] = "repo", ["contextId"] = "recovery", ["query"] = query };
    internal static JsonObject Envelope(JsonObject session, JsonObject args)
    {
        args["repositoryId"] = "repo"; args["sessionId"] = session["sessionId"]!.GetValue<string>(); args["sessionToken"] = session["sessionToken"]!.GetValue<string>(); return args;
    }
    internal static JsonObject Data(RelayReply reply) => JsonNode.Parse(reply.DataJson)!.AsObject();
    internal static async Task<JsonObject> Open(RelayHostFixture host, HttpClient http, string label) => Data(await Call(http, RelayTools.SessionOpen,
        new JsonObject { ["repositoryId"] = "repo", ["requestId"] = "open-" + label, ["label"] = label }));
    internal static async Task<RelayReply> Call(HttpClient http, string tool, JsonObject args)
    {
        using var response = await http.PostAsJsonAsync("/api/tools/" + tool, args);
        return (await response.Content.ReadFromJsonAsync<RelayReply>())!;
    }
    private sealed class FaultingTokens : ITokenCounter
    {
        private readonly O200kTokenCounter inner = new();
        public bool FailSnippet { get; set; }
        public string Encoding => inner.Encoding;
        public int Count(string text) => inner.Count(text);
        public int PrefixLength(string text, int maxTokens) => FailSnippet && maxTokens == SessionContextLimits.SnippetTokens ? throw new IOException("Injected index failure.") : inner.PrefixLength(text, maxTokens);
    }
}
