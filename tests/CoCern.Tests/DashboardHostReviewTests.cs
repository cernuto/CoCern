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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Collection_remains_idle_without_a_connected_viewer_even_when_http_snapshots_are_read(bool enabled)
    {
        var source = new CountingObservationSource();
        await using var host = CreateHost(source, enabled);
        using var browser = Browser(host);
        if (enabled)
            (await browser.PostAsJsonAsync("/dashboard/api/login", new { token = RelayHostFixture.Token })).EnsureSuccessStatusCode();
        else
            Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsJsonAsync("/dashboard/api/login", new { token = RelayHostFixture.Token })).StatusCode);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (enabled)
                Assert.Equal(0, (await browser.GetFromJsonAsync<DashboardSnapshot>("/dashboard/api/snapshot"))!.Revision);
            await Task.Delay(500);
        }
        Assert.Equal(0, source.Observations);
        Assert.Equal(0, source.DatabaseReads);
    }

    [Fact]
    public async Task Connected_viewers_share_one_sampler_and_collection_stops_after_the_last_disconnect()
    {
        var source = new CountingObservationSource();
        await using var host = CreateHost(source);
        using var browser = Browser(host);
        var login = await browser.PostAsJsonAsync("/dashboard/api/login", new { token = RelayHostFixture.Token });
        login.EnsureSuccessStatusCode();
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';')[0];
        await using var first = Viewer(host, cookie);
        await using var second = Viewer(host, cookie);
        await first.StartAsync();
        await second.StartAsync();
        await source.FirstObservation.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(host.Services.GetRequiredService<RelayApplication>().IncarnationId, source.LastIncarnationId);
        await Task.Delay(2250);
        // A loaded test run can start the second tick late; wait for it. The upper bound is what proves the viewers share one sampler.
        for (var attempt = 0; attempt < 100 && source.Observations < 2; attempt++) await Task.Delay(50);
        Assert.InRange(source.Observations, 2, 3);
        Assert.Equal(1, source.DatabaseReads);
        await first.StopAsync();
        await second.StopAsync();
        await Task.Delay(100);
        var count = source.Observations;
        await Task.Delay(2250);
        Assert.Equal(count, source.Observations);
        Assert.Equal(1, source.DatabaseReads);
        await first.StartAsync();
        for (var attempt = 0; attempt < 50 && source.Observations == count; attempt++) await Task.Delay(50);
        Assert.True(source.Observations > count);
        Assert.Equal(1, source.DatabaseReads);
    }

    [Fact]
    public async Task Oversized_database_metadata_has_a_bounded_explicitly_truncated_fallback()
    {
        var source = new CountingObservationSource { DatabaseWarning = new string('\u754c', 100_000) };
        await using var host = CreateHost(source);
        using var browser = Browser(host);
        var login = await browser.PostAsJsonAsync("/dashboard/api/login", new { token = RelayHostFixture.Token });
        login.EnsureSuccessStatusCode();
        await using var viewer = Viewer(host, Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';')[0]);
        await viewer.StartAsync();
        DashboardSnapshot? snapshot = null;
        byte[] bytes = [];
        for (var attempt = 0; attempt < 50; attempt++)
        {
            bytes = await browser.GetByteArrayAsync("/dashboard/api/snapshot");
            snapshot = System.Text.Json.JsonSerializer.Deserialize<DashboardSnapshot>(bytes, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            if (snapshot!.Revision > 0) break;
            await Task.Delay(50);
        }
        Assert.True(snapshot!.Revision > 0);
        Assert.True(bytes.Length <= 255 * 1024);
        Assert.True(snapshot.Database.Truncated);
        Assert.Contains(snapshot.Warnings, warning => warning.Contains("trimmed", StringComparison.Ordinal));
    }

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
            var security = new DashboardSecurity(configuration, new TestEnvironment(directory));
            var principal = Assert.IsType<System.Security.Claims.ClaimsPrincipal>(security.Exchange("first-test-token", DateTimeOffset.UtcNow));
            await File.WriteAllTextAsync(path, "second-test-token");
            Assert.True(security.IsValid(principal, DateTimeOffset.UtcNow));
            await Task.Delay(1100);
            Assert.False(security.IsValid(principal, DateTimeOffset.UtcNow));
            principal = Assert.IsType<System.Security.Claims.ClaimsPrincipal>(security.Exchange("second-test-token", DateTimeOffset.UtcNow));
            File.Delete(path);
            await Task.Delay(1100);
            Assert.False(security.IsValid(principal, DateTimeOffset.UtcNow));
            Assert.Null(security.Exchange("second-test-token", DateTimeOffset.UtcNow));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Token_file_rotation_or_grant_removal_closes_the_stream_within_five_seconds(bool removeGrant)
    {
        var directory = Path.Combine(Path.GetTempPath(), "cocern-dashboard-revoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "viewer.token");
        try
        {
            await File.WriteAllTextAsync(path, RelayHostFixture.Token);
            var source = new CountingObservationSource();
            await using var host = new RelayHostFixture(settings: new Dictionary<string, string?>
            {
                ["Relay:Clients:0:Token"] = null,
                ["Relay:Clients:0:TokenFile"] = path,
                ["Relay:Clients:0:IsAdministrator"] = "true",
                ["Relay:Dashboard:ViewerPrincipalIds:0"] = "test-user"
            }, configureServices: services => services.AddSingleton<IRelayObservationSource>(source));
            using var browser = Browser(host);
            var login = await browser.PostAsJsonAsync("/dashboard/api/login", new { token = RelayHostFixture.Token });
            login.EnsureSuccessStatusCode();
            await using var viewer = Viewer(host, Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';')[0]);
            var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            viewer.On<DashboardSnapshot>("snapshot", snapshot => { if (snapshot.Revision > 0) frame.TrySetResult(); });
            viewer.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
            await viewer.StartAsync();
            await frame.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var count = source.Observations;
            if (removeGrant)
                host.Services.GetRequiredService<IConfiguration>()["Relay:Dashboard:ViewerPrincipalIds:0"] = null;
            else
                await File.WriteAllTextAsync(path, "rotated-test-token");
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/dashboard/api/auth")).StatusCode);
            if (removeGrant) Assert.Equal(count, source.Observations);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Iteration_failure_keeps_the_host_alive_and_recovers_on_the_next_sample()
    {
        var source = new InvalidFirstFrameSource();
        await using var host = new RelayHostFixture(settings: new Dictionary<string, string?>
        {
            ["Relay:Clients:0:IsAdministrator"] = "true",
            ["Relay:Dashboard:ViewerPrincipalIds:0"] = "test-user"
        }, configureServices: services => services.AddSingleton<IRelayObservationSource>(source));
        using var browser = Browser(host);
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stoppedRegistration = lifetime.ApplicationStopping.Register(() => stopping.TrySetResult());
        var login = await browser.PostAsJsonAsync("/dashboard/api/login", new { token = RelayHostFixture.Token });
        login.EnsureSuccessStatusCode();
        await using var viewer = Viewer(host, Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';')[0]);
        var recovered = new TaskCompletionSource<DashboardSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewer.On<DashboardSnapshot>("snapshot", snapshot => { if (snapshot.Revision > 0) recovered.TrySetResult(snapshot); });
        var started = System.Diagnostics.Stopwatch.StartNew();
        await viewer.StartAsync();
        var completed = await Task.WhenAny(recovered.Task, stopping.Task).WaitAsync(TimeSpan.FromSeconds(6));
        Assert.Same(recovered.Task, completed);
        var snapshot = await recovered.Task;
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
        Assert.True(started.Elapsed >= TimeSpan.FromMilliseconds(1500));
        Assert.InRange(source.Observations, 2, 3);
        Assert.Contains(snapshot.Logs, log => log.Category == "Observer" && log.Level == "Warning");
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/health")).StatusCode);
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
