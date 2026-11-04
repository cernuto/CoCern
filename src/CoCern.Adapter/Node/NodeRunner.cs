using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR.Client;
using CoCern.Contracts;

namespace CoCern.Adapter.Node;

/// <summary>How the node reaches the hub. Production pins the hub certificate; tests swap in an in-memory server's handlers, shorter timings and a chat list.</summary>
internal sealed class NodeTransport
{
    public static readonly TimeSpan DefaultMaxRetry = TimeSpan.FromSeconds(60);
    /// <summary>Trust on first use: the fingerprint of the certificate an https hub presents.</summary>
    public Func<Uri, CancellationToken, Task<string>> ProbeFingerprint { get; init; } = PinnedCertificate.ProbeAsync;
    /// <summary>The HTTP handler for a pinned fingerprint (null for loopback HTTP); the callback receives the fingerprint of any certificate it refuses.</summary>
    public Func<string?, Action<string>, HttpMessageHandler> CreateHandler { get; init; } = (fingerprint, mismatch) => fingerprint is null ? RelayForwarder.CreateHandler() : PinnedCertificate.CreateHandler(fingerprint, mismatch);
    /// <summary>Replaces the hint channel's handler; null keeps SignalR's own handler, pinned to the same fingerprint.</summary>
    public Func<HttpMessageHandler>? HubHandler { get; init; }
    /// <summary>Replaces the chat list; null (production) reads the session handles in the node folder and maps them with <see cref="Locators"/>.</summary>
    public Func<CancellationToken, Task<JsonArray>>? Chats { get; init; }
    public IReadOnlyList<IChatLocator> Locators { get; init; } = ChatLocators.Default();
    public TimeProvider Clock { get; init; } = TimeProvider.System;
    /// <summary>How long one request to the hub may take, body included.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>How long after opening a session is searched for; after that it is permanently unmapped.</summary>
    public TimeSpan LocateWindow { get; init; } = NodeChats.DefaultLocateWindow;
    /// <summary>The most one cycle spends reading transcripts; the announcement goes out with what is known so far.</summary>
    public TimeSpan LocateBudget { get; init; } = NodeChats.DefaultBudget;
    public Func<string[]> Clients { get; init; } = () => NodeRunner.DetectClients(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    public string MachineName { get; init; } = Environment.MachineName;
    public TimeSpan AnnounceInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan FirstRetry { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxRetry { get; init; } = DefaultMaxRetry;
    /// <summary>How many times, and how far apart, a credential file that exists but cannot be read is tried at startup before the node gives up.</summary>
    public int CredentialAttempts { get; init; } = 5;
    public TimeSpan CredentialRetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);
    /// <summary>The Codex executable wakes run; null when none is installed, so Codex chats are announced unwakeable.</summary>
    public Func<string?> CodexExecutable { get; init; } = CodexLocator.Find;
    /// <summary>How a wake reaches a Codex chat, given the executable; tests replace it so no real <c>codex.exe</c> ever runs.</summary>
    public Func<string, IWakeDispatcher> CodexDispatcher { get; init; } = executable => new CodexWakeDispatcher(executable, new CodexActivity(CodexActivity.DefaultSessionsRoot), queue: new CodexQueue(CodexQueue.DefaultHome));
    /// <summary>The wake watcher's fallback pass when no hint arrives.</summary>
    public TimeSpan WakeInterval { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>Fixed event names for tests (enrolled, announced, watching, hint, unauthorized, waking, wake-target); never carries data.</summary>
    public Action<string>? Trace { get; init; }
    /// <summary>The hub's join code, sent only when enrolling; enrolled PCs never need it again.</summary>
    public string? JoinCode { get; set; }
    /// <summary>The certificate fingerprint the operator read from the hub's log; when set, any other certificate is refused instead of pinned.</summary>
    public string? ExpectedFingerprint { get; set; }
    /// <summary>Setup's one enrollment-and-first-announce: stop with exit 0 right after the first accepted announcement (and exit 1 if cancelled before it).</summary>
    public bool SingleCycle { get; set; }
}

/// <summary>One of this PC's wakeable chats, as the hub's announce reply lists it.</summary>
public sealed record NodeRoute(string Provider, string ThreadId, string RelaySessionId, string RepositoryId);

/// <summary>
/// The long-running node on each PC: pins the hub certificate on first use, enrolls once, then announces every interval and whenever the hub hints that this machine's routes changed.
/// The routes in each announce reply drive waking: every Codex route whose session handle is on this PC is woken through <c>codex queue</c>, with state in <c>wake.db</c>.
/// A <c>stop</c> file in the node folder stops waking; announcing goes on. Claude chats are never woken here; their own <see cref="ClaudeWaiter"/> waits on their inbox.
/// </summary>
public sealed class NodeRunner : IAsyncDisposable
{
    public const int MismatchExitCode = 4;
    private const int MaxReplyBytes = 64 * 1024;
    private const string WakeDatabase = "wake.db";
    private enum Next { Wait, Retry, Again, Exit }

    private readonly NodeState _state;
    private readonly Uri _hub;
    private readonly NodeTransport _transport;
    private readonly TextWriter _error;
    private readonly SemaphoreSlim _refresh = new(0, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly NodeChats _chats;
    /// <summary>Repositories the hub refused for this PC; their chats stay out of announcements for this run.</summary>
    private readonly HashSet<string> _denied = new(StringComparer.Ordinal);
    /// <summary>Relay sessions the hub refused to announce because this machine did not open them; each is reported once.</summary>
    private readonly HashSet<string> _refusedSessions = new(StringComparer.Ordinal);
    private HttpClient? _http;
    private string? _credential;
    /// <summary>The credential the hub last answered 401 for, kept while re-enrolling: a 409 then means the rejection was ambiguous, and this credential is restored.</summary>
    private string? _rejected;
    private bool _rejectionReported;
    private string? _mismatch;
    private bool _mismatchReported;
    private HubConnection? _hints;
    private string? _hintsCredential;
    private Task _hintsTask = Task.CompletedTask;
    private bool _trouble;
    private int _exitCode;
    /// <summary>The routes from the last accepted announcement; replaced whole, read by the wake watcher.</summary>
    private volatile NodeRoute[] _routes = [];
    private readonly WakeNudge _nudge = new();
    private Task _wakeTask = Task.CompletedTask;
    private CancellationTokenSource? _wakeStop;
    private string? _wakeCredential;
    private bool _wakeTrouble;

    private NodeRunner(NodeState state, Uri hub, NodeTransport transport, TextWriter error)
    {
        (_state, _hub, _transport, _error) = (state, hub, transport, error);
        _chats = new NodeChats(state.Folder, transport.Locators, transport.Clock, transport.LocateWindow, transport.LocateBudget, () => transport.CodexExecutable() is not null);
    }

    /// <summary>Runs until cancelled (exit 0). Exit 1 for configuration problems or a revoked PC, 4 when the hub presents a certificate other than the pinned one.</summary>
    public static Task<int> RunAsync(NodeState state, Uri? hub, CancellationToken ct) => RunAsync(state, hub, new NodeTransport(), Console.Error, ct);

    internal static async Task<int> RunAsync(NodeState state, Uri? hub, NodeTransport transport, TextWriter error, CancellationToken ct)
    {
        Uri target;
        try { target = ResolveHub(state, hub); }
        catch (AdapterConfigurationException exception) { error.WriteLine(exception.Message); return 1; }
        await using var runner = new NodeRunner(state, target, transport, error);
        try { return await runner.LoopAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return transport.SingleCycle ? 1 : 0; }
    }

    /// <summary>The installed coding clients, found by their usual folders in the user profile.</summary>
    internal static string[] DetectClients(string profile) =>
        new[] { (ChatProviders.Codex, ".codex"), (ChatProviders.Claude, ".claude") }.Where(x => Directory.Exists(Path.Combine(profile, x.Item2))).Select(x => x.Item1).ToArray();

    internal static TimeSpan NextRetry(TimeSpan current, TimeSpan max) => current * 2 > max ? max : current * 2;

    /// <summary>A typed address counts only until the PC has enrolled; afterwards a different one is refused, so a typo cannot move the PC to another hub.</summary>
    private static Uri ResolveHub(NodeState state, Uri? typed)
    {
        if (typed is not null && !AdapterOptions.TryParseOrigin(typed.OriginalString, out typed))
            throw new AdapterConfigurationException("--hub must be a root https:// address, or http:// to this PC's loopback, without credentials, query, or fragment.");
        var stored = state.HubUrl is null ? null : new Uri(state.HubUrl);
        if (state.Enrolled)
        {
            if (stored is null) throw new AdapterConfigurationException($"'{state.Folder}' says this PC is enrolled but names no hub. Delete the folder to enroll again.");
            if (typed is not null && !SameOrigin(stored, typed))
                throw new AdapterConfigurationException($"This PC is already enrolled with the hub at {stored}, so --hub {typed} was refused. To move it to another hub on purpose, stop the node and delete '{state.Folder}'.");
            if (stored.Scheme == Uri.UriSchemeHttps && state.Fingerprint is null)
                throw new AdapterConfigurationException($"'{state.Folder}' holds no pinned certificate for the hub at {stored}, and the node will not trust a new one silently. Delete the folder to enroll again.");
            return stored;
        }
        if (typed is null) return stored ?? throw new AdapterConfigurationException($"This PC has no hub yet. Run the node once with --node --hub https://<hub-address>:{HubAddresses.NetworkHttpsPort}/.");
        // Before enrollment a typed address replaces the stored one, and a different hub is pinned afresh.
        if (stored is null || !SameOrigin(stored, typed)) state.SetHub(typed.AbsoluteUri, null);
        return typed;
    }

    private static bool SameOrigin(Uri left, Uri right) => Uri.Compare(left, right, UriComponents.SchemeAndServer, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0;

    private bool Https => _hub.Scheme == Uri.UriSchemeHttps;

    private async Task<int> LoopAsync(CancellationToken ct)
    {
        var read = _state.TryReadCredential(out _credential);
        // A file that exists but cannot be read now (antivirus, backup, permissions) may still hold a valid secret: wait briefly, and never renew or delete for it.
        for (var attempt = 1; read == CredentialRead.Unreadable && attempt < _transport.CredentialAttempts; attempt++)
        {
            await Task.Delay(_transport.CredentialRetryDelay, ct);
            read = _state.TryReadCredential(out _credential);
        }
        if (read == CredentialRead.Unreadable)
        {
            _error.WriteLine($"The machine credential in '{_state.Folder}' exists but cannot be read (another program may hold it open, or its permissions changed). The node stopped and changed nothing; fix access to the file and start it again.");
            return 1;
        }
        // The hub issues a new credential on every enrollment, so a genuinely missing one cannot be recovered under the old ID.
        if (read == CredentialRead.Missing && _state.Enrolled) _state.RenewMachineId();
        var retry = _transport.FirstRetry;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Next next;
            try { next = await StepAsync(ct); }
            catch (Exception exception) when (exception is HttpRequestException or IOException || (exception is OperationCanceledException && !ct.IsCancellationRequested))
            {
                next = _mismatch is null ? Unreachable() : Next.Exit;
            }
            if (_mismatch is { } presented)
            {
                // Setup stops at once. The running node sent nothing and keeps backing off: a moment's spoofed name or another network
                // would otherwise leave this PC off the hub until the next sign-in. It says so once per episode.
                if (_transport.SingleCycle)
                {
                    _error.WriteLine($"The hub at {_hub} presented a certificate with fingerprint {presented}, but this PC pinned {_state.Fingerprint}. Setup stopped and changed nothing. If the hub's certificate was replaced on purpose, delete '{_state.Folder}' and run setup again.");
                    return MismatchExitCode;
                }
                if (!_mismatchReported)
                    _error.WriteLine($"The hub at {_hub} presented a certificate with fingerprint {presented}, but this PC pinned {_state.Fingerprint}. Nothing was sent; the node keeps trying the pinned hub. If the hub's certificate was replaced on purpose, stop the node, delete '{_state.Folder}', and run setup again.");
                _mismatchReported = true;
                _mismatch = null;
                _http?.Dispose(); _http = null;
                _trouble = true;
                next = Next.Retry;
            }
            else if (next is not (Next.Retry or Next.Exit)) _mismatchReported = false;
            switch (next)
            {
                case Next.Exit: return _exitCode;
                case Next.Again: continue;
                case Next.Retry:
                    await Task.Delay(retry, ct);
                    retry = NextRetry(retry, _transport.MaxRetry);
                    break;
                default:
                    retry = _transport.FirstRetry;
                    await _refresh.WaitAsync(_transport.AnnounceInterval, ct);
                    break;
            }
        }
    }

    private async Task<Next> StepAsync(CancellationToken ct)
    {
        if (Https && _state.Fingerprint is null)
        {
            // Trust on first use unless the operator gave the fingerprint, recorded before anything secret is sent.
            var presented = await _transport.ProbeFingerprint(_hub, ct);
            if (!CertificateFingerprint.IsValid(presented)) return Next.Retry;
            if (!ExpectedMatches(presented)) return Unexpected(presented);
            _state.SetFingerprint(presented);
            _error.WriteLine($"CoCern node pinned the certificate of the hub at {_hub}: {_state.Fingerprint}.");
        }
        else if (Https && !ExpectedMatches(_state.Fingerprint!)) return Unexpected(_state.Fingerprint!);
        _http ??= new HttpClient(_transport.CreateHandler(Https ? _state.Fingerprint : null, presented => _mismatch ??= presented)) { Timeout = _transport.RequestTimeout };
        if (_credential is null)
        {
            var enrolled = await EnrollAsync(ct);
            if (enrolled is not Next.Again) return enrolled;
        }
        return await AnnounceAsync(ct);
    }

    private bool ExpectedMatches(string fingerprint) => _transport.ExpectedFingerprint is not { } expected || string.Equals(fingerprint, expected, StringComparison.OrdinalIgnoreCase);

    private Next Unexpected(string presented)
    {
        _exitCode = MismatchExitCode;
        _error.WriteLine($"The hub at {_hub} presented a certificate with fingerprint {presented}, but --fingerprint expects {_transport.ExpectedFingerprint}. Nothing was pinned or sent. Check the address and the fingerprint in the hub's log.");
        return Next.Exit;
    }

    /// <summary>Again means enrolled: go on to announce.</summary>
    private async Task<Next> EnrollAsync(CancellationToken ct)
    {
        var machineId = _state.MachineId;
        var body = new JsonObject { ["machineId"] = machineId, ["machineName"] = _transport.MachineName };
        // A code given now wins; otherwise the one setup kept, so a node the hub dropped enrolls again by itself.
        if ((_transport.JoinCode ?? _state.ReadJoinCode()) is { } joinCode) body["joinCode"] = joinCode;
        using var response = await PostAsync("api/discovery/enroll", body, null, ct);
        var reply = await ReadAsync(response, ct);
        if (response.StatusCode == HttpStatusCode.OK)
        {
            if (Text(reply, "machineId") != machineId || Text(reply, "credential") is not { } credential || credential.Length != 64 || !credential.All(Uri.IsHexDigit))
                return Retry($"The hub at {_hub} sent an unexpected enrollment reply. Retrying.");
            _state.SaveCredential(credential);
            (_credential, _rejected) = (credential, null);
            Recovered();
            _error.WriteLine($"CoCern node enrolled with the hub at {_hub} as machine {machineId}.");
            _transport.Trace?.Invoke("enrolled");
            return Next.Again;
        }
        var code = Code(reply);
        switch (response.StatusCode)
        {
            case HttpStatusCode.Conflict when code == RelayErrorCodes.MachineExpired:
                // The hub dropped this PC while it was away and never reissues an ID: join again under a new one.
                _error.WriteLine($"The hub at {_hub} dropped this PC (machine {machineId}) after a long absence. The node joins again under a new machine ID.");
                (_rejected, _credential) = (null, null);
                _state.RenewMachineId();
                return Next.Retry;
            case HttpStatusCode.Conflict when _rejected is not null:
                // The hub refused the credential yet still holds this ID under another one. That is ambiguous (a hub fault or a replaced record), so keep both and back off.
                _credential = _rejected;
                if (!_rejectionReported)
                    _error.WriteLine($"The hub at {_hub} rejected this PC's credential but still holds its machine ID {machineId}. The node keeps its identity and retries. If this continues, an administrator should revoke machine {machineId} on the hub and then re-run setup on this PC.");
                _rejectionReported = true;
                _trouble = true;
                return Next.Retry;
            case HttpStatusCode.Conflict:
                // No credential was ever saved for this ID (an earlier enrollment's reply was lost), so it cannot be recovered: continue under a new ID.
                _state.RenewMachineId();
                return Next.Retry;
            case HttpStatusCode.Forbidden when code == RelayErrorCodes.MachineRevoked:
                return Stop($"The hub at {_hub} has revoked this PC (machine {machineId}). The node stopped; ask the hub administrator.");
            case HttpStatusCode.Forbidden when code == RelayErrorCodes.JoinCodeRequired:
                return Stop($"The hub at {_hub} needs its join code to enroll this PC{((_transport.JoinCode ?? _state.ReadJoinCode()) is null ? "" : ", and the one this PC has is wrong")}. The node stopped. Run setup again with --join-code <code> (it is in the join-code file next to the hub's database).");
            case HttpStatusCode.Forbidden or HttpStatusCode.BadRequest:
                return Stop($"The hub at {_hub} refused to enroll this PC ({code}). The node stopped.");
            case HttpStatusCode.NotFound:
                return Retry($"The hub at {_hub} does not accept enrollment (is Relay:Discovery:Enabled on?). Retrying.");
            default:
                return Busy(response.StatusCode) ? Unreachable() : Retry($"The hub at {_hub} refused to enroll this PC ({(int)response.StatusCode} {code}). Retrying.");
        }
    }

    private async Task<Next> AnnounceAsync(CancellationToken ct)
    {
        var chats = await ChatsAsync(ct);
        var response = await PostAnnounceAsync(chats, ct);
        try
        {
            string? refusal = null;
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                refusal = Code(await ReadAsync(response, ct));
                if (refusal == RelayErrorCodes.RepositoryDenied && chats.Count > 0)
                {
                    response.Dispose();
                    (response, refusal) = await LeaveOutDeniedAsync(chats, ct);
                }
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // Only the hub refusing the credential leads to enrolling again; network errors never do. The credential is kept aside in case the hub turns out to still hold this ID.
                (_rejected, _credential) = (_credential, null);
                _transport.Trace?.Invoke("unauthorized");
                _error.WriteLine($"The hub at {_hub} no longer accepts this PC's credential; enrolling again.");
                return Next.Again;
            }
            if (!response.IsSuccessStatusCode)
                return Busy(response.StatusCode) ? Unreachable() : Retry($"The hub at {_hub} refused this PC's announcement ({(int)response.StatusCode} {refusal ?? Code(await ReadAsync(response, ct))}). Retrying.");
            var accepted = await ReadAsync(response, ct);
            ReportRefused(accepted);
            var routes = Routes(accepted);
            if (!routes.SequenceEqual(_routes)) { _routes = routes; _nudge.Pulse(); }
        }
        finally { response.Dispose(); }
        Recovered();
        _transport.Trace?.Invoke("announced");
        if (_transport.SingleCycle) return Next.Exit;
        EnsureHints();
        EnsureWaking();
        return Next.Wait;
    }

    /// <summary>
    /// The hub announces a chat only when this machine opened its relay session; any other chat comes back in <c>refused</c> with a denial code while the rest are announced.
    /// Each refused session is reported once per run; nothing else changes, since the hub keeps leaving it out.
    /// </summary>
    private void ReportRefused(JsonObject? reply)
    {
        if (reply?["refused"] is not JsonArray refused) return;
        foreach (var session in refused.OfType<JsonObject>().Select(x => Text(x, "relaySessionId")).OfType<string>())
            if (_refusedSessions.Add(session))
                _error.WriteLine($"The hub at {_hub} did not announce the chat with relay session '{Printable(session)}' because this PC's machine did not open that session (it may predate this PC's current enrollment); open a new session from that chat.");
    }

    /// <summary>The well-formed routes in an announce reply; none when it cannot be read.</summary>
    private static NodeRoute[] Routes(JsonObject? reply) => reply?["routes"] is JsonArray list
        ? list.OfType<JsonObject>().Select(x => (Provider: Text(x, "provider"), ThreadId: Text(x, "threadId"), Session: Text(x, "relaySessionId"), Repository: Text(x, "repositoryId")))
            .Where(x => x is { Provider: not null, ThreadId: not null, Session: not null, Repository: not null }).Select(x => new NodeRoute(x.Provider!, x.ThreadId!, x.Session!, x.Repository!)).Take(NodeChats.MaxChats).ToArray()
        : [];

    /// <summary>
    /// The chats to wake: each Codex route whose session handle on this PC names that same thread and repository, and only while a Codex executable is installed.
    /// Every other route (Claude chats, chats not on this PC, a route that disagrees with its handle) is ignored.
    /// </summary>
    internal static IReadOnlyList<WakeTarget> WakeTargets(string nodeFolder, IEnumerable<NodeRoute> routes, string? codex, Func<string, IWakeDispatcher> dispatcher, string machineName)
    {
        if (codex is null) return [];
        var targets = new List<WakeTarget>();
        IReadOnlyList<SessionHandle>? native = null;
        foreach (var route in routes)
        {
            if (route.Provider != ChatProviders.Codex || !ChatLocators.IsUuid(route.ThreadId) || !NodeState.IsFileId(route.RelaySessionId)) continue;
            var path = NodeState.SessionHandlePath(nodeFolder, route.RelaySessionId);
            if (NodeState.ReadSessionHandle(path) is not { Chat: { Provider: ChatProviders.Codex } chat } handle || !string.Equals(chat.ThreadId, route.ThreadId, StringComparison.OrdinalIgnoreCase) ||
                handle.RepositoryId != route.RepositoryId) continue;
            if (handle.NativeBindingKey is not null)
            {
                native ??= NativeJoinBinding.DispatchSnapshot(nodeFolder);
                if (!native.Contains(handle) || native.Count(other => other.Chat == handle.Chat) != 1) continue;
            }
            targets.Add(new(new WakeRoute(handle.RepositoryId, "default", path, chat.ThreadId, machineName), new WakeSession(handle.SessionId, handle.SessionToken),
                NativeJoinBinding.ForDispatch(nodeFolder, handle, dispatcher(codex))));
        }
        return targets;
    }

    private string StopFile => Path.Combine(_state.Folder, ClaudeWaiter.StopFile);

    /// <summary>Starts the wake watcher after an announcement unless the stop file exists. A watcher still on an old credential is stopped and started again after the next announcement.</summary>
    private void EnsureWaking()
    {
        if (!_wakeTask.IsCompleted)
        {
            if (_wakeCredential != _credential) _wakeStop?.Cancel();
            return;
        }
        if (File.Exists(StopFile)) return;
        _wakeStop?.Dispose();
        _wakeStop = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        _wakeCredential = _credential;
        var (credential, ct) = (_credential!, _wakeStop.Token);
        _wakeTask = Task.Run(() => WakeAsync(credential, ct));
    }

    private async Task WakeAsync(string credential, CancellationToken ct)
    {
        var fingerprint = Https ? _state.Fingerprint : null;
        var options = new AdapterOptions(_hub.AbsoluteUri, credential, signalR: true) { PinnedFingerprint = fingerprint };
        var runtime = new WakeRuntime(options, Path.Combine(_state.Folder, WakeDatabase), StopFile, _hub.AbsoluteUri) { Nudge = _nudge, HubHandler = _transport.HubHandler, Interval = _transport.WakeInterval };
        using var handler = _transport.CreateHandler(fingerprint, presented => _mismatch ??= presented);
        try
        {
            _transport.Trace?.Invoke("waking");
            await WakeWatcher.RunAsync(runtime, _ =>
            {
                var targets = WakeTargets(_state.Folder, _routes, _transport.CodexExecutable(), _transport.CodexDispatcher, _transport.MachineName);
                if (targets.Count > 0) _transport.Trace?.Invoke("wake-target");
                return Task.FromResult(targets);
            }, exitWhenQueued: false, timeout: null, ct, handler, _transport.Clock);
            _wakeTrouble = false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_wakeTrouble) _error.WriteLine($"The node could not wake chats from '{Path.Combine(_state.Folder, WakeDatabase)}' (another process may be using it); it tries again after the next announcement.");
            _wakeTrouble = true;
        }
    }

    /// <summary>The chats to announce, without any in a repository the hub refused.</summary>
    private async Task<JsonArray> ChatsAsync(CancellationToken ct)
    {
        if (_transport.Chats is null) return _chats.Read(repository => !_denied.Contains(repository));
        var given = await _transport.Chats(ct);
        return Only(given, chat => !_denied.Contains(Repository(chat)));
    }

    private Task<HttpResponseMessage> PostAnnounceAsync(JsonArray chats, CancellationToken ct) => PostAsync("api/discovery/announce", new JsonObject
    {
        ["machineName"] = _transport.MachineName,
        ["clients"] = new JsonArray(_transport.Clients().Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
        ["chats"] = chats.DeepClone()
    }, _credential, ct);

    /// <summary>
    /// The hub refuses a whole announcement when one chat names a repository this PC may not use. Adding one repository at a time finds which; each is reported once
    /// and left out, and the rest are announced. Any other failure stops the search and is returned, handled like an ordinary failed announcement.
    /// </summary>
    private async Task<(HttpResponseMessage Response, string? Refusal)> LeaveOutDeniedAsync(JsonArray chats, CancellationToken ct)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var repository in chats.Select(Repository).Distinct(StringComparer.Ordinal).ToArray())
        {
            var trial = await PostAnnounceAsync(Only(chats, chat => allowed.Contains(Repository(chat)) || Repository(chat) == repository), ct);
            if (trial.IsSuccessStatusCode) { trial.Dispose(); allowed.Add(repository); continue; }
            string? refusal;
            try { refusal = trial.StatusCode == HttpStatusCode.Forbidden ? Code(await ReadAsync(trial, ct)) : null; }
            catch { trial.Dispose(); throw; }
            // Any other failure is the cycle's own, so it retries with the full list; a reduced list is never posted for it.
            if (refusal != RelayErrorCodes.RepositoryDenied) return (trial, refusal);
            trial.Dispose();
            if (_denied.Add(repository))
                _error.WriteLine($"The hub at {_hub} does not let this PC announce chats in repository '{Printable(repository)}'; they are left out until the node restarts.");
        }
        return (await PostAnnounceAsync(Only(chats, chat => allowed.Contains(Repository(chat))), ct), null);
    }

