using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using CoCern.Contracts.Dashboard;
using CoCern.Core;
using CoCern.Host.Dashboard;
using Xunit;

namespace CoCern.Tests;

public sealed class DashboardHostReviewTests
{

    [Fact]
    public async Task Login_attempts_are_rate_limited_in_independent_remote_address_buckets()
    {
        await using var host = new RelayHostFixture(configureServices: services =>
            services.AddSingleton<IStartupFilter>(new TestAddressStartupFilter()));
        using var browser = Browser(host);
        for (var attempt = 0; attempt < 6; attempt++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginFrom("198.51.100.10")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginFrom("198.51.100.10")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginFrom("198.51.100.11")).StatusCode);

        Task<HttpResponseMessage> LoginFrom(string address)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/dashboard/api/login")
            {
                Content = JsonContent.Create(new { token = "invalid-test-token" })
            };
            request.Headers.Add("X-Test-Remote-Address", address);
            return browser.SendAsync(request);
        }
    }

    [Fact]
    public async Task Token_file_enrollment_is_cached_briefly_then_rotation_and_read_failure_revoke_the_cookie()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cocern-dashboard-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "viewer.token");
        try
        {
            await File.WriteAllTextAsync(path, "first-test-token");
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Relay:Clients:0:PrincipalId"] = "viewer",
                ["Relay:Clients:0:TokenFile"] = path,
                ["Relay:Clients:0:IsAdministrator"] = "true",
                ["Relay:Dashboard:ViewerPrincipalIds:0"] = "viewer"
            }).Build();
            long ticks = 10_000;
            var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var security = new DashboardSecurity(configuration, new TestEnvironment(directory), () => ticks);
            var principal = Assert.IsType<System.Security.Claims.ClaimsPrincipal>(security.Exchange("first-test-token", now));
            await File.WriteAllTextAsync(path, "second-test-token");
            ticks = 10_999;
            Assert.True(security.IsValid(principal, now));
            Assert.Null(security.Exchange("second-test-token", now));
            ticks = 11_000;
            Assert.False(security.IsValid(principal, now));
            Assert.Null(security.Exchange("first-test-token", now));
            principal = Assert.IsType<System.Security.Claims.ClaimsPrincipal>(security.Exchange("second-test-token", now));
            var expiring = Assert.IsType<System.Security.Claims.ClaimsPrincipal>(security.Exchange("second-test-token", now, now.AddSeconds(1)));
            Assert.True(security.IsValid(expiring, now.AddMilliseconds(999)));
            Assert.False(security.IsValid(expiring, now.AddSeconds(1)));
            File.Delete(path);
            ticks = 11_999;
            Assert.True(security.IsValid(principal, now));
            ticks = 12_000;
            Assert.False(security.IsValid(principal, now));
            Assert.Null(security.Exchange("second-test-token", now));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static RelayHostFixture CreateHost(CountingObservationSource source, bool enabled = true) => new(
        settings: new Dictionary<string, string?>
        {
            ["Relay:Clients:0:IsAdministrator"] = "true",
            ["Relay:Dashboard:ViewerPrincipalIds:0"] = enabled ? "test-user" : null
        },
        configureServices: services => services.AddSingleton<IRelayObservationSource>(source));

    private static HttpClient Browser(RelayHostFixture host)
    {
        var browser = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        browser.DefaultRequestHeaders.Add("Origin", "https://localhost");
        return browser;
    }

    private static HubConnection Viewer(RelayHostFixture host, string cookie) =>
        new HubConnectionBuilder().WithUrl("https://localhost/dashboard/hub", options =>
        {
            options.Transports = HttpTransportType.LongPolling;
            options.HttpMessageHandlerFactory = _ => host.Server.CreateHandler();
            options.Headers["Cookie"] = cookie;
            options.Headers["Origin"] = "https://localhost";
        }).Build();

    private sealed class CountingObservationSource : IRelayObservationSource
    {
        private int observations;
        private int databaseReads;
        public int Observations => Volatile.Read(ref observations);
        public int DatabaseReads => Volatile.Read(ref databaseReads);
        public string? DatabaseWarning { get; init; }
        public string? LastIncarnationId { get; private set; }
        public TaskCompletionSource FirstObservation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<RelayObservation?> ReadObservationAsync(DateTimeOffset now, string incarnationId, CancellationToken cancellationToken = default)
        {
            LastIncarnationId = incarnationId;
            Interlocked.Increment(ref observations);
            FirstObservation.TrySetResult();
            return Task.FromResult<RelayObservation?>(new() { Warnings = [] });
        }
        public Task<DatabaseMetrics?> ReadDatabaseMetricsAsync(int rowCountLimit = 10_000, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref databaseReads);
            return Task.FromResult<DatabaseMetrics?>(new() { Warning = DatabaseWarning });
        }
    }

    private sealed class InvalidFirstFrameSource : IRelayObservationSource
    {
        private int observations;
        public int Observations => Volatile.Read(ref observations);
        public Task<RelayObservation?> ReadObservationAsync(DateTimeOffset now, string incarnationId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref observations);
            return Task.FromResult<RelayObservation?>(new() { Warnings = [] });
        }
        public Task<DatabaseMetrics?> ReadDatabaseMetricsAsync(int rowCountLimit = 10_000, CancellationToken cancellationToken = default) =>
            Task.FromResult<DatabaseMetrics?>(new() { Warning = new string('x', 300_000), Tables = null! });
    }

    private sealed class TestAddressStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, following) =>
            {
                if (IPAddress.TryParse(context.Request.Headers["X-Test-Remote-Address"], out var address))
                    context.Connection.RemoteIpAddress = address;
                return following(context);
            });
            next(app);
        };
    }

    private sealed class TestEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "CoCern.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
