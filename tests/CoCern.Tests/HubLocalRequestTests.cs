using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoCern.Adapter;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Host;
using CoCern.Host.Dashboard;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CoCern.Tests;

public sealed class HubLocalRequestTests
{
    [Fact]
    public async Task Native_resolution_uses_discovered_origins_and_authorized_clone_less_hints_without_remote_path_authority()
    {
        var root = Path.Combine(Path.GetTempPath(), "cocern-native-resolution-" + Guid.NewGuid().ToString("N"));
        var checkout = Path.Combine(root, "repo");
        Directory.CreateDirectory(Path.Combine(checkout, ".git"));
        try
        {
            var listener = new HubListener { Https = (5273, new string('a', 64)) };
            using var services = new ServiceCollection().AddSingleton(listener).BuildServiceProvider();
            var context = new DefaultHttpContext { RequestServices = services };
            context.Connection.LocalPort = 5273;
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            var transport = new HttpContextAccessor { HttpContext = context };
            var settings = new DocCacheSettings(true, 30, 30, 100, []) { Roots = [root] };
            var registry = new DocRepositoryRegistry(settings, new DiscoveryGit(), NullLogger<DocRepositoryRegistry>.Instance, transport);
            await registry.ScanAsync(CancellationToken.None);
            Assert.Empty(settings.Repositories);
            Assert.Contains(registry.Repositories, repository => repository.Id == "repo");

            var caller = new RelayCaller("remote-node", false, ["repo", "clone-less"]);
            var origin = NativeGitOrigin.Canonical(CheckoutGit.Origin);
            Assert.Equal("repo", await registry.ResolveNativeAsync(caller, null, null, origin));
            Assert.Null(await registry.ResolveNativeAsync(caller, null, null, "https://example.test/another/repo", "repo"));
            Assert.Null(await registry.ResolveNativeAsync(caller, null, null, origin, "unauthorized"));
            var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(checkout)));
            Assert.Null(await registry.ResolveNativeAsync(caller, digest, null, null, "repo"));
            Assert.Equal("clone-less", await registry.ResolveNativeAsync(caller, digest, null, null, "CLONE-LESS"));
            Assert.Equal("clone-less", await registry.ResolveNativeAsync(caller with { Repositories = ["clone-less"] }, null, null, origin));

            Directory.CreateDirectory(Path.Combine(root, "repo2", ".git"));
            await registry.ScanAsync(CancellationToken.None);
            Assert.Null(await registry.ResolveNativeAsync(caller with { Repositories = ["repo", "repo2"] }, null, null, origin));

