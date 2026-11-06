using System.Text.Json;
using CoCern.Contracts;
using CoCern.Contracts.Dashboard;

namespace CoCern.Adapter.Node;

/// <summary>Private startup observation; public readiness always requires a fresh exact-handle probe.</summary>
internal static class NativeStartupEvidence
{
    private sealed record Receipt(string Definition, string Profile, string NativeId, string Workspace, string RepositoryId, string SessionId, string Generation, DateTimeOffset CapturedAt);
    private static string PathFor(NativeRegistration registration) => Path.Combine(registration.Folder, "native-codex-startup.json");

    internal static async Task RecordAsync(NativeRegistration registration, NativeJoin join, NativeHookContext context, SessionHandle handle, CancellationToken ct)
    {
        if (registration.Provider != "codex" || !registration.StillCurrent()) return;
        await using var binding = await NativeJoinBinding.AcquireAsync(join.PrivateFolder, "codex", context.NativeId, Environment.MachineName,
            handle.RepositoryId, context.Cwd, ct, existingOnly: true);
        if (binding.CurrentHandle?.SessionId != handle.SessionId) return;
        NodeState.WritePrivately(PathFor(registration), JsonSerializer.SerializeToUtf8Bytes(new Receipt(registration.Definition, registration.Profile,
            binding.NativeId, binding.Workspace, handle.RepositoryId, handle.SessionId, binding.Generation, DateTimeOffset.UtcNow)));
    }

    internal static DashboardCodexReadiness Verify(NativeRegistration registration, DashboardCodexReadiness initial)
    {
        try { return VerifyAsync(registration, initial).GetAwaiter().GetResult(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or OperationCanceledException or HttpRequestException)
        {
            return initial with { State = "error", Summary = "Codex readiness could not be verified", NextAction = "Check that CoCern is running, then refresh. Your saved session was preserved." };
        }
    }

    private static async Task<DashboardCodexReadiness> VerifyAsync(NativeRegistration registration, DashboardCodexReadiness initial)
    {
        var path = PathFor(registration);
        if (!File.Exists(path)) return initial;
        if (!NodeState.IsPrivateFile(path) || new FileInfo(path).Length > 16384) return initial;
        var saved = JsonSerializer.Deserialize<Receipt>(File.ReadAllBytes(path));
        if (saved is null || saved.Definition != registration.Definition || saved.Profile != registration.Profile ||
            saved.CapturedAt > DateTimeOffset.UtcNow.AddMinutes(1) || saved.CapturedAt < DateTimeOffset.UtcNow.AddDays(-7)) return initial;
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

    internal static bool HasReachableRoute(AdapterReply directory, SessionHandle handle) =>
        directory.Ok && directory.Data is { } data && data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array &&
            items.EnumerateArray().Any(item => ChatLocators.Text(item, "relaySessionId") == handle.SessionId &&
                ChatLocators.Text(item, "repositoryId") == handle.RepositoryId && string.Equals(ChatLocators.Text(item, "provider"), "codex", StringComparison.OrdinalIgnoreCase) &&
                item.TryGetProperty("reachable", out var reachable) && reachable.ValueKind == JsonValueKind.True);

    internal static string WorkflowContext(SessionHandle handle) =>
        $"CoCern native startup verified this chat in repository '{handle.RepositoryId}'. Use the cocern-workflow skill and the current CoCern tool schemas. Your own session is bound privately; do not open a replacement session or supply session credentials. Check status and inbox before coordinated work, read the task scope before accepting, and report only milestones or blockers. Native startup restores this context after resume or compaction.";
}
