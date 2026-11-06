using System.Text.Json;
using System.Diagnostics;
using CoCern.Contracts;
using CoCern.Contracts.Dashboard;

namespace CoCern.Adapter.Node;

/// <summary>Private startup observation; public readiness always requires a fresh exact-handle probe.</summary>
internal static class NativeStartupEvidence
{
    private sealed record Receipt(string Definition, string Profile, string NativeId, string Workspace, string RepositoryId, string SessionId, string Generation, DateTimeOffset CapturedAt);
    private sealed record Rejection(string Definition, string Code, DateTimeOffset CapturedAt);
    private sealed record WakeReceipt(string SessionId, string Generation, int ProcessId, DateTime StartedAt, DateTimeOffset VerifiedAt);
    private static string PathFor(NativeRegistration registration) => Path.Combine(registration.Folder, "native-codex-startup.json");
    private static string RejectionPathFor(NativeRegistration registration) => Path.Combine(registration.Folder, "native-codex-startup-error.json");
    internal const string RepositoryDeniedContext = "Repository mapping missing or not authorized (RepositoryDenied). Ask the hub administrator to check the repository ID, Git origin and your connection's repository authorization, then refresh Connect. The first CoCern call joins this same chat automatically; do not create a replacement session.";
    private static bool TerminalRefusal(string code) => code is RelayErrorCodes.RepositoryDenied or "native_private_connection_required" or "native_handle_conflict" or "native_join_handle_conflict";
    private static string RefusalSummary(string code) => code switch
    {
        RelayErrorCodes.RepositoryDenied => "Repository mapping missing or not authorized",
        "native_private_connection_required" => "The native connection could not be verified",
        _ => "The saved chat identity conflicts with native startup"
    };
    private static string RefusalAction(string code) => code switch
    {
        RelayErrorCodes.RepositoryDenied => RepositoryDeniedContext,
        "native_private_connection_required" => "Check this connection's diagnostics and private enrollment. Repair the owned connection from its installed package if needed; keep the existing credentials and hook trust. Then refresh Connect.",
        _ => "Check this chat's connection diagnostics for the conflicting saved identity. Keep the existing session and enrollment; do not open a replacement session or copy another chat's handle."
    };
    private static string? WakePath(string folder, SessionHandle handle) => handle.NativeBindingKey is { Length: 64 } key && key.All(Uri.IsHexDigit)
        ? Path.Combine(folder, "native-wake-" + key + ".json") : null;

