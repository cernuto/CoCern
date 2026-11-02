using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CoCern.Host.Dashboard;
using Xunit;

namespace CoCern.Tests;

public sealed partial class DashboardSignInLinkTests
{
    private static readonly Dictionary<string, string?> Viewer = new()
    {
        ["Relay:Clients:0:IsAdministrator"] = "true", ["Relay:Dashboard:ViewerPrincipalIds:0"] = "test-user"
    };

    [Fact]
    public async Task The_logged_link_signs_in_once_from_this_pc_and_the_next_one_is_logged()
    {
        var log = new CapturedLog();
        await using var host = new RelayHostFixture(remoteAddress: IPAddress.Loopback, settings: Viewer, configureServices: services => services.AddSingleton<Serilog.Core.ILogEventSink>(log));
        using var browser = Browser(host);
        var first = log.LastCode();
        var signedIn = await browser.PostAsJsonAsync("/dashboard/api/login", new { link = first.ToLowerInvariant() });
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        Assert.Contains("test-user", await signedIn.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsJsonAsync("/dashboard/api/login", new { link = first })).StatusCode);
        var next = log.LastCode();
        Assert.NotEqual(first, next);
        Assert.Equal(HttpStatusCode.OK, (await browser.PostAsJsonAsync("/dashboard/api/login", new { link = next })).StatusCode);
    }

    [Fact]
    public async Task A_link_is_refused_from_another_pc()
    {
        var log = new CapturedLog();
        await using var host = new RelayHostFixture(remoteAddress: IPAddress.Parse("192.168.1.20"), settings: Viewer, configureServices: services => services.AddSingleton<Serilog.Core.ILogEventSink>(log));
        using var browser = Browser(host);
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.PostAsJsonAsync("/dashboard/api/login", new { link = log.LastCode() })).StatusCode);
    }

    [Fact]
    public void A_spent_or_expired_code_never_redeems()
    {
        var log = new CapturedLog();
        var link = new DashboardSignInLink(new LoggerAdapter(log));
        var now = DateTimeOffset.UtcNow;
        link.Issue(now);
        var code = log.LastCode();
        Assert.False(link.Redeem(code, now + DashboardSignInLink.Lifetime));
        link.Issue(now);
        code = log.LastCode();
        Assert.False(link.Redeem(code + "X", now));
        Assert.True(link.Redeem(code, now));
        Assert.False(link.Redeem(code, now));
    }

    [Fact]
    public async Task A_missing_token_file_is_created_owner_only_at_startup_and_signs_in()
    {
        var folder = Path.Combine(Path.GetTempPath(), "cocern-token-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "data", "local-client-token");
        try
        {
            var settings = new Dictionary<string, string?>(Viewer) { ["Relay:Clients:0:Token"] = null, ["Relay:Clients:0:TokenFile"] = path };
            await using var host = new RelayHostFixture(settings: settings);
            using var browser = Browser(host);
            var token = File.ReadAllText(path);
            Assert.Matches("^[0-9A-F]{64}$", token);
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Equal(HttpStatusCode.OK, (await browser.PostAsJsonAsync("/dashboard/api/login", new { token })).StatusCode);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }

    [Theory]
    [InlineData("http://127.0.0.1:5271", "http://127.0.0.1:5271/dashboard/")]
    [InlineData("https://0.0.0.0:5273;http://127.0.0.1:5271", "http://127.0.0.1:5271/dashboard/")]
    [InlineData("http://+:5271", "http://localhost:5271/dashboard/")]
    [InlineData("http://192.168.1.5:5271", null)]
    public void The_link_uses_a_loopback_address_the_hub_serves(string addresses, string? expected) =>
        Assert.Equal(expected, DashboardSignInLink.LoopbackUrl(addresses.Split(';')));

    private static HttpClient Browser(RelayHostFixture host)
    {
        var browser = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        browser.DefaultRequestHeaders.Add("Origin", "https://localhost");
        return browser;
    }

    private sealed class LoggerAdapter(ILogger inner) : ILogger<DashboardSignInLink>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => inner.Log(logLevel, eventId, state, exception, formatter);
    }

    private sealed partial class CapturedLog : ILoggerProvider, ILogger, Serilog.Core.ILogEventSink
    {
        private readonly ConcurrentQueue<string> lines = new();
        public void Emit(Serilog.Events.LogEvent logEvent) => lines.Enqueue(logEvent.RenderMessage());
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => lines.Enqueue(formatter(state, exception));
        public void Dispose() { }
        public string LastCode() => lines.Select(line => LinkCode().Match(line)).Last(match => match.Success).Groups[1].Value;
        [GeneratedRegex("/dashboard/#link=([A-Z0-9]{24})")] private static partial Regex LinkCode();
    }
}