    private static JsonArray Only(JsonArray chats, Func<JsonNode?, bool> keep) => new(chats.Where(keep).Select(x => x?.DeepClone()).ToArray());

    private static string Repository(JsonNode? chat) => chat?["repositoryId"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    /// <summary>A repository ID read from a local file, printed only when it is a plain identifier.</summary>
    private static string Printable(string repository) => repository.Length is > 0 and <= 256 && repository.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':' or '/') ? repository : "(unprintable)";

    private async Task<HttpResponseMessage> PostAsync(string path, JsonObject body, string? credential, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_hub, path)) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        if (credential is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return await _http!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private async Task<JsonObject?> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaxReplyBytes) return null;
        // HttpClient.Timeout stops at the headers under ResponseHeadersRead; this bounds the body read too.
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(_http!.Timeout);
        var ct = bounded.Token;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0)
        {
            bytes.Write(buffer, 0, count);
            if (bytes.Length > MaxReplyBytes) return null;
        }
        try { return JsonNode.Parse(bytes.ToArray()) as JsonObject; }
        catch (JsonException) { return null; }
    }

    /// <summary>The hub's refusal code, printed only when it is a plain word.</summary>
    private static string Code(JsonObject? reply) => Text(reply, "code") is { Length: > 0 and <= 64 } text && text.All(char.IsAsciiLetterOrDigit) ? text : "no code";

    private static bool Busy(HttpStatusCode status) => status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout || (int)status >= 500;

    private static string? Text(JsonObject? reply, string name) => reply?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private Next Stop(string message)
    {
        _error.WriteLine(message);
        _exitCode = 1;
        return Next.Exit;
    }

    private Next Unreachable() => Retry($"The hub at {_hub} is unreachable or busy; retrying with back-off up to {_transport.MaxRetry.TotalSeconds:0} s. This PC keeps its identity.");

    /// <summary>Backs off; the message is written once per run of failures.</summary>
    private Next Retry(string message)
    {
        if (!_trouble) _error.WriteLine(message);
        _trouble = true;
        return Next.Retry;
    }

    private void Recovered()
    {
        if (_trouble) _error.WriteLine($"The hub at {_hub} is answering normally again.");
        _trouble = false;
    }

    private void Refresh()
    {
        try { _refresh.Release(); }
        catch (SemaphoreFullException) { /* a refresh is already pending */ }
        catch (ObjectDisposedException) { /* stopping */ }
    }

    /// <summary>Starts the hint channel in the background when it is down or still carries an old credential. Announcements never wait for it.</summary>
    private void EnsureHints()
    {
        if (!_hintsTask.IsCompleted) return;
        if (_hints is { State: not HubConnectionState.Disconnected } && _hintsCredential == _credential) return;
        _hintsTask = ConnectHintsAsync(_credential!, _stopping.Token);
    }

    private async Task ConnectHintsAsync(string credential, CancellationToken ct)
    {
        if (_hints is { } old)
        {
            _hints = null;
            try { await old.DisposeAsync(); }
            catch (Exception) { /* the old channel is gone either way */ }
        }
        HubConnection connection;
        try { connection = RelayHubConnection.Create(new AdapterOptions(_hub.AbsoluteUri, credential, signalR: true) { PinnedFingerprint = Https ? _state.Fingerprint : null }, _transport.HubHandler); }
        catch (Exception) { return; /* Hints are optional. */ }
        connection.On<JsonElement>(HubEvents.DiscoveryChanged, _ => { _transport.Trace?.Invoke("hint"); Refresh(); });
        connection.Reconnected += async _ =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await connection.WatchMachineAsync(timeout.Token);
                Refresh();
            }
            catch (Exception) { /* Hints are optional; the interval still announces. */ }
        };
        (_hints, _hintsCredential) = (connection, credential);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await connection.StartAsync(timeout.Token);
            await connection.WatchMachineAsync(timeout.Token);
            _transport.Trace?.Invoke("watching");
            // A change made before the group was joined sent its hint to nobody, so refresh once now.
            Refresh();
        }
        catch (Exception)
        {
            // Hints are optional; the next successful announcement tries again.
            _hints = null;
            await connection.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        try { await _hintsTask; }
        catch (Exception) { /* the hint channel never decides the exit code */ }
        try { await _wakeTask; }
        catch (Exception) { /* waking never decides the exit code */ }
        _wakeStop?.Dispose();
        if (_hints is { } hints) { _hints = null; await hints.DisposeAsync(); }
        _http?.Dispose();
        _stopping.Dispose();
        _refresh.Dispose();
    }
}