    // Written only after the exclusive watcher has completed an authenticated poll, never merely because its task was started.
    internal static void RecordLocalWake(string folder, SessionHandle handle, string status)
    {
        if (status is not ("empty" or "queued")) { ClearLocalWake(folder, handle); return; }
        try
        {
            if (WakePath(folder, handle) is not { } path || NativeJoinBinding.DispatchGeneration(folder, handle) is not { } generation) return;
            using var process = Process.GetCurrentProcess();
            NodeState.WritePrivately(path, JsonSerializer.SerializeToUtf8Bytes(new WakeReceipt(handle.SessionId, generation,
                process.Id, process.StartTime.ToUniversalTime(), DateTimeOffset.UtcNow)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    internal static void ClearLocalWake(string folder, SessionHandle handle)
    {
        try
        {
            if (WakePath(folder, handle) is not { } path || !NodeState.IsPrivateFile(path) || new FileInfo(path).Length > 2048) return;
            var saved = JsonSerializer.Deserialize<WakeReceipt>(File.ReadAllBytes(path));
            using var process = Process.GetCurrentProcess();
            if (saved is not null && saved.ProcessId == process.Id && saved.StartedAt == process.StartTime.ToUniversalTime()) File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    internal static bool HasLiveLocalWake(NativeJoinBinding binding, SessionHandle handle)
    {
        try
        {
            if (File.Exists(Path.Combine(binding.Folder, "stop")) || WakePath(binding.Folder, handle) is not { } path ||
                !NodeState.IsPrivateFile(path) || new FileInfo(path).Length > 2048) return false;
            var saved = JsonSerializer.Deserialize<WakeReceipt>(File.ReadAllBytes(path));
            // A healthy hinted watcher polls at least once a minute; a stale poll cannot establish readiness.
            if (saved is null || saved.SessionId != handle.SessionId || saved.Generation != binding.Generation ||
                saved.VerifiedAt > DateTimeOffset.UtcNow.AddSeconds(5) || saved.VerifiedAt < DateTimeOffset.UtcNow.AddSeconds(-90)) return false;
            using var process = Process.GetProcessById(saved.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime() == saved.StartedAt;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    internal static async Task RecordAsync(NativeRegistration registration, NativeJoin join, NativeHookContext context, SessionHandle handle, CancellationToken ct)
    {
        if (registration.Provider != "codex" || !registration.StillCurrent()) return;
        await using var binding = await NativeJoinBinding.AcquireAsync(join.PrivateFolder, "codex", context.NativeId, Environment.MachineName,
            handle.RepositoryId, context.Cwd, ct, existingOnly: true);
        if (binding.CurrentHandle?.SessionId != handle.SessionId) return;
        NodeState.WritePrivately(PathFor(registration), JsonSerializer.SerializeToUtf8Bytes(new Receipt(registration.Definition, registration.Profile,
            binding.NativeId, binding.Workspace, handle.RepositoryId, handle.SessionId, binding.Generation, DateTimeOffset.UtcNow)));
    }

    // A genuine PreToolUse has already generated this stamp. Evidence is best-effort and may never join or repair a session.
    internal static async Task RecordStampedAsync(NativeRegistration registration, NativeJoin join, NativeHookContext context, JsonElement updated, CancellationToken ct)
    {
        try
        {
            if (registration.Provider != "codex" || context.Provider != "codex" || !registration.StillCurrent() ||
                !Guid.TryParse(context.NativeId, out var nativeId) || !updated.TryGetProperty("_cocernNative", out var stamp) ||
                ChatLocators.Text(stamp, "provider") != context.Provider || ChatLocators.Text(stamp, "nativeId") != nativeId.ToString("D") ||
                ChatLocators.Text(stamp, "repositoryId") is not { } repository || ChatLocators.Text(stamp, "cwd") is not { } workspace ||
                ChatLocators.Text(stamp, "generation") is not { } generation) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(250));
            await using var binding = await NativeJoinBinding.AcquireAsync(join.PrivateFolder, "codex", nativeId.ToString("D"), Environment.MachineName,
                repository, workspace, timeout.Token, existingOnly: true,
                authenticate: candidate => candidate.Generation == generation && candidate.CurrentHandle is not null);
            if (binding.Generation != generation || binding.CurrentHandle is not { } handle || !registration.StillCurrent()) return;
            NodeState.WritePrivately(PathFor(registration), JsonSerializer.SerializeToUtf8Bytes(new Receipt(registration.Definition, registration.Profile,
                binding.NativeId, binding.Workspace, handle.RepositoryId, handle.SessionId, binding.Generation, DateTimeOffset.UtcNow)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or OperationCanceledException or System.Security.SecurityException) { }
    }

    internal static void RecordRepositoryDenied(NativeRegistration registration) => RecordRefusal(registration, RelayErrorCodes.RepositoryDenied);

    internal static void RecordRefusal(NativeRegistration registration, string code)
    {
        try
        {
            if (registration.Provider != "codex" || !TerminalRefusal(code) || !registration.StillCurrent()) return;
            // Definition binds the exact profile, credential scope and helper version; retain no checkout or input data.
            NodeState.WritePrivately(RejectionPathFor(registration), JsonSerializer.SerializeToUtf8Bytes(
                new Rejection(registration.Definition, code, DateTimeOffset.UtcNow)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Security.SecurityException) { }
    }

    private static Rejection? ReadRejection(NativeRegistration registration, DateTimeOffset? succeededAt)
    {
        try
        {
            var path = RejectionPathFor(registration);
            if (!File.Exists(path) || !NodeState.IsPrivateFile(path) || new FileInfo(path).Length > 2048 || !registration.StillCurrent()) return null;
            var saved = JsonSerializer.Deserialize<Rejection>(File.ReadAllBytes(path));
            return saved is not null && saved.Definition == registration.Definition && TerminalRefusal(saved.Code) &&
                saved.CapturedAt <= DateTimeOffset.UtcNow.AddMinutes(1) && saved.CapturedAt >= DateTimeOffset.UtcNow.AddDays(-1) &&
                (succeededAt is null || saved.CapturedAt > succeededAt) ? saved : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or System.Security.SecurityException) { return null; }
    }

    internal static DashboardCodexReadiness Verify(NativeRegistration registration, DashboardCodexReadiness initial,
        string? checkout = null, string? repositoryId = null, Func<JsonElement, CancellationToken, Task<AdapterReply>>? preflight = null)
    {
        try { return VerifyAsync(registration, initial, checkout, repositoryId, preflight).GetAwaiter().GetResult(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or OperationCanceledException or HttpRequestException)
        {
            return initial with { State = "error", Summary = "Codex readiness could not be verified", NextAction = "Check that CoCern is running, then refresh. Your saved session was preserved." };
        }
    }

    internal static async Task<DashboardCodexReadiness> VerifyAsync(NativeRegistration registration, DashboardCodexReadiness initial,
        string? checkout = null, string? repositoryId = null, Func<JsonElement, CancellationToken, Task<AdapterReply>>? preflight = null)
    {
        var path = PathFor(registration);
        Receipt? saved = null;
        try
        {
            if (File.Exists(path) && NodeState.IsPrivateFile(path) && new FileInfo(path).Length <= 16384)
            {
                saved = JsonSerializer.Deserialize<Receipt>(File.ReadAllBytes(path));
                if (saved is not null && (saved.Definition != registration.Definition || saved.Profile != registration.Profile ||
                    saved.CapturedAt > DateTimeOffset.UtcNow.AddMinutes(1) || saved.CapturedAt < DateTimeOffset.UtcNow.AddDays(-7))) saved = null;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or System.Security.SecurityException) { saved = null; }
        var mapped = false;
        if (checkout is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var http = new HttpClient(RelayForwarder.CreateHandler(registration.Connection)) { Timeout = TimeSpan.FromSeconds(3) };
            var forwarder = new RelayForwarder(http, registration.Connection);
            await using var join = new NativeJoin(registration.Folder, registration.Connection, forwarder);
            var workspace = join.LatestPendingWorkspace() ?? saved?.Workspace ?? checkout;
            JsonElement evidence;
            try { evidence = await Task.Run(() => NativeCheckout.Inspect(workspace).Preflight(registration.Connection.BaseUri.IsLoopback, repositoryId), timeout.Token).WaitAsync(timeout.Token); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                return initial with { State = "error", Summary = "The configured checkout could not be checked", Detail = "native_checkout_unavailable",
                    NextAction = "Check that the configured repository checkout is available on this PC, then refresh Connect. No chat resume or hook review is needed for this check." };
            }
            var reply = preflight is null ? await forwarder.ForwardAsync(RelayTools.SessionOpen, evidence, timeout.Token) : await preflight(evidence, timeout.Token);
            if (!reply.Ok)
            {
                var code = TerminalRefusal(reply.Code) ? reply.Code : reply.Code is "hub_timeout" or "hub_unavailable" or "hub_identity_changed" or "unauthorized" or "forbidden" ? reply.Code : "native_preflight_failed";
                return initial with { State = "error", Summary = TerminalRefusal(code) ? RefusalSummary(code) : "The repository preflight could not reach an authorized hub", Detail = code,
                    NextAction = TerminalRefusal(code) ? RefusalAction(code) : "Check this connection's hub address, certificate pin and enrollment diagnostics, then refresh Connect. Your saved session and hook trust were preserved.",
                    SessionVerified = false, WakeReady = false, SessionId = null, RepositoryId = null };
            }
            if (reply.Code != "Ok" || reply.Version is null || reply.Data is not { } data || ChatLocators.Text(data, "repositoryId") is not { Length: > 0 } resolved ||
                AdapterOptions.ParseRepository(resolved) != resolved || repositoryId is not null && !string.Equals(repositoryId, resolved, StringComparison.OrdinalIgnoreCase))
                return initial with { State = "error", Summary = "The hub returned an invalid repository preflight", Detail = "invalid_hub_reply",
                    NextAction = "Check the hub's connection diagnostics, then refresh. No session was opened by this check." };
            mapped = true;
        }
        if (ReadRejection(registration, saved?.CapturedAt) is { } rejected && !(mapped && rejected.Code == RelayErrorCodes.RepositoryDenied))
            return initial with { State = "error", Summary = RefusalSummary(rejected.Code), Detail = rejected.Code,
                NextAction = RefusalAction(rejected.Code), StartupObserved = true, CapturedAt = rejected.CapturedAt,
                SessionVerified = false, WakeReady = false, SessionId = null, RepositoryId = null };
        if (LatestCatalogFailure(registration) is { } failure)
            return initial with { State = "error", Summary = "The latest native MCP connection failed to load", Detail = failure.ErrorCode,
                NextAction = "Reconnect the CoCern MCP server in Codex, then refresh. Check this connection's diagnostics if it still fails; its saved session and hook trust were preserved.",
                SessionVerified = false, WakeReady = false, SessionId = null, CapturedAt = failure.StartedAt };
        if (saved is null) return initial;
        initial = initial with { StartupObserved = true, CapturedAt = saved.CapturedAt };
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await using var binding = await NativeJoinBinding.AcquireAsync(registration.Folder, "codex", saved.NativeId, Environment.MachineName,
                saved.RepositoryId, saved.Workspace, timeout.Token, existingOnly: true,
                authenticate: candidate => candidate.Generation == saved.Generation && candidate.CurrentHandle?.SessionId == saved.SessionId);
            if (binding.Generation != saved.Generation || binding.CurrentHandle is not { } handle || handle.SessionId != saved.SessionId) return initial;
            using var http = new HttpClient(RelayForwarder.CreateHandler(registration.Connection)) { Timeout = TimeSpan.FromSeconds(3) };
            var forwarder = new RelayForwarder(http, registration.Connection);
            var reply = await forwarder.ProbeNativeAsync(binding, timeout.Token);
            if (!reply.Ok || reply.Version is null || !SessionHandoff.IsSessionReply(RelayTools.GetStatus,
                JsonSerializer.SerializeToElement(new { sessionId = handle.SessionId }), reply))
                return initial with { State = "error", Summary = "The saved Codex session is not currently verified", NextAction = "Resume the Codex chat in its repository, then refresh. CoCern will recover its own session." };
            initial = initial with { SessionVerified = true, SessionId = handle.SessionId, RepositoryId = handle.RepositoryId,
                State = "joined", Summary = "Codex session verified", NextAction = "Keep the CoCern agent running on this PC, then refresh to check its wake route." };
            if (registration.Connection.NodeFolder is null && HasLiveLocalWake(binding, handle))
                return initial with { State = "ready", Summary = "Codex is ready", NextAction = "Use CoCern from this chat. Its native startup will restore the workflow when you resume or compact.", WakeReady = true };
            var directory = await forwarder.ForwardAsync(RelayTools.ChatDirectory, JsonSerializer.SerializeToElement(new
            { repositoryId = handle.RepositoryId, sessionId = handle.SessionId, sessionToken = handle.SessionToken, backgroundCall = true }), timeout.Token);
            if (HasReachableRoute(directory, handle))
                return initial with { State = "ready", Summary = "Codex is ready", NextAction = "Use CoCern from this chat. Its native startup will restore the workflow when you resume or compact.", WakeReady = true };
            return initial;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or OperationCanceledException or HttpRequestException)
        {
            return initial.SessionVerified
                ? initial with { NextAction = "The live wake route could not be checked. Keep the agent running and refresh." }
                : initial with { State = "error", Summary = "The native startup was observed, but its session is unverified", NextAction = "Check that CoCern is running, then refresh. Your saved session was preserved." };
        }
    }

    private static ConnectionDiagnosticEvent? LatestCatalogFailure(NativeRegistration registration)
    {
        using var store = new ConnectionTraceStore(Path.Combine(registration.Folder, "connection-diagnostics"));
        var events = store.Read().Events;
        var failure = events.Where(item => item.Stage is "native_registration" or "backend_start" && item.Outcome == "error" &&
            item.ErrorCode is "native_registration_unverified" or "backend_unavailable" && item.StartedAt >= DateTimeOffset.UtcNow.AddMinutes(-10))
            .OrderByDescending(item => item.StartedAt).FirstOrDefault();
        if (failure is null) return null;
        return events.Any(item => item.Stage is "mcp_initialize" or "mcp_list_tools" && item.Outcome == "ok" && item.StartedAt >= failure.StartedAt) ? null : failure;
    }

    internal static bool HasReachableRoute(AdapterReply directory, SessionHandle handle) =>
        directory.Ok && directory.Data is { } data && data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array &&
            items.EnumerateArray().Any(item => ChatLocators.Text(item, "relaySessionId") == handle.SessionId &&
                ChatLocators.Text(item, "repositoryId") == handle.RepositoryId && string.Equals(ChatLocators.Text(item, "provider"), "codex", StringComparison.OrdinalIgnoreCase) &&
                item.TryGetProperty("reachable", out var reachable) && reachable.ValueKind == JsonValueKind.True);

    internal static string WorkflowContext(SessionHandle handle) =>
        $"CoCern native startup verified this chat in repository '{handle.RepositoryId}'. Use the cocern-workflow skill and the current CoCern tool schemas. Your own session is bound privately; do not open a replacement session or supply session credentials. Check status and inbox before coordinated work, read the task scope before accepting, and report only milestones or blockers. Native startup restores this context after resume or compaction.";
}
