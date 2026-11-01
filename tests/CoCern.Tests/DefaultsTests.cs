using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CoCern.Analytics;
using CoCern.Contracts;
using CoCern.Core;
using CoCern.Host;
using CoCern.Storage;
using Xunit;

namespace CoCern.Tests;

/// <summary>Every feature is on out of the box, and being on is safe with no configuration.</summary>
public sealed class DefaultsTests
{
    private const string AdminToken = "defaults-admin-test-secret-not-production";

    /// <summary>Only an administrator client: no feature setting at all.</summary>
    private static Dictionary<string, string?> AdminOnly(Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Relay:Clients:1:Token"] = AdminToken, ["Relay:Clients:1:PrincipalId"] = "defaults-admin", ["Relay:Clients:1:IsAdministrator"] = "true", ["Relay:Clients:1:Repositories:0"] = "repo"
        };
        foreach (var (key, value) in extra ?? []) settings[key] = value;
        return settings;
    }

    [Fact]
    public async Task A_hub_with_default_settings_starts_with_every_feature_on_and_stays_quiet()
    {
        // Only the extension folder is set: the bundled one covers Windows x64, and these tests also run elsewhere.
        await using var host = new RelayHostFixture(settings: AdminOnly(new() { ["Relay:Analytics:ExtensionDirectory"] = await AnalyticsTestExtension.DirectoryAsync() }));
        using var client = host.AuthorizedClient();
        using var admin = Admin(host);
        var services = host.Services;

        // Analytics, with its snapshots from the bundled extension and the anomaly monitor with the manager alerts.
        Assert.True(services.GetRequiredService<AnalyticsSettings>().Enabled);
        var analytics = Assert.IsType<AnalyticsService>(services.GetService<IAnalyticsQueries>());
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "duckdb_extensions"), new AnalyticsOptions().ExtensionDirectory);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (analytics.SnapshotAt is null && DateTimeOffset.UtcNow < deadline) await Task.Delay(50);
        Assert.NotNull(analytics.SnapshotAt);
        Assert.True(services.GetRequiredService<AnomalyOptions>() is { Enabled: true, Alerts: true });
        // Ad hoc SQL, still for administrators only.
        var sql = await Tool(admin, "analytics_query", new { repositoryId = "repo", name = "sql", sql = "SELECT 42 AS answer" });
        Assert.Equal((HttpStatusCode.OK, "Ok"), (sql.Status, sql.Code));
        Assert.Equal((HttpStatusCode.Forbidden, "AdministratorRequired"), Status(await Tool(client, "analytics_query", new { repositoryId = "repo", name = "sql", sql = "SELECT 1" })));
        // Standing orders.
        Assert.True(services.GetRequiredService<OrderOptions>().Enabled);
        // The doc cache, which with no repositories configured does nothing and says so.
        Assert.True(services.GetRequiredService<DocCacheSettings>() is { Enabled: true, Repositories.Count: 0 });
        Assert.Equal((HttpStatusCode.BadRequest, "NoRepositoriesConfigured"), Status(await Tool(client, "doc_search", new { repositoryId = "repo", query = "claim" })));
        // Tips, alerts and team rules.
        Assert.True(services.GetRequiredService<FeedbackOptions>() is { Tips: true, ProposeRules: true });
        // Every tool's budget is enforced except the doc cache's, which keeps its own.
        var tokens = services.GetRequiredService<TokenOptions>();
        Assert.True(tokens.Enabled);
        Assert.Equal(RelayToolCatalog.All.Select(tool => tool.Name).Where(name => !name.StartsWith("doc_", StringComparison.Ordinal)).Order(), tokens.EnforceTools.Order());
        Assert.All(RelayToolCatalog.All.Where(tool => tokens.Enforces(tool.Name)), tool => Assert.True(tool.TokenBudget >= RelayLimits.MaxPayloadBytes / RelayLimits.BudgetBytesPerToken, tool.Name));
        Assert.All(RelayToolCatalog.All.Where(tool => tool.Name is RelayTools.DocSearch or RelayTools.DocSection), tool => Assert.Equal(RelayLimits.MaxDocReplyTokens, tool.TokenBudget));
        // The older multi-PC features stay off: discovery, and the network listener beyond loopback.
        var configuration = services.GetRequiredService<IConfiguration>();
        Assert.False(DiscoverySettings.Enabled(configuration));
        Assert.False(HubNetwork.Enabled(configuration));

        // A fresh hub raises nothing on its own: a manager and a worker do ordinary work and are offered nothing.
        var manager = Data(await host.Call(client, "session_open", new { repositoryId = "repo", requestId = "quiet-manager", role = "manager" }));
        var worker = Data(await host.Call(client, "session_open", new { repositoryId = "repo", requestId = "quiet-worker", role = "worker" }));
        object Handle(JsonElement session, object? extra = null)
        {
            var args = new Dictionary<string, object?> { ["repositoryId"] = "repo", ["sessionId"] = session.GetProperty("sessionId").GetString(), ["sessionToken"] = session.GetProperty("sessionToken").GetString() };
            foreach (var property in JsonSerializer.SerializeToElement(extra ?? new { }).EnumerateObject()) args[property.Name] = property.Value;
            return args;
        }
        Data(await host.Call(client, "task_update", Handle(worker, new { requestId = "quiet-task", taskId = "T-1", expectedVersion = 0, notes = "first task" })));
        Data(await host.Call(client, "task_get", Handle(worker, new { taskId = "T-1" })));
        Data(await host.Call(client, "check_inbox", Handle(manager)));
        // Give the anomaly monitor's first check (due at once) and the feedback sweep time to run.
        await Task.Delay(2_000);
        foreach (var session in new[] { manager, worker })
            Assert.Equal(0, Data(await host.Call(client, "get_status", Handle(session, new { attentionDetail = true }))).GetProperty("attention").GetProperty("actionable").GetInt32());
        Assert.False(Data(await host.Call(client, "check_inbox", Handle(manager))).TryGetProperty("attention", out _));
    }

    [Fact]
    public async Task A_missing_or_damaged_DuckDB_extension_leaves_the_hub_running_with_one_warning_and_AnalyticsUnavailable()
    {
        var root = Path.Combine(Path.GetTempPath(), "cocern-defaults-" + Guid.NewGuid().ToString("N"));
        var empty = Directory.CreateDirectory(Path.Combine(root, "empty")).FullName;
        try
        {
            var good = await AnalyticsTestExtension.DirectoryAsync();
            Assert.Null(AnalyticsExtension.Problem(good));
            Assert.Contains("is not in", AnalyticsExtension.Problem(empty));
            // Only windows_amd64 is bundled: on any other platform the bundled folder has no extension for it.
            var bundled = AnalyticsExtension.Problem(AnalyticsOptions.DefaultExtensionDirectory);
            if (OperatingSystem.IsWindows() && System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.X64) Assert.Null(bundled);
            else Assert.Contains("is not in", bundled);
            // A copy of a working extension, listed in a manifest with its real sum, then one byte changed: it fails its SHA-256 sum
            // before DuckDB is asked to load it.
            var damaged = Path.Combine(root, "damaged");
            var original = Directory.EnumerateFiles(good, "*.duckdb_extension", SearchOption.AllDirectories).First();
            var relative = Path.GetRelativePath(good, original).Replace('\\', '/');
            var extension = Path.Combine(damaged, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(extension)!);
            File.Copy(original, extension);
            await using (var stream = File.OpenRead(extension))
                File.WriteAllText(Path.Combine(damaged, AnalyticsExtension.ManifestName), Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant() + "  " + relative + "\n");
            Assert.Null(AnalyticsExtension.Problem(damaged));
            await using (var stream = File.Open(extension, FileMode.Open)) { stream.Position = 1_000; var value = stream.ReadByte(); stream.Position = 1_000; stream.WriteByte((byte)(value ^ 0xFF)); }
            Assert.Contains("SHA-256", AnalyticsExtension.Problem(damaged));

            // An empty folder, a damaged file, and (off Windows x64) the bundled folder with no file for this platform.
            var cases = new List<string?> { empty, damaged };
            if (bundled is not null) cases.Add(null);
            foreach (var setting in cases)
            {
                var directory = setting ?? AnalyticsOptions.DefaultExtensionDirectory;
                await using var host = new RelayHostFixture(settings: AdminOnly(setting is null ? null : new() { ["Relay:Analytics:ExtensionDirectory"] = setting }));
                using var client = host.AuthorizedClient();
                using var admin = Admin(host);
                // The hub starts and serves calls.
                Data(await host.Call(client, "session_open", new { repositoryId = "repo", requestId = "no-extension", role = "manager" }));
                // The snapshot service stops after its one check instead of retrying every interval.
                var service = host.Services.GetServices<IHostedService>().OfType<AnalyticsSnapshotService>().Single();
                await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30));
                var refused = await Tool(admin, "analytics_query", new { repositoryId = "repo", name = "noisiest_tools" });
                Assert.Equal((HttpStatusCode.BadRequest, "AnalyticsUnavailable"), (refused.Status, refused.Code));
                Assert.Contains(directory, refused.Data.GetProperty("message").GetString());
                Assert.Null(host.Services.GetRequiredService<AnalyticsService>().SnapshotAt);
                var snapshots = host.Services.GetRequiredService<AnalyticsSettings>().Directory;
                Assert.False(Directory.Exists(snapshots) && Directory.EnumerateFiles(snapshots, "snapshot-*").Any());

                // One clear warning, logged once.
                var logger = new ListLogger<AnalyticsSnapshotService>();
                var again = new AnalyticsSnapshotService(host.Services.GetRequiredService<SqliteStore>(), host.Services.GetRequiredService<AnalyticsSettings>(), host.Services, logger);
                await again.StartAsync(CancellationToken.None);
                await again.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30));
                await again.StopAsync(CancellationToken.None);
                var warning = Assert.Single(logger.Entries);
                Assert.Equal(LogLevel.Warning, warning.Level);
                Assert.StartsWith("Analytics is unavailable:", warning.Message);
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void The_bundled_DuckDB_extensions_match_their_manifest_and_carry_the_license()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "CoCern.slnx"))) repository = repository.Parent;
        Assert.NotNull(repository);
        var bundle = Path.Combine(repository!.FullName, "extensions", "duckdb");
        var sums = File.ReadAllLines(Path.Combine(bundle, AnalyticsExtension.ManifestName)).Where(line => line.Length > 66).ToDictionary(line => line[66..].Trim(), line => line[..64]);
        var packed = sums.Keys.Where(path => path.EndsWith(".gz", StringComparison.Ordinal)).ToArray();
        // Only Windows x64 is bundled; other platforms install the extension with --install-analytics-extension.
        Assert.Equal(["windows_amd64"], packed.Select(path => path.Split('/')[1]).ToArray());
        foreach (var path in packed)
        {
            using var stream = File.OpenRead(Path.Combine(bundle, path));
            Assert.Equal(sums[path], Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
            // Each one also lists the sum of the file it unpacks to, which the build and the hub check.
            Assert.Matches("^[0-9a-f]{64}$", sums[path[..^3]]);
        }
        Assert.Contains("MIT License", File.ReadAllText(Path.Combine(bundle, "LICENSE-duckdb.txt")));
        // The build unpacked this machine's extension next to the binaries, with the manifest and the license.
        Assert.True(File.Exists(Path.Combine(AnalyticsOptions.DefaultExtensionDirectory, AnalyticsExtension.ManifestName)));
        Assert.True(File.Exists(Path.Combine(AnalyticsOptions.DefaultExtensionDirectory, "LICENSE-duckdb.txt")));
    }

    private static HttpClient Admin(RelayHostFixture host)
    {
        var client = host.AuthorizedClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        return client;
    }

    private static async Task<(HttpStatusCode Status, string Code, JsonElement Data)> Tool(HttpClient client, string tool, object arguments)
    {
        using var response = await client.PostAsJsonAsync("/api/tools/" + tool, arguments);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (response.StatusCode, body.GetProperty("code").GetString()!, JsonDocument.Parse(body.GetProperty("dataJson").GetString()!).RootElement.Clone());
    }

    private static (HttpStatusCode, string) Status((HttpStatusCode Status, string Code, JsonElement Data) reply) => (reply.Status, reply.Code);

    private static JsonElement Data(JsonElement reply)
    {
        Assert.True(reply.GetProperty("ok").GetBoolean(), reply.ToString());
        return JsonDocument.Parse(reply.GetProperty("dataJson").GetString()!).RootElement.Clone();
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Entries.Enqueue((logLevel, formatter(state, exception)));
    }
}
