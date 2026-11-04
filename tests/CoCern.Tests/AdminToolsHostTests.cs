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
    public async Task A_worker_on_another_pc_gets_no_join_code()
    {
        await using var host = Hub(Remote);
        Listening(host);
        using var worker = Worker(host);
        var (status, code, _, raw) = await Post(worker, RelayTools.AddPc, new { repositoryId = "repo" });
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal(RelayErrorCodes.AdministratorRequired, code);
        Assert.DoesNotContain(RelayHostFixture.JoinCode, raw);
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

    // ---- hub_diagnose ----

    [Fact]
    public async Task Hub_diagnose_reports_the_hub_and_each_pc_and_holds_no_secret_or_session_label()
    {
        var now = DateTimeOffset.UtcNow;
        var observation = new RelayObservation
        {
            SessionCount = 3,
            Machines =
            [
                new() { MachineId = "machine-live", Name = "LIVE-PC", Reachable = true, LastSeenAt = now, SessionCount = 2 },
                new() { MachineId = "machine-quiet", Name = "QUIET-PC", Reachable = false, LastSeenAt = now.AddDays(-3), SessionCount = 1 }
            ],
            Sessions = [new() { SessionId = "other", RepositoryId = "other-repository", Label = "private-label-from-another-repository" }]
        };
        await using var host = Hub(Remote, services: services => services.AddSingleton<IRelayObservationSource>(new FixedObservation(observation)), extra: new() { ["Relay:Network:Enabled"] = "true" });
        host.Services.GetRequiredService<HubListener>().Https = (5273, Fingerprint);
        using var admin = host.AuthorizedClient();

        // A read, and open to an administrator on another PC: it holds nothing that needs the hub's PC.
        var (status, code, data, raw) = await Post(admin, RelayTools.HubDiagnose, new { repositoryId = "repo" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Ok", code);
        Assert.False(string.IsNullOrEmpty(data.GetProperty("version").GetString()));
        Assert.True(data.GetProperty("uptimeMinutes").GetInt64() >= 0);
        Assert.Equal(Fingerprint, data.GetProperty("certificateFingerprint").GetString());
        Assert.True(data.GetProperty("discoveryEnabled").GetBoolean());
        Assert.True(data.GetProperty("networkEnabled").GetBoolean());
        Assert.Equal(3, data.GetProperty("sessionCount").GetInt32());
        Assert.Equal(RelayLimits.MaxRecords, data.GetProperty("sessionLimit").GetInt32());
        var pcs = data.GetProperty("pcs").EnumerateArray().ToDictionary(pc => pc.GetProperty("machineId").GetString()!);
        Assert.True(pcs["machine-live"].GetProperty("seenRecently").GetBoolean());
        Assert.False(pcs["machine-quiet"].GetProperty("seenRecently").GetBoolean());
        Assert.Equal("QUIET-PC", pcs["machine-quiet"].GetProperty("name").GetString());
        Assert.True(pcs["machine-quiet"].TryGetProperty("lastSeenAt", out _));
        // The hub keeps no PC's version yet, and the reply says so rather than implying every PC is current.
        Assert.Contains("version", data.GetProperty("versionNote").GetString(), StringComparison.OrdinalIgnoreCase);

        foreach (var secret in new[] { RelayHostFixture.Token, WorkerToken, RelayHostFixture.JoinCode, "private-label-from-another-repository", "PRIVATE KEY", root })
            Assert.DoesNotContain(secret, raw);
        Assert.DoesNotContain(root.Replace("\\", "\\\\"), raw);
    }

    [Fact]
    public async Task Hub_diagnose_gives_reasons_for_what_it_cannot_read_and_still_answers()
    {
        await using var host = Hub(Remote, services: services => services.AddSingleton<IRelayObservationSource>(new FixedObservation(null)), extra: new() { ["Relay:Analytics:Enabled"] = "false" });
        using var admin = host.AuthorizedClient();
        var (status, _, data, _) = await Post(admin, RelayTools.HubDiagnose, new { repositoryId = "repo" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("turned off", data.GetProperty("analyticsUnavailable").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("analyticsSnapshotAgeMinutes").ValueKind);
        Assert.Contains("busy", data.GetProperty("sessionsUnavailable").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("sessionCount").ValueKind);
        Assert.Equal(CertificateBackupStates.Off, data.GetProperty("certificateBackup").GetString());
    }

    [Fact]
    public async Task Hub_diagnose_reads_a_real_hubs_pcs_with_no_dashboard_viewer_connected()
    {
        await using var host = Hub(Remote);
        using var admin = host.AuthorizedClient();
        var opened = await Post(admin, RelayTools.SessionOpen, new { repositoryId = "repo", requestId = "open", label = "A chat" });
        Assert.Equal("Ok", opened.Code);
        var (status, _, data, raw) = await Post(admin, RelayTools.HubDiagnose, new { repositoryId = "repo" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("sessionsUnavailable").ValueKind);
        Assert.True(data.GetProperty("sessionCount").GetInt32() >= 1);
        Assert.DoesNotContain("A chat", raw);
        Assert.DoesNotContain(opened.Data.GetProperty("sessionToken").GetString()!, raw);
    }

    // ---- add_pc ----

    [Fact]
    public async Task Add_pc_returns_the_dashboards_command_with_a_note_not_to_forward_it()
    {
        await using var host = Hub(IPAddress.Loopback);
        Listening(host);
        using var admin = host.AuthorizedClient();
        var (status, code, data, _) = await Post(admin, RelayTools.AddPc, new { repositoryId = "repo" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Ok", code);
        Assert.Contains("secret", data.GetProperty("secretWarning").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("forward", data.GetProperty("secretWarning").GetString(), StringComparison.OrdinalIgnoreCase);
        var expected = host.Services.GetRequiredService<DashboardAddPc>().Status();
        var given = data.GetProperty("status");
        Assert.NotEmpty(expected.Addresses);
        Assert.Equal(expected.Addresses[0].Url, given.GetProperty("addresses")[0].GetProperty("url").GetString());
        Assert.Equal(DashboardAddPc.Command(expected.Addresses[0].Url, RelayHostFixture.JoinCode, Fingerprint), given.GetProperty("addresses")[0].GetProperty("command").GetString());
        Assert.Equal(expected.FirewallCommand, given.GetProperty("firewallCommand").GetString());
        Assert.False(data.TryGetProperty("compacted", out _));
    }

    /// <summary>Null is a connection with no address at all, which the dashboard's check also refuses.</summary>
    public static TheoryData<string?> NotThisPc => new() { "192.168.1.20", "10.0.0.5", "2001:db8::1", null };

    [Theory]
    [MemberData(nameof(NotThisPc))]
    public async Task Add_pc_answers_an_administrator_on_another_pc(string? address)
    {
        await using var host = Hub(address is null ? null : IPAddress.Parse(address));
        Listening(host);
        using var admin = host.AuthorizedClient();
        var (status, code, data, raw) = await Post(admin, RelayTools.AddPc, new { repositoryId = "repo" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Ok", code);
        Assert.Contains(RelayHostFixture.JoinCode, raw);
        Assert.Contains("secret", data.GetProperty("secretWarning").GetString(), StringComparison.OrdinalIgnoreCase);
    }

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

    [Fact]
    public async Task Add_pc_answers_the_same_way_over_mcp_on_the_hubs_pc_and_from_another_pc()
    {
        foreach (var address in new[] { IPAddress.Loopback, Remote })
        {
            await using var host = Hub(address);
            Listening(host);
            using var http = host.AuthorizedClient();
            await using var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri("https://localhost/mcp"), TransportMode = HttpTransportMode.StreamableHttp, EnableStandaloneGetStream = false }, http);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var mcp = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
            Assert.Contains(await mcp.ListToolsAsync(cancellationToken: timeout.Token), tool => tool.Name == RelayTools.AddPc);
            var result = await mcp.CallToolAsync(RelayTools.AddPc, new Dictionary<string, object?> { ["repositoryId"] = "repo" }, cancellationToken: timeout.Token);
            Assert.NotEqual(true, result.IsError);
            Assert.Contains(RelayHostFixture.JoinCode, Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        }
    }

    [Fact]
    public async Task Add_pc_passes_on_why_a_hub_cannot_take_other_pcs_as_readable_text()
    {
        await using (var off = Hub(IPAddress.Loopback, discovery: false))
        {
            Listening(off);
            using var admin = off.AuthorizedClient();
            var (status, _, data, raw) = await Post(admin, RelayTools.AddPc, new { repositoryId = "repo" });
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Contains("Relay:Discovery:Enabled", data.GetProperty("status").GetProperty("unavailable").GetString());
            Assert.DoesNotContain(RelayHostFixture.JoinCode, raw);
            Assert.Equal(JsonValueKind.Null, data.GetProperty("secretWarning").ValueKind);
        }
        await using var missing = Hub(IPAddress.Loopback, adapter: Path.Combine(root, "missing.dll"));
        Listening(missing);
        using var client = missing.AuthorizedClient();
        var noAdapter = await Post(client, RelayTools.AddPc, new { repositoryId = "repo" });
        Assert.Contains("adapter was not found", noAdapter.Data.GetProperty("status").GetProperty("unavailable").GetString());
        Assert.DoesNotContain(RelayHostFixture.JoinCode, noAdapter.Raw);
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
            byte[] bytes;
            await using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer);
                bytes = buffer.ToArray();
            }
            Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(RelayHostFixture.JoinCode)));
            Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(RelayHostFixture.JoinCode)));
        }
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
    public async Task Connect_app_connects_a_known_client_with_no_admin_choice_when_none_is_given()
    {
        var connect = new FakeConnect();
        var logged = new CapturedLines();
        await using var host = ConnectHub(connect, IPAddress.Loopback, logged);
        using var admin = host.AuthorizedClient();
        var (status, code, data, _) = await Post(admin, RelayTools.ConnectApp, new { repositoryId = "repo", requestId = "connect", client = ConnectClients.ClaudeCode });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Ok", code);
        Assert.Equal(ConnectStates.Added, data.GetProperty("clients")[0].GetProperty("state").GetString());
        Assert.Equal((ConnectClients.ClaudeCode, (bool?)null), Assert.Single(connect.Runs));
        Assert.DoesNotContain(logged.Lines, line => line.Contains("connect_app", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("anything")]
    [InlineData("")]
    [InlineData("CODEX")]
    [InlineData("codex --role admin")]
    public async Task Connect_app_refuses_an_unknown_client_and_starts_no_process(string client)
    {
        var connect = new FakeConnect();
        await using var host = ConnectHub(connect, IPAddress.Loopback);
        using var admin = host.AuthorizedClient();
        var (status, code, _, _) = await Post(admin, RelayTools.ConnectApp, new { repositoryId = "repo", requestId = "unknown", client });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(RelayErrorCodes.InvalidArguments, code);
        Assert.Empty(connect.Runs);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_admin_choice_reaches_the_connector_as_given_and_is_logged(bool choice)
    {
        var connect = new FakeConnect();
        var logged = new CapturedLines();
        await using var host = ConnectHub(connect, IPAddress.Loopback, logged);
        using var client = host.AuthorizedClient();
        var (status, code, _, _) = await Post(client, RelayTools.ConnectApp, new { repositoryId = "repo", requestId = "choice", client = ConnectClients.Codex, admin = choice });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Ok", code);
        Assert.Equal((ConnectClients.Codex, (bool?)choice), Assert.Single(connect.Runs));
        var line = Assert.Single(logged.Lines, entry => entry.Contains("connect_app", StringComparison.Ordinal));
        Assert.Contains("test-user", line);
        Assert.Contains(ConnectClients.Codex, line);
        Assert.Contains(choice.ToString(), line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(RelayHostFixture.Token, line);
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
        Assert.DoesNotContain(logged.Lines, line => line.Contains("connect_app", StringComparison.Ordinal));
    }

    private static async Task<(HttpStatusCode Status, JsonElement Reply)> PostRaw(HttpClient client, string body)
    {
        using var response = await client.PostAsync("/api/tools/" + RelayTools.ConnectApp, new StringContent(body, Encoding.UTF8, "application/json"));
        return (response.StatusCode, (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone());
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("{}")]
    [InlineData("[]")]
    public async Task A_non_boolean_admin_is_invalid_and_runs_nothing(string value)
    {
        var connect = new FakeConnect();
        await using var host = ConnectHub(connect, IPAddress.Loopback);
        using var client = host.AuthorizedClient();
        var (status, reply) = await PostRaw(client, """{"repositoryId":"repo","requestId":"r","client":"codex","admin":""" + value + "}");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(RelayErrorCodes.InvalidArguments, reply.GetProperty("code").GetString());
        Assert.Contains("admin", reply.GetProperty("dataJson").GetString());
        Assert.Empty(connect.Runs);
    }

    /// <summary>Ignoring another spelling would connect the app with other tools than the caller asked for.</summary>
    [Theory]
    [InlineData(""" "Admin":true""")]
    [InlineData(""" "ADMIN":false""")]
    [InlineData(""" "admin":false,"Admin":true""")]
    public async Task A_differently_cased_admin_is_refused_and_runs_nothing(string argument)
    {
        var connect = new FakeConnect();
        await using var host = ConnectHub(connect, IPAddress.Loopback);
        using var client = host.AuthorizedClient();
        var (status, reply) = await PostRaw(client, """{"repositoryId":"repo","requestId":"r","client":"codex",""" + argument + "}");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(RelayErrorCodes.InvalidArguments, reply.GetProperty("code").GetString());
        Assert.Empty(connect.Runs);
    }

    /// <summary>Some clients send null for every optional field; it asks for nothing, so it counts as left out.</summary>
    [Fact]
    public async Task A_null_admin_counts_as_left_out()
    {
        var connect = new FakeConnect();
        await using var host = ConnectHub(connect, IPAddress.Loopback);
        using var client = host.AuthorizedClient();
        var (status, _) = await PostRaw(client, """{"repositoryId":"repo","requestId":"r","client":"codex","admin":null}""");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal((ConnectClients.Codex, (bool?)null), Assert.Single(connect.Runs));
    }

    private static (int Requests, int Order) Remembered(RelayHostFixture host)
    {
        var tools = (AdminToolsService)host.Services.GetRequiredService<IAdminTools>();
        return (tools.RememberedRequests, tools.RememberedOrderLength);
    }

    [Fact]
    public async Task Many_failed_setups_leave_the_request_memory_bounded_and_a_remembered_success_still_replays()
    {
        var connect = new FakeConnect();
        await using var host = ConnectHub(connect, IPAddress.Loopback);
        using var client = host.AuthorizedClient();
        var kept = await Post(client, RelayTools.ConnectApp, new { repositoryId = "repo", requestId = "kept", client = ConnectClients.Codex });
        connect.Answer = new DashboardConnectStatus { Unavailable = "The adapter failed." };
        var attempts = RelayLimits.RememberedConnectRequests * 2;
        for (var attempt = 0; attempt < attempts; attempt++)
            Assert.Equal("The adapter failed.", (await Post(client, RelayTools.ConnectApp, new { repositoryId = "repo", requestId = "fails-" + attempt, client = ConnectClients.Codex })).Data.GetProperty("unavailable").GetString());
        Assert.Equal((1, 1), Remembered(host));

        // A second run would answer differently, so a repeat of the first answer shows it did not run.
        connect.Answer = new DashboardConnectStatus { Clients = [new() { Id = ConnectClients.Codex, Name = "Codex", State = ConnectStates.Connected }] };
        Assert.Equal(kept.Raw, (await Post(client, RelayTools.ConnectApp, new { repositoryId = "repo", requestId = "kept", client = ConnectClients.Codex })).Raw);
        Assert.Equal(1 + attempts, connect.Runs.Count);
    }

    [Fact]
    public async Task Successful_setups_stay_within_the_limit_in_both_records()
    {
        var connect = new FakeConnect();
        await using var host = ConnectHub(connect, IPAddress.Loopback);
        using var client = host.AuthorizedClient();
        for (var attempt = 0; attempt < RelayLimits.RememberedConnectRequests + 5; attempt++)
            Assert.Equal("Ok", (await Post(client, RelayTools.ConnectApp, new { repositoryId = "repo", requestId = "works-" + attempt, client = ConnectClients.Codex })).Code);
        Assert.Equal((RelayLimits.RememberedConnectRequests, RelayLimits.RememberedConnectRequests), Remembered(host));
    }

    /// <summary>A failed attempt leaves no place behind it, so the oldest request is the one evicted, not the retry that later succeeded.</summary>
    [Fact]
    public async Task A_retry_that_succeeds_is_not_evicted_by_its_own_earlier_failure()
    {
        var connect = new FakeConnect(new DashboardConnectStatus { Unavailable = "The adapter failed." });
        await using var host = ConnectHub(connect, IPAddress.Loopback);
        using var client = host.AuthorizedClient();
        await Post(client, RelayTools.ConnectApp, new { repositoryId = "repo", requestId = "retried", client = ConnectClients.Codex });
        connect.Answer = null;
        for (var attempt = 0; attempt < RelayLimits.RememberedConnectRequests - 1; attempt++)
            await Post(client, RelayTools.ConnectApp, new { repositoryId = "repo", requestId = "other-" + attempt, client = ConnectClients.Codex });
        var retried = await Post(client, RelayTools.ConnectApp, new { repositoryId = "repo", requestId = "retried", client = ConnectClients.Codex });
        await Post(client, RelayTools.ConnectApp, new { repositoryId = "repo", requestId = "one-more", client = ConnectClients.Codex });
        var runs = connect.Runs.Count;

        Assert.Equal(retried.Raw, (await Post(client, RelayTools.ConnectApp, new { repositoryId = "repo", requestId = "retried", client = ConnectClients.Codex })).Raw);
        Assert.Equal(runs, connect.Runs.Count);
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
    public async Task Connect_app_needs_a_request_id()
    {
        var connect = new FakeConnect();
        await using var host = ConnectHub(connect, IPAddress.Loopback);
        using var admin = host.AuthorizedClient();
        Assert.Equal(RelayErrorCodes.RequestIdRequired, (await Post(admin, RelayTools.ConnectApp, new { repositoryId = "repo", client = ConnectClients.Codex })).Code);
        Assert.Empty(connect.Runs);
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

    [Fact]
    public async Task A_failed_setup_returns_its_text_not_a_hub_error_and_its_retry_runs_again()
    {
        var connect = new FakeConnect(new DashboardConnectStatus { Unavailable = "The adapter failed: settings are locked." });
        await using var host = ConnectHub(connect, IPAddress.Loopback);
        using var admin = host.AuthorizedClient();
        var failed = await Post(admin, RelayTools.ConnectApp, new { repositoryId = "repo", requestId = "fails", client = ConnectClients.Codex });
        Assert.Equal(HttpStatusCode.OK, failed.Status);
        Assert.Equal("The adapter failed: settings are locked.", failed.Data.GetProperty("unavailable").GetString());

        connect.Answer = null;
        var retry = await Post(admin, RelayTools.ConnectApp, new { repositoryId = "repo", requestId = "fails", client = ConnectClients.Codex });
        Assert.Equal(ConnectStates.Added, retry.Data.GetProperty("clients")[0].GetProperty("state").GetString());
        Assert.Equal(2, connect.Runs.Count);
    }

    [Fact]
    public async Task An_adapter_that_cannot_start_is_failure_text_too()
    {
        var connect = new FakeConnect(failure: new System.ComponentModel.Win32Exception("The system cannot find the file specified: C:\\secret\\path"));
        await using var host = ConnectHub(connect, IPAddress.Loopback);
        using var admin = host.AuthorizedClient();
        var failed = await Post(admin, RelayTools.ConnectApp, new { repositoryId = "repo", requestId = "no-start", client = ConnectClients.Codex });
        Assert.Equal(HttpStatusCode.OK, failed.Status);
        Assert.Contains("could not start", failed.Data.GetProperty("unavailable").GetString());
        Assert.DoesNotContain("secret", failed.Raw);
    }

    /// <summary>The real connector, which stops before any process because the adapter it would run is missing.</summary>
    [Fact]
    public async Task The_real_connector_reports_a_missing_adapter_as_text()
    {
        await using var host = Hub(IPAddress.Loopback, adapter: Path.Combine(root, "missing.dll"));
        using var admin = host.AuthorizedClient();
        var (status, _, data, _) = await Post(admin, RelayTools.ConnectApp, new { repositoryId = "repo", requestId = "missing", client = ConnectClients.Codex });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("adapter was not found", data.GetProperty("unavailable").GetString());
    }
}
