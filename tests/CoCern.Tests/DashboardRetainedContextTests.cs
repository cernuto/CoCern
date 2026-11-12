using System.Net;
using System.Security.Claims;
using System.Text.Json.Nodes;
using CoCern.Contracts;
using CoCern.Host.Dashboard;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static CoCern.Tests.SessionContextTests;

namespace CoCern.Tests;

public sealed class DashboardRetainedContextTests
{
    [Fact]
    public async Task Retained_reads_recheck_the_current_viewer_instead_of_trusting_a_cookie_or_session()
    {
        await using var host = Host();
        using var owner = host.AuthorizedClient();
        var session = await Open(host, owner, "dashboard-retained");
        Assert.True((await Save(owner, session, 0, "private-retained-marker")).Ok);
        var retained = host.Services.GetRequiredService<DashboardRetainedContext>();
        var viewer = host.Services.GetRequiredService<DashboardSecurity>().Exchange(RelayHostFixture.Token, DateTimeOffset.UtcNow)!;
        var listed = Read<DashboardRetainedContextList>(await retained.ListAsync(Context(host, viewer, "repositoryId=REPO")));
        Assert.True(listed.Ok);
        Assert.Equal("repo", listed.RepositoryId);
        Assert.Equal("dashboard-retained", Assert.Single(listed.Data!.Items).ContextId);
        var manifest = Read<DashboardRetainedContextManifest>(await retained.ManifestAsync(Context(host, viewer, "repositoryId=repo&contextId=dashboard-retained")));
        var uri = Assert.Single(manifest.Data!.PinnedEntries).Uri;
        Assert.True(Read<DashboardRetainedContextSearch>(await retained.SearchAsync(Context(host, viewer, "repositoryId=repo&contextId=dashboard-retained&query=private"))).Ok);
        Assert.True(Read<DashboardRetainedContextPart>(await retained.PartAsync(Context(host, viewer, "uri=" + Uri.EscapeDataString(uri)))).Ok);
        Assert.Equal(RelayErrorCodes.RepositoryDenied, Read<DashboardRetainedContextList>(await retained.ListAsync(Context(host, viewer, "repositoryId=unconfigured"))).Code);

        // A relay bearer/session is not a dashboard viewer credential.
        using var unauthenticated = await owner.GetAsync("/dashboard/api/retained-context/part?uri=" + Uri.EscapeDataString(uri));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        // Reuse the same authenticated principal directly, bypassing cookie middleware, to exercise the service's own revalidation.
        host.Services.GetRequiredService<IConfiguration>()["Relay:Dashboard:ViewerPrincipalIds:0"] = "revoked";
        AssertDenied(Read<DashboardRetainedContextList>(await retained.ListAsync(Context(host, viewer, "repositoryId=repo"))));
        AssertDenied(Read<DashboardRetainedContextManifest>(await retained.ManifestAsync(Context(host, viewer, "repositoryId=repo&contextId=dashboard-retained"))));
        AssertDenied(Read<DashboardRetainedContextSearch>(await retained.SearchAsync(Context(host, viewer, "repositoryId=repo&contextId=dashboard-retained&query=private"))));
        AssertDenied(Read<DashboardRetainedContextPart>(await retained.PartAsync(Context(host, viewer, "uri=" + Uri.EscapeDataString(uri)))));
    }

    [Fact]
    public async Task Explicit_entry_reads_preserve_exact_text_and_reject_stale_versions_or_generations()
    {
        await using var host = Host();
        using var owner = host.AuthorizedClient();
        var session = await Open(host, owner, "dashboard-exact");
        const string exact = "retainedmarker <script>alert('literal')</script>\r\n  code\twith whitespace 😀\n";
        Assert.True((await Save(owner, session, 0, exact)).Ok);
        var retained = host.Services.GetRequiredService<DashboardRetainedContext>();
        var viewer = host.Services.GetRequiredService<DashboardSecurity>().Exchange(RelayHostFixture.Token, DateTimeOffset.UtcNow)!;
        var manifest = Read<DashboardRetainedContextManifest>(await retained.ManifestAsync(Context(host, viewer, "repositoryId=repo&contextId=dashboard-retained")));
        var uri = Assert.Single(manifest.Data!.PinnedEntries).Uri;
        var observed = Read<DashboardRetainedContextPart>(await retained.PartAsync(Context(host, viewer, "uri=" + Uri.EscapeDataString(uri))));
        Assert.True(observed.Ok);
        Assert.Equal(exact, observed.Data!.Text);
        Assert.Equal(uri, observed.Data.Uri);
        Assert.Equal(manifest.Data.Generation, observed.Data.Generation);
        Assert.Equal(1, observed.Data.Version);
        Assert.Equal(0, observed.Data.Part);
        Assert.Equal(1, observed.Data.PartCount);

        var noExactAddress = Read<DashboardRetainedContextPart>(await retained.PartAsync(Context(host, viewer, "uri=" + Uri.EscapeDataString(manifest.Data.Uri))));
        Assert.Equal(RelayErrorCodes.InvalidArguments, noExactAddress.Code);
        var wrongGeneration = SessionContextResourceUri.PartUri("repo", "dashboard-retained", observed.Data.EntryId, observed.Data.Version, 0, Guid.NewGuid().ToString("N"));
        var wrongIncarnation = Read<DashboardRetainedContextPart>(await retained.PartAsync(Context(host, viewer, "uri=" + Uri.EscapeDataString(wrongGeneration))));
        Assert.Equal(SessionContextLimits.VersionUnavailable, wrongIncarnation.Code);
        Assert.Null(wrongIncarnation.Data);

        Assert.True((await Save(owner, session, 1, "replacement must not satisfy an old address")).Ok);
        var stale = Read<DashboardRetainedContextPart>(await retained.PartAsync(Context(host, viewer, "uri=" + Uri.EscapeDataString(uri))));
        Assert.Equal(SessionContextLimits.VersionUnavailable, stale.Code);
        Assert.Null(stale.Data);
    }

    private static RelayHostFixture Host() => new(settings: new Dictionary<string, string?>
    {
        ["Relay:Clients:0:IsAdministrator"] = "true", ["Relay:Dashboard:ViewerPrincipalIds:0"] = "test-user"
    });

    private static DefaultHttpContext Context(RelayHostFixture host, ClaimsPrincipal viewer, string query) => new()
    {
        User = viewer, RequestServices = host.Services,
        Request = { Scheme = "https", QueryString = new QueryString("?" + query) }
    };

    private static async Task<CoCern.Domain.RelayReply> Save(HttpClient owner, JsonObject session, long expectedVersion, string text)
    {
        var patch = Patch("replaced");
        patch["upserts"]![0]!["text"] = text;
        return await Call(owner, RelayTools.SessionContextPut, Envelope(session, new JsonObject
        {
            ["contextId"] = "dashboard-retained", ["expectedVersion"] = expectedVersion,
            ["requestId"] = "dashboard-save-" + expectedVersion, ["patch"] = patch
        }));
    }

    private static DashboardRetainedContextReply<T> Read<T>(IResult result) where T : class =>
        Assert.IsType<DashboardRetainedContextReply<T>>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);

    private static void AssertDenied<T>(DashboardRetainedContextReply<T> reply) where T : class
    {
        Assert.False(reply.Ok);
        Assert.Equal(RelayErrorCodes.RepositoryDenied, reply.Code);
        Assert.Null(reply.Data);
        Assert.Empty(reply.RepositoryId);
    }
}
