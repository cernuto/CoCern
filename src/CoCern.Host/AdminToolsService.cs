using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using CoCern.Analytics;
using CoCern.Contracts;
using CoCern.Contracts.Dashboard;
using CoCern.Core;
using CoCern.Domain;
using static CoCern.Contracts.RelayErrorCodes;
using static CoCern.Domain.JsonFields;
using static CoCern.Domain.Replies;

namespace CoCern.Host;

using Dashboard;

/// <summary>
/// <c>hub_diagnose</c>, <c>add_pc</c> and <c>connect_app</c>, over the services the dashboard uses. <c>connect_app</c> runs a process on this PC, so it answers
/// only a connection from the hub's own PC, judged as the dashboard judges it, from the connection and never a header; a call with no HTTP connection
/// behind it is refused. Services resolve per call, because the application holds this port and the dashboard's collector holds the application.
/// </summary>
internal sealed class AdminToolsService(IServiceProvider services, Func<string, bool?, CancellationToken, Task<DashboardConnectStatus>>? connect = null) : IAdminTools
{
    private readonly Func<string, bool?, CancellationToken, Task<DashboardConnectStatus>> runConnect = connect ?? ((client, admin, cancellationToken) => services.GetRequiredService<DashboardConnect>().ConnectAsync(client, admin, cancellationToken));
    private readonly object gate = new();
    private readonly Dictionary<string, (string Client, bool? Admin, Task<RelayReply> Run)> requests = new(StringComparer.Ordinal);
    /// <summary>The keys of <see cref="requests"/>, oldest first: exactly those, so a forgotten run leaves no stale place to grow or to evict its retry early.</summary>
    private readonly List<string> requestOrder = [];

    /// <summary>The <c>connect_app</c> requests kept for retries. No other tool here keeps anything, which is what keeps the join code out.</summary>
    internal int RememberedRequests { get { lock (gate) return requests.Count; } }

    internal int RememberedOrderLength { get { lock (gate) return requestOrder.Count; } }

    public Task<RelayReply> RunAsync(RelayCaller caller, string tool, JsonObject args, CancellationToken cancellationToken) => tool switch
    {
        RelayTools.HubDiagnose => DiagnoseAsync(cancellationToken),
        RelayTools.AddPc => Task.FromResult(AddPc()),
        RelayTools.ConnectApp => ConnectAsync(caller, args, cancellationToken),
        _ => Task.FromResult(Error(UnknownTool))
    };

    private bool OnHubPc() => services.GetRequiredService<IHttpContextAccessor>().HttpContext is { } context && DashboardConnect.OnThisPc(context);

    private static RelayReply NotOnHubPc() => Error(HubPcRequired, new { message = "This tool answers only on the hub's PC." });

