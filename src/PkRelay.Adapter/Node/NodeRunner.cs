using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR.Client;
using PkRelay.Contracts;

namespace PkRelay.Adapter.Node;

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
    /// <summary>Fixed event names for tests (enrolled, announced, watching, hint, unauthorized); never carries data.</summary>
    public Action<string>? Trace { get; init; }
}

/// <summary>The long-running node on each PC: pins the hub certificate on first use, enrolls once, then announces every interval and whenever the hub hints that this machine's routes changed.</summary>
public sealed class NodeRunner : IAsyncDisposable
{
    public const int MismatchExitCode = 4;
    private const int MaxReplyBytes = 64 * 1024;
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
    private HttpClient? _http;
    private string? _credential;
    private string? _mismatch;
    private HubConnection? _hints;
    private string? _hintsCredential;
    private Task _hintsTask = Task.CompletedTask;
    private bool _trouble;
    private int _exitCode;

    private NodeRunner(NodeState state, Uri hub, NodeTransport transport, TextWriter error)
    {
        (_state, _hub, _transport, _error) = (state, hub, transport, error);
        _chats = new NodeChats(state.Folder, transport.Locators, transport.Clock, transport.LocateWindow, transport.LocateBudget);
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return 0; }
    }

    /// <summary>The installed coding clients, found by their usual folders in the user profile.</summary>
    internal static string[] DetectClients(string profile) =>
        new[] { ("codex", ".codex"), ("claude", ".claude") }.Where(x => Directory.Exists(Path.Combine(profile, x.Item2))).Select(x => x.Item1).ToArray();

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
        if (typed is null) return stored ?? throw new AdapterConfigurationException("This PC has no hub yet. Run the node once with --node --hub https://<hub-address>:5273/.");
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
                _error.WriteLine($"The hub at {_hub} presented a certificate with fingerprint {presented}, but this PC pinned {_state.Fingerprint}. The node stopped and changed nothing. If the hub's certificate was replaced on purpose, stop the node, delete '{_state.Folder}', and run --node --hub again.");
                return MismatchExitCode;
            }
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
            // Trust on first use, recorded before anything secret is sent.
            var presented = await _transport.ProbeFingerprint(_hub, ct);
            if (!PinnedCertificate.IsFingerprint(presented)) return Next.Retry;
            _state.SetFingerprint(presented);
            _error.WriteLine($"PkRelay node pinned the certificate of the hub at {_hub}: {_state.Fingerprint}.");
        }
        _http ??= new HttpClient(_transport.CreateHandler(Https ? _state.Fingerprint : null, presented => _mismatch ??= presented)) { Timeout = TimeSpan.FromSeconds(30) };
        if (_credential is null)
        {
            var enrolled = await EnrollAsync(ct);
            if (enrolled is not Next.Again) return enrolled;
        }
        return await AnnounceAsync(ct);
    }

    /// <summary>Again means enrolled: go on to announce.</summary>
    private async Task<Next> EnrollAsync(CancellationToken ct)
    {
        var machineId = _state.MachineId;
        using var response = await PostAsync("api/discovery/enroll", new JsonObject { ["machineId"] = machineId, ["machineName"] = _transport.MachineName }, null, ct);
        var reply = await ReadAsync(response, ct);
        if (response.StatusCode == HttpStatusCode.OK)
        {
            if (Text(reply, "machineId") != machineId || Text(reply, "credential") is not { } credential || credential.Length != 64 || !credential.All(Uri.IsHexDigit))
                return Retry($"The hub at {_hub} sent an unexpected enrollment reply. Retrying.");
            _state.SaveCredential(credential);
            _credential = credential;
            Recovered();
            _error.WriteLine($"PkRelay node enrolled with the hub at {_hub} as machine {machineId}.");
            _transport.Trace?.Invoke("enrolled");
            return Next.Again;
        }
        var code = Code(reply);
        switch (response.StatusCode)
        {
            case HttpStatusCode.Conflict:
                // The hub already holds this ID under another credential, which cannot be recovered: continue under a new ID.
                _state.RenewMachineId();
                return Next.Retry;
            case HttpStatusCode.Forbidden when code == "MachineRevoked":
                return Stop($"The hub at {_hub} has revoked this PC (machine {machineId}). The node stopped; ask the hub administrator.");
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
                    response = await LeaveOutDeniedAsync(chats, ct);
                    refusal = null;
                }
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // Only the hub refusing the credential leads to enrolling again; network errors never do.
                _credential = null;
                _transport.Trace?.Invoke("unauthorized");
                _error.WriteLine($"The hub at {_hub} no longer accepts this PC's credential; enrolling again.");
                return Next.Again;
            }
            if (!response.IsSuccessStatusCode)
                return Busy(response.StatusCode) ? Unreachable() : Retry($"The hub at {_hub} refused this PC's announcement ({(int)response.StatusCode} {refusal ?? Code(await ReadAsync(response, ct))}). Retrying.");
        }
        finally { response.Dispose(); }
        Recovered();
        _transport.Trace?.Invoke("announced");
        EnsureHints();
        return Next.Wait;
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
    /// and left out, and the rest are announced. Any other refusal stops the search and is handled like an ordinary failed announcement.
    /// </summary>
    private async Task<HttpResponseMessage> LeaveOutDeniedAsync(JsonArray chats, CancellationToken ct)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var repository in chats.Select(Repository).Distinct(StringComparer.Ordinal).ToArray())
        {
            using var trial = await PostAnnounceAsync(Only(chats, chat => allowed.Contains(Repository(chat)) || Repository(chat) == repository), ct);
            if (trial.IsSuccessStatusCode) { allowed.Add(repository); continue; }
            if (trial.StatusCode != HttpStatusCode.Forbidden || Code(await ReadAsync(trial, ct)) != RelayErrorCodes.RepositoryDenied) break;
            if (_denied.Add(repository))
                _error.WriteLine($"The hub at {_hub} does not let this PC announce chats in repository '{Printable(repository)}'; they are left out until the node restarts.");
        }
        return await PostAnnounceAsync(Only(chats, chat => allowed.Contains(Repository(chat))), ct);
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

    private static async Task<JsonObject?> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > MaxReplyBytes) return null;
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
        connection.On<JsonElement>(RelayHubConnection.DiscoveryChangedEvent, _ => { _transport.Trace?.Invoke("hint"); Refresh(); });
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
        if (_hints is { } hints) { _hints = null; await hints.DisposeAsync(); }
        _http?.Dispose();
        _stopping.Dispose();
        _refresh.Dispose();
    }
}
