using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using CoCern.Contracts;
using CoCern.Contracts.Dashboard;
using CoCern.Core;
using CoCern.Domain;
using Xunit;

namespace CoCern.Tests;

/// <summary>
/// The Sessions page's "Close idle sessions": a signed-in dashboard viewer's same-origin POST to <c>/dashboard/api/sessions/close</c> runs
/// <c>control_request close_sessions</c> for one repository as that viewer, and answers with the counts, never a session token.
/// </summary>
public sealed class DashboardSessionCleanupTests
{
    private const string Route = "/dashboard/api/sessions/close";
    private const string OtherToken = "other-admin-test-secret-not-a-production-token";
    private static readonly RelayCaller Worker = new("test-user", false, ["repo"]);
    private static readonly RelayCaller Admin = new("test-user", true, ["repo"]);

    [Fact]
    public async Task A_signed_in_viewer_closes_the_matching_sessions_and_gets_the_counts()
    {
        await using var host = ViewerHost();
        var smoke = await OpenAsync(host, "Smoke test 1");
        var smoke2 = await OpenAsync(host, "SMOKE-2");
        var work = await OpenAsync(host, "Real work");
        using var browser = await SignedInAsync(host);

        // Nothing just opened has been idle for a day, so the idle filter closes nothing.
        using (var idle = await browser.PostAsync(Route, Json(new { repositoryId = "repo", idleMinutes = 1440 })))
        {
            Assert.Equal(HttpStatusCode.OK, idle.StatusCode);
            var none = (await idle.Content.ReadFromJsonAsync<DashboardCloseSessionsResult>())!;
            Assert.Equal((0, 0), (none.Closed, none.Kept));
            Assert.Empty(none.Ids);
        }
        Assert.Equal(3, (await LiveAsync(host)).Length);

        using var response = await browser.PostAsync(Route, Json(new { repositoryId = "repo", labelPrefix = "smoke" }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<DashboardCloseSessionsResult>())!;
        Assert.Equal((2, 0), (result.Closed, result.Kept));
        Assert.Null(result.Code);
        Assert.Equal(new[] { smoke.Id, smoke2.Id }.Order(StringComparer.Ordinal), result.Ids.Order(StringComparer.Ordinal));
        Assert.Equal([work.Id], await LiveAsync(host));
    }

    [Fact]
    public async Task A_viewer_can_close_one_exact_session_without_closing_an_identically_labelled_peer()
    {
        await using var host = ViewerHost();
        var target = await OpenAsync(host, "Same label");
        var peer = await OpenAsync(host, "Same label");
        var directory = host.Services.GetRequiredService<IGrainFactory>().GetGrain<IControlGrain>(AggregateKinds.ControlKey);
        var tracked = await directory.ExecuteAsync(new RelayCaller("test-internal", false, ["repo"], true), Operations.SessionWork,
            JsonSerializer.Serialize(new { repositoryId = "repo", sessionId = target.Id, owns = true, seq = 1 }));
        Assert.True(tracked.Ok, tracked.DataJson);
        using var browser = await SignedInAsync(host);
        using (var normal = await browser.PostAsync(Route, Json(new { repositoryId = "repo", targetSessionId = target.Id })))
        {
            Assert.Equal(HttpStatusCode.OK, normal.StatusCode);
            var kept = (await normal.Content.ReadFromJsonAsync<DashboardCloseSessionsResult>())!;
            Assert.Equal((0, 1), (kept.Closed, kept.Kept));
        }
        using var response = await browser.PostAsync(Route, Json(new { repositoryId = "repo", targetSessionId = target.Id, force = true }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<DashboardCloseSessionsResult>())!;
        Assert.Equal((1, 0), (result.Closed, result.Kept));
        Assert.Equal([target.Id], result.Ids);
        Assert.Equal([peer.Id], await LiveAsync(host));
    }

    [Fact]
    public async Task An_anonymous_caller_a_relay_token_and_a_non_viewer_are_refused()
    {
        await using var host = ViewerHost(new Dictionary<string, string?>
        {
            ["Relay:Clients:1:Token"] = OtherToken,
            ["Relay:Clients:1:PrincipalId"] = "other-admin",
            ["Relay:Clients:1:IsAdministrator"] = "true",
            ["Relay:Clients:1:Repositories:0"] = "repo"
        });
        var smoke = await OpenAsync(host, "Smoke test");
        var body = new { repositoryId = "repo", labelPrefix = "smoke" };

        using (var anonymous = Browser(host))
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync(Route, Json(body))).StatusCode);

        // A relay client token is not a dashboard sign-in, even an administrator's.
        using (var bearer = Browser(host))
        {
            bearer.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", RelayHostFixture.Token);
            Assert.Equal(HttpStatusCode.Unauthorized, (await bearer.PostAsync(Route, Json(body))).StatusCode);
        }

        // An administrator that is not a configured dashboard viewer cannot sign in, so it cannot close sessions either.
        using (var other = Browser(host))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await other.PostAsync("/dashboard/api/login", Json(new { token = OtherToken }))).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await other.PostAsync(Route, Json(body))).StatusCode);
        }

        Assert.Equal([smoke.Id], await LiveAsync(host));
    }

    [Fact]
    public async Task A_signed_in_request_without_a_same_origin_header_is_refused()
    {
        await using var host = ViewerHost();
        var smoke = await OpenAsync(host, "Smoke test");
        using var browser = await SignedInAsync(host);
        var body = new { repositoryId = "repo", labelPrefix = "smoke" };

        // The session cookie rides along on a cross-site form or fetch; only the browser's Origin tells them apart.
        browser.DefaultRequestHeaders.Remove("Origin");
        foreach (var origin in new string?[] { null, "https://evil.example", "http://localhost", "null" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Route) { Content = Json(body) };
            if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
            using var response = await browser.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        Assert.Equal([smoke.Id], await LiveAsync(host));
    }

    [Theory]
    [InlineData("{\"repositoryId\":\"repo\",\"idleMinutes\":30}", "idleMinutes")]
    [InlineData("{\"repositoryId\":\"repo\",\"targetSessionId\":\"\"}", "targetSessionId")]
    [InlineData("{\"repositoryId\":\"repo\",\"labelPrefix\":\"smoke\",\"force\":true}", "force")]
    [InlineData("{\"repositoryId\":\"repo\",\"targetSessionId\":\"s\",\"force\":\"true\"}", "force")]
    [InlineData("{\"repositoryId\":\"repo\",\"idleMinutes\":\"an hour\"}", "idleMinutes")]
    [InlineData("{\"repositoryId\":\"repo\",\"labelPrefix\":\"\"}", null)]
    [InlineData("{\"repositoryId\":\"repo\"}", "idleMinutes")]
    [InlineData("{\"idleMinutes\":60}", "repositoryId")]
    [InlineData("{\"repositoryId\":\"\",\"idleMinutes\":60}", "repositoryId")]
    [InlineData("not json", null)]
    [InlineData("[]", null)]
    public async Task Bad_input_is_a_clean_bad_request_with_the_hub_code(string body, string? field)
    {
        await using var host = ViewerHost();
        var smoke = await OpenAsync(host, "Smoke test");
        using var browser = await SignedInAsync(host);
        using var response = await browser.PostAsync(Route, new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<DashboardCloseSessionsResult>())!;
        Assert.Equal(RelayErrorCodes.InvalidArguments, result.Code);
        if (field is not null) Assert.Equal(field, result.Field);
        Assert.Equal(0, result.Closed);
        Assert.Equal([smoke.Id], await LiveAsync(host));
    }

    [Fact]
    public async Task The_reply_carries_no_tokens_or_session_records()
    {
        await using var host = ViewerHost();
        var smoke = await OpenAsync(host, "Smoke test");
        var work = await OpenAsync(host, "Real work");
        using var browser = await SignedInAsync(host);
        using var response = await browser.PostAsync(Route, Json(new { repositoryId = "repo", labelPrefix = "smoke", sessionId = work.Id, sessionToken = work.Token }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(smoke.Token, text);
        Assert.DoesNotContain(work.Token, text);
        Assert.DoesNotContain(RelayHostFixture.Token, text);
        Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("label", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["closed", "code", "field", "held", "ids", "kept", "more"], JsonNode.Parse(text)!.AsObject().Select(property => property.Key).Order(StringComparer.Ordinal));
        Assert.Equal([smoke.Id], JsonNode.Parse(text)!["ids"]!.AsArray().Select(id => id!.GetValue<string>()));
        // A session named in the body is not the viewer's own: the dashboard forwards only the repository and the filter.
        Assert.Equal([work.Id], await LiveAsync(host));
    }

    private static RelayHostFixture ViewerHost(Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Relay:Clients:0:IsAdministrator"] = "true",
            ["Relay:Dashboard:ViewerPrincipalIds:0"] = "test-user"
        };
        foreach (var (key, value) in extra ?? []) settings[key] = value;
        return new RelayHostFixture(settings: settings);
    }

    private static HttpClient Browser(RelayHostFixture host)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Origin", "https://localhost");
        return client;
    }

    private static async Task<HttpClient> SignedInAsync(RelayHostFixture host)
    {
        var browser = Browser(host);
        (await browser.PostAsync("/dashboard/api/login", Json(new { token = RelayHostFixture.Token }))).EnsureSuccessStatusCode();
        return browser;
    }

    private static async Task<(string Id, string Token)> OpenAsync(RelayHostFixture host, string label)
    {
        var reply = await host.Services.GetRequiredService<RelayApplication>().ExecuteAsync(Worker, RelayTools.SessionOpen,
            JsonSerializer.SerializeToElement(new { repositoryId = "repo", requestId = "open-" + Guid.NewGuid().ToString("N"), label }));
        Assert.True(reply.Ok, reply.Code + " " + reply.DataJson);
        var data = JsonNode.Parse(reply.DataJson)!;
        return (data["sessionId"]!.GetValue<string>(), data["sessionToken"]!.GetValue<string>());
    }

    private static async Task<string[]> LiveAsync(RelayHostFixture host)
    {
        var reply = await host.Services.GetRequiredService<RelayApplication>().ExecuteAsync(Admin, RelayTools.WorkerList, JsonSerializer.SerializeToElement(new { repositoryId = "repo" }));
        Assert.True(reply.Ok, reply.Code + " " + reply.DataJson);
        return JsonNode.Parse(reply.DataJson)!["items"]!.AsArray().Select(worker => worker!["sessionId"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToArray();
    }

    private static StringContent Json(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
}