    private async Task<RelayReply> DiagnoseAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var configuration = services.GetRequiredService<IConfiguration>();
        var listener = services.GetRequiredService<HubListener>();
        var host = services.GetRequiredService<DashboardCollector>().Current.Host;
        var fingerprint = (listener.Https ?? listener.Local)?.Fingerprint;
        // Only behind a running listener, whose start already settled where the certificate lives: CertificatePath can otherwise copy an older one into place.
        var certificate = fingerprint is null ? null : HubNetwork.CertificatePath(configuration, services.GetRequiredService<IHostEnvironment>());
        var (snapshotAge, analyticsUnavailable) = AnalyticsAge(now);
        var (observation, sessionsUnavailable) = await ObserveAsync(now, cancellationToken);
        // Any failure to read the offered release means none is offered, so every PC's adapter-current is unknown rather than out of date.
        var offered = services.GetService<DashboardConnect>() is { } connect && services.GetService<AdapterZipCache>() is { } cache ? await connect.OfferedReleaseAsync(cache, cancellationToken) : null;
        var pcs = (observation?.Machines ?? []).OrderBy(machine => machine.Reachable).ThenBy(machine => machine.LastSeenAt)
            .Select(machine => new HubDiagnosisPc
            {
                MachineId = machine.MachineId, Name = machine.Name, LastSeenAt = machine.LastSeenAt, SeenRecently = machine.Reachable, SessionCount = machine.SessionCount,
                AdapterVersion = machine.AdapterVersion, AdapterCurrent = DashboardConnect.AdapterCurrent(machine.AdapterDllSha256, offered)
            }).ToArray();
        var diagnosis = new HubDiagnosis
        {
            Version = host.Version, StartedAt = host.StartedAt, UptimeMinutes = (long)(now - host.StartedAt).TotalMinutes,
            Addresses = services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.ToArray() ?? [],
            CertificateFingerprint = fingerprint, CertificateBackup = BackupState(certificate, fingerprint),
            DiscoveryEnabled = DiscoverySettings.Enabled(configuration), NetworkEnabled = HubNetwork.Enabled(configuration),
            AnalyticsSnapshotAgeMinutes = snapshotAge, AnalyticsUnavailable = analyticsUnavailable,
            SessionCount = observation?.SessionCount, SessionsUnavailable = sessionsUnavailable, SessionsPartial = observation?.SessionsTruncated == true,
            OfferedAdapterVersion = offered?.Package.ProductVersion
        };
        return Ok(Fitted(RelayTools.HubDiagnose, pcs.Length, kept => diagnosis with { Pcs = pcs[..kept], Truncated = kept < pcs.Length, MorePcs = pcs.Length - kept }, services.GetService<ITokenCounter>(), least: 0));
    }

    /// <summary>The sessions and PCs as the dashboard's collector reads them, read directly because that collector samples only while a viewer is connected.</summary>
    private async Task<(RelayObservation? Observation, string? Unavailable)> ObserveAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var source = services.GetRequiredService<IRelayObservationSource>();
        var incarnation = services.GetRequiredService<RelayApplication>().IncarnationId;
        try
        {
            // SQLite performs synchronous bounded copies; keep that work off the call's thread, as the collector does.
            var observation = await Task.Run(() => source.ReadObservationAsync(now, incarnation, cancellationToken), cancellationToken);
            return observation is null ? (null, "The session directory is busy; call again.") : (observation, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return (null, "The session directory could not be read."); }
    }

    private (long? Age, string? Unavailable) AnalyticsAge(DateTimeOffset now)
    {
        try
        {
            if (!services.GetRequiredService<AnalyticsSettings>().Enabled) return (null, "Analytics is turned off on this hub (Relay:Analytics:Enabled).");
            return services.GetRequiredService<AnalyticsService>().SnapshotAt is { } taken ? ((long)(now - taken).TotalMinutes, null) : (null, "No analytics snapshot has been taken yet.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or AnalyticsException) { return (null, "Analytics could not be read on this hub."); }
    }

    /// <summary>Whether the backup beside <paramref name="certificate"/> is that certificate. A reply never gets the failure's text, which names the file.</summary>
    internal static string BackupState(string? certificate, string? fingerprint)
    {
        if (certificate is null || fingerprint is null) return CertificateBackupStates.Off;
        var backup = certificate + ".backup";
        try
        {
            if (!HubCertificate.EvidenceExists(backup)) return CertificateBackupStates.Missing;
            HubCertificate.ValidateCertificate(backup, fingerprint);
            return CertificateBackupStates.Matches;
        }
        catch (InvalidOperationException) { return CertificateBackupStates.Mismatch; }
    }

    private RelayReply AddPc() => Ok(AddPcReply(services.GetRequiredService<DashboardAddPc>().Status(), services.GetService<ITokenCounter>()));

    internal static JsonObject AddPcReply(DashboardAddPcStatus status, ITokenCounter? counter) =>
        Fitted(RelayTools.AddPc, status.Addresses.Length, kept => new AddPcReply
        {
            SecretWarning = status.Addresses.Length > 0 ? Contracts.AddPcReply.SecretNotice : null,
            Status = status with { Addresses = status.Addresses[..kept] }, Truncated = kept < status.Addresses.Length, MoreAddresses = status.Addresses.Length - kept
        }, counter, least: Math.Min(1, status.Addresses.Length));

    /// <summary>
    /// The reply with the most of its list that fits, at least <paramref name="least"/>. Past the wire limit the whole reply would be refused, and past
    /// its token budget compaction would cut long text such as a command short, so the list gives way instead.
    /// </summary>
    private static JsonObject Fitted<T>(string tool, int count, Func<int, T> reply, ITokenCounter? counter, int least)
    {
        var budget = RelayToolCatalog.TryGet(tool, out var definition) ? definition.TokenBudget : RelayLimits.DefaultReplyTokens;
        for (var kept = count; ; kept--)
        {
            var data = JsonSerializer.SerializeToNode(reply(kept), ToolResults.JsonOptions)!.AsObject();
            if (kept <= least) return data;
            if (JsonSerializer.SerializeToUtf8Bytes(Ok(data), ToolResults.JsonOptions).Length <= RelayLimits.MaxPayloadBytes - RelayLimits.AdminReplyRoomBytes && (counter is null || counter.Count(data.ToJsonString()) <= budget)) return data;
        }
    }

    private async Task<RelayReply> ConnectAsync(RelayCaller caller, JsonObject args, CancellationToken cancellationToken)
    {
        if (!OnHubPc()) return NotOnHubPc();
        if (!TryAdminChoice(args, out var choice)) return Error(InvalidArguments, new { field = "admin" });
        var client = Text(args, "client");
        if (!ConnectClients.All.Contains(client)) return Error(InvalidArguments, new { field = "client" });
        if (choice is not null) services.GetRequiredService<ILogger<AdminToolsService>>().LogInformation("connect_app by {Principal} for {Client} sets administrator tools to {Admin}", caller.PrincipalId, client, choice);
        var key = string.Join('\n', caller.PrincipalId, Text(args, "repositoryId"), Text(args, "requestId"));
        Task<RelayReply> run;
        lock (gate)
        {
            if (requests.TryGetValue(key, out var stored))
            {
                if (stored.Client != client || stored.Admin != choice) return Error(RequestConflict, new { message = "This requestId was already used for a connect_app with another client or admin." });
                run = stored.Run;
            }
            else
            {
                run = Task.Run(() => RunConnectAsync(key, client, choice), CancellationToken.None);
                requests[key] = (client, choice, run);
                requestOrder.Add(key);
                if (requests.Count > RelayLimits.RememberedConnectRequests) { requests.Remove(requestOrder[0]); requestOrder.RemoveAt(0); }
            }
        }
        return await run.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// The optional <c>admin</c> boolean; null when it is left out, or null as some clients send for every optional field. Argument names are
    /// case-sensitive, so another spelling is refused: ignoring it would connect the app with other tools than the caller asked for.
    /// </summary>
    private static bool TryAdminChoice(JsonObject args, out bool? choice)
    {
        choice = null;
        if (args.Any(argument => argument.Key != "admin" && argument.Key.Equals("admin", StringComparison.OrdinalIgnoreCase))) return false;
        if (args["admin"] is null) return true;
        if (args["admin"] is not JsonValue value || !value.TryGetValue<bool>(out var given)) return false;
        choice = given;
        return true;
    }

    /// <summary>Runs setup once for its request, whoever is waiting: one caller giving up never fails its retry. A failed run is forgotten, so its retry runs again.</summary>
    private async Task<RelayReply> RunConnectAsync(string key, string client, bool? admin)
    {
        DashboardConnectStatus status;
        try { status = await runConnect(client, admin, CancellationToken.None); }
        catch (Exception) { status = new() { Unavailable = "The adapter could not start." }; }
        if (status.Unavailable is not null || status.Clients.Any(entry => entry.State == ConnectStates.Error))
            lock (gate) { requests.Remove(key); requestOrder.Remove(key); }
        return Ok(JsonSerializer.SerializeToNode(status, ToolResults.JsonOptions)!);
    }
}