            var disabled = new DocRepositoryRegistry(settings with { Enabled = false, Roots = [] }, new DiscoveryGit(),
                NullLogger<DocRepositoryRegistry>.Instance, transport);
            var tools = DocCacheTool.OffWithNativeResolution(disabled.ResolveNativeAsync);
            Assert.False(DocCacheTool.IsEnabled(tools));
            Assert.Equal("clone-less", await tools.ResolveNativeAsync(caller, null, null, null, "clone-less"));
            Assert.Null(await tools.ResolveNativeAsync(caller, null, null, null, "unauthorized"));
            Assert.Equal(RelayErrorCodes.DocCacheUnavailable, (await tools.SearchAsync(new())).Code);
            Assert.Null(await tools.LearnAsync("clone-less", checkout));
            var preflight = new NativeCheckout(checkout, null, null).Preflight(false, "clone-less").GetProperty("nativePreflight");
            Assert.True(NativeGitOrigin.ValidCheckoutEvidence(preflight));
            Assert.Equal("clone-less", preflight.GetProperty("repositoryId").GetString());
            Assert.Single(preflight.EnumerateObject());
            var noIdentity = new NativeCheckout(checkout, null, null).Preflight(false).GetProperty("nativePreflight");
            Assert.Empty(noIdentity.EnumerateObject());
            Assert.True(NativeGitOrigin.ValidCheckoutEvidence(noIdentity));
            Assert.Equal("clone-less", await registry.ResolveNativeAsync(caller with { Repositories = ["clone-less"] }, null, null, null));
            Assert.Null(await registry.ResolveNativeAsync(caller, null, null, null));
            Assert.Null(await registry.ResolveNativeAsync(caller with { Repositories = [] }, null, null, null));
            Assert.Null(await registry.ResolveNativeAsync(caller with { IsAdministrator = true, Repositories = ["clone-less"] }, null, null, null));
            Assert.Null(await registry.ResolveNativeAsync(caller with { Repositories = ["repo"] }, null, null, null));
            Assert.False(NativeGitOrigin.ValidCheckoutEvidence(JsonSerializer.SerializeToElement(new { repositoryId = "clone-less\n" })));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Network_listener_loopback_cannot_grant_local_dashboard_access_or_checkout_identity()
    {
        var listener = new HubListener { Https = (5273, new string('a', 64)), Local = (5272, new string('a', 64)) };
        using var services = new ServiceCollection().AddSingleton(listener).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Connection.RemoteIpAddress = IPAddress.Loopback.MapToIPv6();
        context.Connection.LocalPort = 5273;
        // An apparent local Host cannot override the actual network listener receiving a forwarded TCP connection.
        context.Request.Host = new HostString("localhost", 5272);
        Assert.False(DashboardConnect.OnThisPc(context));
        Assert.False(DashboardSecurity.LocalBrowser(context));
        context.Connection.LocalPort = 0;
        Assert.False(DashboardConnect.OnThisPc(context));
        context.Connection.LocalPort = 5273;

        var checkout = Path.Combine(Path.GetTempPath(), "cocern-local-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(checkout, ".git"));
        try
        {
            var repository = new DocRepository("repo", checkout, "main", "origin", [], [], null);
            var registry = new DocRepositoryRegistry(new DocCacheSettings(true, 30, 30, 100, [repository]), new CheckoutGit(checkout),
                NullLogger<DocRepositoryRegistry>.Instance, new HttpContextAccessor { HttpContext = context });
            var caller = new RelayCaller("remote-node", false, ["repo"]);
            var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(checkout)));
            Assert.Null(await registry.ResolveNativeAsync(caller, digest, null, null));
            Assert.Equal("repo", await registry.ResolveNativeAsync(caller, null, null, NativeGitOrigin.Canonical(CheckoutGit.Origin)));

            context.Connection.LocalPort = 5272;
            Assert.True(DashboardConnect.OnThisPc(context));
            Assert.True(DashboardSecurity.LocalBrowser(context));
            Assert.Equal("repo", await registry.ResolveNativeAsync(caller, digest, null, null));
            context.Connection.LocalPort = 5271;
            Assert.True(DashboardConnect.OnThisPc(context));
            context.Request.Headers["Forwarded"] = "for=127.0.0.1";
            Assert.False(DashboardSecurity.LocalBrowser(context));
        }
        finally { Directory.Delete(checkout, true); }
    }

    private sealed class CheckoutGit(string checkout) : GitCli
    {
        internal const string Origin = "https://example.test/team/repository.git";

        public override Task<GitResult> RunAsync(string directory, IReadOnlyList<string> arguments, int maxOutputBytes,
            IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken) =>
            Task.FromResult(new GitResult(0, Encoding.UTF8.GetBytes(arguments[0] == "config" ? Origin : checkout), false, ""));
    }

    private sealed class DiscoveryGit : GitCli
    {
        public override Task<GitResult> RunAsync(string directory, IReadOnlyList<string> arguments, int maxOutputBytes,
            IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken) =>
            Task.FromResult(new GitResult(0, Encoding.UTF8.GetBytes(arguments[0] switch
            {
                "symbolic-ref" => "main",
                "config" => CheckoutGit.Origin,
                _ => directory
            }), false, ""));
    }
}
