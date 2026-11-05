using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CoCern.Contracts;
using CoCern.Contracts.Dashboard;
using CoCern.Core;
using CoCern.Host;
using CoCern.Host.Dashboard;
using CoCern.Storage;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace CoCern.Tests;

/// <summary>The administrator tools through a real hub's pipeline: who may call them, from where, what they answer and what they leave behind.</summary>
public sealed class AdminToolsHostTests : IDisposable
{
    private const string Fingerprint = "AB12CD34AB12CD34AB12CD34AB12CD34AB12CD34AB12CD34AB12CD34AB12CD34";
    private const string WorkerToken = "admin-tools-fixture-worker-token";
    private static readonly IPAddress Remote = IPAddress.Parse("192.168.1.20");
    private readonly string root = Path.Combine(Path.GetTempPath(), "cocern-admin-tools-host-" + Guid.NewGuid().ToString("N"));
    private string Adapter => Path.Combine(root, "adapter", "CoCern.Adapter.dll");

    public AdminToolsHostTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Adapter)!);
        File.WriteAllText(Adapter, "");
    }

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    /// <summary>A hub whose first client is an administrator and whose second is a worker. Nothing here reads real settings, a real join code file or a real home.</summary>
    private RelayHostFixture Hub(IPAddress? remote, bool discovery = true, string? adapter = null, Action<IServiceCollection>? services = null, Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Relay:Clients:0:IsAdministrator"] = "true",
            ["Relay:Clients:1:Token"] = WorkerToken, ["Relay:Clients:1:PrincipalId"] = "worker-user", ["Relay:Clients:1:Repositories:0"] = "repo",
            ["Relay:Discovery:Enabled"] = discovery.ToString(), ["Relay:Connect:AdapterPath"] = adapter ?? Adapter
        };
        foreach (var (key, value) in extra ?? []) settings[key] = value;
        return new RelayHostFixture(databasePath: Path.Combine(root, "data-" + Guid.NewGuid().ToString("N"), "relay.db"), remoteAddress: remote, settings: settings, configureServices: services);
    }

    private static HttpClient Worker(RelayHostFixture host)
    {
        var client = host.AuthorizedClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", WorkerToken);
        return client;
    }

    /// <summary>Serves add_pc's HTTPS address, as the network listener does once Kestrel sets it up; a test server never does.</summary>
    private static void Listening(RelayHostFixture host) => host.Services.GetRequiredService<HubListener>().Https = (5273, Fingerprint);

    private static async Task<(HttpStatusCode Status, string Code, JsonElement Data, string Raw)> Post(HttpClient client, string tool, object arguments)
    {
        using var response = await client.PostAsJsonAsync("/api/tools/" + tool, arguments);
        var raw = await response.Content.ReadAsStringAsync();
        using var reply = JsonDocument.Parse(raw);
        var data = reply.RootElement.TryGetProperty("dataJson", out var text) ? JsonDocument.Parse(text.GetString()!).RootElement.Clone() : default;
        return (response.StatusCode, reply.RootElement.GetProperty("code").GetString()!, data, raw);
    }

    /// <summary>Stands in for running the adapter, which would edit this user's real app settings. It records each run and the admin choice it was given.</summary>
    private sealed class FakeConnect(DashboardConnectStatus? answer = null, Exception? failure = null)
    {
        public ConcurrentQueue<(string Client, bool? Admin)> Runs { get; } = new();
        public DashboardConnectStatus? Answer { get; set; } = answer;
        public Task<DashboardConnectStatus> Run(string client, bool? admin, CancellationToken cancellationToken)
        {
            Runs.Enqueue((client, admin));
            if (failure is not null) throw failure;
            return Task.FromResult(Answer ?? new DashboardConnectStatus { Clients = [new() { Id = client, Name = client, State = ConnectStates.Added, Admin = false }] });
        }
    }

    private RelayHostFixture ConnectHub(FakeConnect connect, IPAddress? remote, CapturedLines? logged = null) =>
        Hub(remote, services: services =>
        {
            services.AddSingleton<IAdminTools>(provider => new AdminToolsService(provider, connect.Run));
            if (logged is not null) services.AddSingleton<Serilog.Core.ILogEventSink>(logged);
        });

    private sealed class FixedObservation(RelayObservation? observation) : IRelayObservationSource
    {
        public Task<RelayObservation?> ReadObservationAsync(DateTimeOffset now, string incarnationId, CancellationToken cancellationToken = default) => Task.FromResult(observation);
        public Task<DatabaseMetrics?> ReadDatabaseMetricsAsync(int rowCountLimit = 10_000, CancellationToken cancellationToken = default) => Task.FromResult<DatabaseMetrics?>(null);
    }

    // ---- Who may call them ----

    [Theory]
    [InlineData(RelayTools.HubDiagnose)]
    [InlineData(RelayTools.AddPc)]
    [InlineData(RelayTools.ConnectApp)]
    public async Task A_worker_is_refused_even_on_the_hubs_pc_and_learns_nothing(string tool)
    {
        await using var host = Hub(IPAddress.Loopback);
        Listening(host);
        using var worker = Worker(host);
        var (status, code, _, raw) = await Post(worker, tool, new { repositoryId = "repo", requestId = "worker-call", client = ConnectClients.Codex, admin = true });
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal(RelayErrorCodes.AdministratorRequired, code);
        Assert.DoesNotContain(RelayHostFixture.JoinCode, raw);
        Assert.DoesNotContain(Fingerprint, raw);
    }

    [Fact]
    public async Task Being_the_recorded_manager_grants_none_of_them()
    {
        var connect = new FakeConnect();
        await using var host = ConnectHub(connect, IPAddress.Loopback);
        Listening(host);
        using var admin = host.AuthorizedClient();
        using var worker = Worker(host);
        var session = (await Post(worker, RelayTools.SessionOpen, new { repositoryId = "repo", requestId = "manager", label = "Manager", role = "manager" })).Data;
        var appointed = await Post(admin, RelayTools.ControlRequest, new { repositoryId = "repo", requestId = "select", command = "appoint_manager", targetSessionId = session.GetProperty("sessionId").GetString(), expectedManagerGeneration = 0, humanWords = "fixture selection" });
        Assert.Equal("Ok", appointed.Code);
        foreach (var tool in new[] { RelayTools.HubDiagnose, RelayTools.AddPc, RelayTools.ConnectApp })
        {
            var refused = await Post(worker, tool, new { repositoryId = "repo", requestId = "as-manager", client = ConnectClients.Codex, admin = true, sessionId = session.GetProperty("sessionId").GetString(), sessionToken = session.GetProperty("sessionToken").GetString() });
            Assert.Equal(RelayErrorCodes.AdministratorRequired, refused.Code);
        }
        Assert.Empty(connect.Runs);
    }

    /// <summary>Null is a connection with no address at all, which the dashboard's check also refuses.</summary>
    public static TheoryData<string?> NotThisPc => new() { "192.168.1.20", "10.0.0.5", "2001:db8::1", null };

    /// <summary>add_pc no longer looks at where a call comes from, so a forged header can gain nothing there; connect_app still judges the real connection.</summary>
    [Fact]
    public async Task A_forwarded_header_gains_nothing()
    {
        var connect = new FakeConnect();
        await using var host = ConnectHub(connect, Remote);
        Listening(host);
        void Forged(HttpClient client)
        {
            client.DefaultRequestHeaders.Add("X-Forwarded-For", "127.0.0.1");
            client.DefaultRequestHeaders.Add("X-Real-IP", "127.0.0.1");
            client.DefaultRequestHeaders.Add("Forwarded", "for=127.0.0.1");
        }
        using var admin = host.AuthorizedClient();
        Forged(admin);
        using var worker = Worker(host);
        Forged(worker);

        var refused = await Post(worker, RelayTools.AddPc, new { repositoryId = "repo" });
        Assert.Equal(RelayErrorCodes.AdministratorRequired, refused.Code);
        Assert.DoesNotContain(RelayHostFixture.JoinCode, refused.Raw);
        Assert.Equal("Ok", (await Post(admin, RelayTools.AddPc, new { repositoryId = "repo" })).Code);

        var connected = await Post(admin, RelayTools.ConnectApp, new { repositoryId = "repo", requestId = "forged", client = ConnectClients.Codex, admin = true });
        Assert.Equal(HttpStatusCode.Forbidden, connected.Status);
        Assert.Equal(RelayErrorCodes.HubPcRequired, connected.Code);
        Assert.Empty(connect.Runs);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("192.168.1.20")]
    public async Task The_join_code_reaches_no_log_traffic_buffer_telemetry_or_replay_state(string address)
    {
        var logged = new CapturedLines();
        await using var host = Hub(IPAddress.Parse(address), services: services => services.AddSingleton<Serilog.Core.ILogEventSink>(logged));
        Listening(host);
        using var admin = host.AuthorizedClient();

        // Twice, and once with a request ID and a session, which is what would be kept if anything were.
        var first = await Post(admin, RelayTools.AddPc, new { repositoryId = "repo" });
        var session = (await Post(admin, RelayTools.SessionOpen, new { repositoryId = "repo", requestId = "open" })).Data;
        var second = await Post(admin, RelayTools.AddPc, new { repositoryId = "repo", requestId = "add-1", sessionId = session.GetProperty("sessionId").GetString(), sessionToken = session.GetProperty("sessionToken").GetString() });
        Assert.Contains(RelayHostFixture.JoinCode, first.Raw);
        Assert.Contains(RelayHostFixture.JoinCode, second.Raw);

        var store = host.Services.GetRequiredService<SqliteStore>();
        await store.FlushActivityAsync();

        var traffic = host.Services.GetRequiredService<DashboardTrafficBuffer>().Snapshot();
        Assert.Contains(traffic, entry => entry.Name == RelayTools.AddPc);
        Assert.DoesNotContain(RelayHostFixture.JoinCode, JsonSerializer.Serialize(traffic));
        Assert.DoesNotContain(RelayHostFixture.JoinCode, JsonSerializer.Serialize(host.Services.GetRequiredService<DashboardLogBuffer>().Snapshot()));
        Assert.NotEmpty(logged.Lines);
        Assert.DoesNotContain(logged.Lines, line => line.Contains(RelayHostFixture.JoinCode, StringComparison.Ordinal));
        Assert.Equal(0, ((AdminToolsService)host.Services.GetRequiredService<IAdminTools>()).RememberedRequests);

        // Everything durable: the activity rows prove the calls were recorded, and no database file holds the code in any encoding it could take.
        Assert.True(await ActivityRowsAsync(host, RelayTools.AddPc) >= 2);
        // The hub's lock file is held open and holds nothing.
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(host.DatabasePath)!).Where(file => !file.EndsWith(".lock", StringComparison.Ordinal)))
        {
            var bytes = await ReadLiveDatabaseFileAsync(file);
            Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(RelayHostFixture.JoinCode)));
            Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(RelayHostFixture.JoinCode)));
        }
    }

    /// <summary>
    /// A live SQLite file's bytes, page by page, waiting out brief locks. SQLite holds byte ranges of its <c>-shm</c> file while it works, and on Windows reading a
    /// locked range throws whatever the sharing mode. That file is only the write-ahead log's index and never holds row content, so a page there that stays locked
    /// reads as zeros; the database and the log must be read in full or the scan fails.
    /// </summary>
    internal static async Task<byte[]> ReadLiveDatabaseFileAsync(string file, int attempts = 40)
    {
        const int Page = 4096;
        var mayskip = file.EndsWith("-shm", StringComparison.Ordinal);
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 0);
        using var all = new MemoryStream();
        var page = new byte[Page];
        while (all.Length < stream.Length)
        {
            var length = (int)Math.Min(Page, stream.Length - all.Length);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    stream.Position = all.Length;
                    await stream.ReadExactlyAsync(page.AsMemory(0, length));
                    break;
                }
                catch (IOException) when (attempt < attempts) { await Task.Delay(50); }
                catch (IOException) when (mayskip) { page.AsSpan(0, length).Clear(); break; }
            }
            all.Write(page, 0, length);
        }
        return all.ToArray();
    }

    private static async Task<long> ActivityRowsAsync(RelayHostFixture host, string tool)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={host.DatabasePath};Mode=ReadOnly");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM activity_log WHERE tool = $tool";
        command.Parameters.AddWithValue("$tool", tool);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed class CapturedLines : Serilog.Core.ILogEventSink
    {
        private readonly ConcurrentQueue<string> lines = new();
        public IReadOnlyCollection<string> Lines => lines;
        public void Emit(Serilog.Events.LogEvent logEvent) => lines.Enqueue(logEvent.RenderMessage());
    }

    // ---- connect_app ----

    [Theory]
    [MemberData(nameof(NotThisPc))]
    public async Task Connect_app_from_anywhere_else_is_refused_and_runs_nothing(string? address)
    {
        var connect = new FakeConnect();
        await using var host = ConnectHub(connect, address is null ? null : IPAddress.Parse(address));
        using var admin = host.AuthorizedClient();
        var (status, code, _, _) = await Post(admin, RelayTools.ConnectApp, new { repositoryId = "repo", requestId = "remote", client = ConnectClients.Codex });
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal(RelayErrorCodes.HubPcRequired, code);
        Assert.Empty(connect.Runs);
    }

    [Fact]
    public async Task An_admin_choice_from_another_pc_is_refused_runs_nothing_and_logs_nothing()
    {
        var connect = new FakeConnect();
        var logged = new CapturedLines();
        await using var host = ConnectHub(connect, Remote, logged);
        using var client = host.AuthorizedClient();
        var (status, code, _, _) = await Post(client, RelayTools.ConnectApp, new { repositoryId = "repo", requestId = "remote-choice", client = ConnectClients.Codex, admin = true });
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal(RelayErrorCodes.HubPcRequired, code);
        Assert.Empty(connect.Runs);
        Assert.DoesNotContain(logged.Lines, line => line.Contains("connect_app by", StringComparison.Ordinal));
    }

    private static async Task<(HttpStatusCode Status, JsonElement Reply)> PostRaw(HttpClient client, string body)
    {
        using var response = await client.PostAsync("/api/tools/" + RelayTools.ConnectApp, new StringContent(body, Encoding.UTF8, "application/json"));
        return (response.StatusCode, (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone());
    }

    private static (int Requests, int Order) Remembered(RelayHostFixture host)
    {
        var tools = (AdminToolsService)host.Services.GetRequiredService<IAdminTools>();
        return (tools.RememberedRequests, tools.RememberedOrderLength);
    }

    [Fact]
    public async Task A_changed_admin_under_one_request_id_conflicts()
    {
        var connect = new FakeConnect();
        await using var host = ConnectHub(connect, IPAddress.Loopback);
        using var client = host.AuthorizedClient();
        var first = await Post(client, RelayTools.ConnectApp, new { repositoryId = "repo", requestId = "same", client = ConnectClients.Codex, admin = true });
        Assert.Equal("Ok", first.Code);
        Assert.Equal(first.Raw, (await Post(client, RelayTools.ConnectApp, new { repositoryId = "repo", requestId = "same", client = ConnectClients.Codex, admin = true })).Raw);
        Assert.Single(connect.Runs);
        var other = await Post(client, RelayTools.ConnectApp, new { repositoryId = "repo", requestId = "same", client = ConnectClients.Codex, admin = false });
        Assert.Equal(HttpStatusCode.Conflict, other.Status);
        Assert.Equal(RelayErrorCodes.RequestConflict, other.Code);
        var absent = await Post(client, RelayTools.ConnectApp, new { repositoryId = "repo", requestId = "same", client = ConnectClients.Codex });
        Assert.Equal(RelayErrorCodes.RequestConflict, absent.Code);
        Assert.Single(connect.Runs);
    }

    [Fact]
    public async Task A_retry_returns_the_first_reply_without_running_setup_again_and_changed_arguments_conflict()
    {
        var connect = new FakeConnect();
        await using var host = ConnectHub(connect, IPAddress.Loopback);
        using var admin = host.AuthorizedClient();
        var first = await Post(admin, RelayTools.ConnectApp, new { repositoryId = "repo", requestId = "same", client = ConnectClients.Codex });
        // A second run would answer differently, so a repeat of the first answer shows it did not run.
        connect.Answer = new DashboardConnectStatus { Clients = [new() { Id = ConnectClients.Codex, Name = "Codex", State = ConnectStates.Connected }] };
        var retry = await Post(admin, RelayTools.ConnectApp, new { repositoryId = "repo", requestId = "same", client = ConnectClients.Codex });
        Assert.Equal(first.Raw, retry.Raw);
        Assert.Single(connect.Runs);

        var changed = await Post(admin, RelayTools.ConnectApp, new { repositoryId = "repo", requestId = "same", client = ConnectClients.ClaudeCode });
        Assert.Equal(HttpStatusCode.Conflict, changed.Status);
        Assert.Equal(RelayErrorCodes.RequestConflict, changed.Code);
        Assert.Single(connect.Runs);

        var another = await Post(admin, RelayTools.ConnectApp, new { repositoryId = "repo", requestId = "other", client = ConnectClients.Codex });
        Assert.Equal("Ok", another.Code);
        Assert.Equal(2, connect.Runs.Count);
    }

}
