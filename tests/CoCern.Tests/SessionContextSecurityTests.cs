using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoCern.Adapter;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Xunit;
using static CoCern.Tests.SessionContextTests;

namespace CoCern.Tests;

public sealed class SessionContextSecurityTests
{
    [Fact]
    public async Task Repository_access_is_rechecked_across_tools_resources_and_successor_sessions()
    {
        await using var host = new RelayHostFixture(settings: new Dictionary<string, string?>
        {
            ["Relay:Clients:1:Token"] = "other-context-test-token", ["Relay:Clients:1:PrincipalId"] = "other-user", ["Relay:Clients:1:Repositories:0"] = "other"
        });
        using var owner = host.AuthorizedClient();
        var old = await Open(host, owner, "old-owner");
        Assert.True((await Call(owner, RelayTools.SessionContextPut, Envelope(old, new JsonObject { ["contextId"] = "recovery", ["expectedVersion"] = 0, ["requestId"] = "publish", ["patch"] = Patch("authorized-marker") }))).Ok);
        var successor = await Open(host, owner, "new-owner");
        Assert.NotEqual(old["sessionId"]!.GetValue<string>(), successor["sessionId"]!.GetValue<string>());
        Assert.True((await Call(owner, RelayTools.SessionContextGet, Envelope(successor, new JsonObject { ["contextId"] = "recovery" }))).Ok);
        var service = host.Services.GetRequiredService<SessionContextTools>();
        var uri = SessionContextResourceUri.ManifestUri("repo", "recovery");
        Assert.True((await service.ReadResourceAsync(new RelayCaller("test-user", false, ["repo"]), uri)).Ok);
        var manifest = Data(await service.RunAsync(new RelayCaller("test-user", false, ["repo"]), RelayTools.SessionContextGet,
            new JsonObject { ["repositoryId"] = "repo", ["contextId"] = "recovery" }));
        var entryUri = manifest["pinnedEntries"]![0]!["uri"]!.GetValue<string>();
        using var adapterHttp = new HttpClient(host.Server.CreateHandler());
        var adapter = new RelayForwarder(adapterHttp, new AdapterOptions("https://localhost", RelayHostFixture.Token, false, false));
        var forwarded = await adapter.ReadSessionContextResourceAsync(entryUri);
        Assert.True(forwarded.Ok);
        Assert.Contains("authorized-marker", Assert.IsType<TextResourceContents>(Assert.Single(SessionContextMcp.ReadResult(forwarded).Contents)).Text);
        var otherAdapter = new RelayForwarder(adapterHttp, new AdapterOptions("https://localhost", "other-context-test-token", false, false));
        var deniedForwarding = await otherAdapter.ReadSessionContextResourceAsync(entryUri);
        Assert.Equal(RelayErrorCodes.RepositoryDenied, deniedForwarding.Code);
        Assert.Empty(deniedForwarding.Text);
        var revoked = new RelayCaller("test-user", false, ["other"]);
        Assert.Equal(RelayErrorCodes.RepositoryDenied, (await service.ReadResourceAsync(revoked, uri)).Code);
        Assert.Equal(RelayErrorCodes.RepositoryDenied, (await service.RunAsync(revoked, RelayTools.SessionContextSearch, Query("authorized-marker"))).Code);
        using var outsider = host.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        outsider.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "other-context-test-token");
        using var denied = await outsider.GetAsync("/api/session-context/resource?uri=" + Uri.EscapeDataString(uri));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.DoesNotContain("authorized-marker", await denied.Content.ReadAsStringAsync());
        var forged = Envelope(old, new JsonObject { ["contextId"] = "recovery", ["label"] = "old-owner" });
        Assert.Equal(RelayErrorCodes.RepositoryDenied, (await Call(outsider, RelayTools.SessionContextGet, forged)).Code);
        using var anonymous = host.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var unauthenticated = await anonymous.GetAsync("/api/session-context/resource?uri=" + Uri.EscapeDataString(uri));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        var forgery = Patch("forged-provenance"); forgery["upserts"]![0]!["author"] = "somebody-else";
        Assert.Equal(RelayErrorCodes.InvalidArguments, (await Call(owner, RelayTools.SessionContextPut, Envelope(successor, new JsonObject { ["contextId"] = "recovery", ["expectedVersion"] = 1, ["requestId"] = "forged-author", ["patch"] = forgery }))).Code);
    }
}
