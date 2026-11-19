using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using CoCern.Contracts;

namespace CoCern.Adapter;

/// <summary>Optional compact hints. Durable inbox reads remain authoritative.</summary>
public sealed class SessionHints(AdapterOptions options, Action<string>? writeHint = null, Func<HttpMessageHandler>? hubHandler = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Action<string> _writeHint = writeHint ?? Console.Error.WriteLine;
    private volatile Subscription? _current;
    private int _hintPending;

    /// <summary>The first back-off after a failed start or watch; it doubles up to <see cref="MaxRetry"/>.</summary>
    internal TimeSpan FirstRetry { get; init; } = TimeSpan.FromSeconds(1);
    internal TimeSpan MaxRetry { get; init; } = TimeSpan.FromSeconds(30);

    public void ResetHint() => Interlocked.Exchange(ref _hintPending, 0);

    // Multiple concurrent callers can borrow the same authenticated subscription. Explicit waits also work
    // when the optional stderr hints are disabled, using a connection scoped to that one wait.
    internal InboxSubscription? OpenInboxWait(JsonElement arguments)
    {
        if (!TryString(arguments, "repositoryId", out var repositoryId) || !TryString(arguments, "sessionId", out var sessionId)
            || !TryString(arguments, "sessionToken", out var sessionToken)) return null;
        if (_current is { } current && current.Matches(repositoryId, sessionId, sessionToken))
            return new(current, owned: false);
        return new(new Subscription(null, RelayHubConnection.Create(options, hubHandler), sessionId, sessionToken, repositoryId), owned: true);
    }

    internal sealed class InboxSubscription(Subscription subscription, bool owned) : IAsyncDisposable
    {
        internal (Task Changed, bool Listening) Observe() => subscription.Observe();

        public async ValueTask DisposeAsync()
        {
            if (!owned) return;
            // A slow SignalR transport shutdown must not hold the model-visible reply past its deadline.
            // StopAsync cancels the keeper first and still finishes disposing its transport if the budget expires.
            var shutdown = StopAsync();
            try { await shutdown.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch (TimeoutException) { }

            async Task StopAsync()
            {
                try { await subscription.StopAsync(); }
                catch (Exception) { /* An optional channel cannot invalidate a completed durable inbox read. */ }
            }
        }
    }

    public async Task ObserveSessionAsync(JsonElement arguments, AdapterReply reply, CancellationToken cancellationToken)
    {
        if (!options.SignalR || reply.Data is not { ValueKind: JsonValueKind.Object } data ||
            !TryString(data, "sessionId", out var sessionId) || !TryString(data, "sessionToken", out var sessionToken) ||
            !TryString(arguments, "repositoryId", out var repositoryId)) return;

        // Optional hint setup cannot turn a committed session_open into a failed call.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            Subscription subscription;
            await _gate.WaitAsync(timeout.Token);
            try
            {
                if (_current is { } old) await old.StopAsync();
                _current = subscription = new Subscription(this, RelayHubConnection.Create(options, hubHandler), sessionId, sessionToken, repositoryId);
            }
            finally { _gate.Release(); }
            // The keeper goes on retrying in the background whatever the first attempt does.
            if (!await subscription.FirstAttempt.WaitAsync(timeout.Token)) throw new TimeoutException();
        }
        catch (Exception)
        {
            _writeHint("CoCern optional hints are unavailable; use check_inbox for durable messages.");
        }
    }

    private static bool TryString(JsonElement element, string name, out string value)
    {
        value = "";
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString()!;
        return value.Length is > 0 and <= 8192;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_current is { } old) await old.StopAsync();
            _current = null;
        }
        finally { _gate.Release(); }
        _gate.Dispose();
    }

    /// <summary>One session's hint connection and the loop that keeps it started and joined to the session's group.</summary>
    internal sealed class Subscription
    {
        private readonly SessionHints? _owner;
        private readonly HubConnection _connection;
        private readonly string _sessionId, _sessionToken, _repositoryId;
        private readonly CancellationTokenSource _stop = new();
        private readonly SemaphoreSlim _changed = new(0, 1);
        private readonly TaskCompletionSource<bool> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _keeper;
        private readonly object _inboxGate = new();
        private TaskCompletionSource? _inboxChanged;
        // Bumped on every reconnect or close; the group was joined only while _joined equals it.
        private int _generation, _joined = -1;

        public Subscription(SessionHints? owner, HubConnection connection, string sessionId, string sessionToken, string repositoryId)
        {
            (_owner, _connection, _sessionId, _sessionToken, _repositoryId) = (owner, connection, sessionId, sessionToken, repositoryId);
            connection.On<JsonElement>(HubEvents.CoordinationChanged, hint =>
            {
                if (RelayHubConnection.SessionHintReason(hint, sessionId, repositoryId) is null) return;
                SignalInbox();
                if (owner is not null && hint.TryGetProperty("sessionId", out _) && Interlocked.Exchange(ref owner._hintPending, 1) == 0)
                    owner._writeHint("CoCern coordination changed; use check_inbox for durable messages.");
            });
            connection.Reconnecting += _ => { Interlocked.Increment(ref _generation); SignalInbox(); return Task.CompletedTask; };
            connection.Reconnected += _ => { Interlocked.Increment(ref _generation); SignalInbox(); Signal(); return Task.CompletedTask; };
            // Automatic reconnect gives up after a long outage; the keeper then starts the connection again.
            connection.Closed += _ => { Interlocked.Increment(ref _generation); SignalInbox(); Signal(); return Task.CompletedTask; };
            _keeper = Task.Run(() => KeepAsync(_stop.Token));
        }

        public Task<bool> FirstAttempt => _first.Task;

        internal bool Matches(string repositoryId, string sessionId, string sessionToken) =>
            repositoryId == _repositoryId && sessionId == _sessionId && sessionToken == _sessionToken;

        // Capture before reading the durable state. An intervening hint or (re)join completes that exact task.
        internal (Task, bool) Observe()
        {
            lock (_inboxGate)
            {
                _inboxChanged ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                return (_inboxChanged.Task, !_stop.IsCancellationRequested && _connection.State == HubConnectionState.Connected
                    && Volatile.Read(ref _joined) == Volatile.Read(ref _generation));
            }
        }

        private void SignalInbox()
        {
            lock (_inboxGate)
            {
                _inboxChanged?.TrySetResult();
                _inboxChanged = null;
            }
        }

        private void Signal()
        {
            try { _changed.Release(); }
            catch (SemaphoreFullException) { /* a check is already pending */ }
        }

        private async Task KeepAsync(CancellationToken ct)
        {
            var firstRetry = _owner?.FirstRetry ?? TimeSpan.FromSeconds(1);
            var maxRetry = _owner?.MaxRetry ?? TimeSpan.FromSeconds(30);
            var delay = firstRetry;
            while (!ct.IsCancellationRequested)
            {
                var ok = await TryEnsureAsync(ct);
                _first.TrySetResult(ok);
                try { await _changed.WaitAsync(ok ? Timeout.InfiniteTimeSpan : RetryPacing.Jitter(delay), ct); }
                catch (OperationCanceledException) { break; }
                delay = ok ? firstRetry : TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, maxRetry.Ticks));
            }
            _first.TrySetResult(false);
        }

        /// <summary>Starts the connection when it is down and joins the session's group when it has not since the last (re)connect.</summary>
        private async Task<bool> TryEnsureAsync(CancellationToken ct)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                if (_connection.State == HubConnectionState.Disconnected) await _connection.StartAsync(timeout.Token);
                var generation = Volatile.Read(ref _generation);
                if (_connection.State != HubConnectionState.Connected) return false;
                if (Volatile.Read(ref _joined) == generation) return true;
                await _connection.WatchSessionAsync(_sessionId, _sessionToken, _repositoryId, timeout.Token);
                Volatile.Write(ref _joined, generation);
                _owner?.ResetHint();
                SignalInbox();
                return generation == Volatile.Read(ref _generation);
            }
            catch (Exception) { return false; /* Hints are optional; do not expose invocation arguments in logs. */ }
        }

        public async Task StopAsync()
        {
            await _stop.CancelAsync();
            SignalInbox();
            try { await _keeper; } catch (Exception) { }
            await _connection.DisposeAsync();
            _stop.Dispose();
        }
    }
}
