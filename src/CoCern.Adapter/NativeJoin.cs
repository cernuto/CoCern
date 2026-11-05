using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoCern.Adapter.Node;
using CoCern.Contracts;

namespace CoCern.Adapter;

internal sealed record NativeHookContext(string Provider, string NativeId, string Cwd, string? DisplayTitle = null);

// Constructed only for a private enrolled or token-file connection. Hook verification belongs to the
// client integration; raw MCP arguments never construct a context here.
internal sealed class NativeJoin(string folder, AdapterOptions options, RelayForwarder forwarder) : IAsyncDisposable
{
    private readonly CancellationTokenSource stopping = new();
    // A chat's tool calls report as the model's; this binding's own checks do not, whichever process runs them. The native opening envelopes go untagged either way.
    private readonly RelayForwarder modelCalls = forwarder.Tagged(CallOrigins.Mcp), ownChecks = forwarder.Tagged(CallOrigins.Node);
    internal string PrivateFolder => folder;
    internal AdapterOptions WakeOptions => BackgroundOptions(options);
    // The private waiter/watcher has a fixed inbox/status protocol and uses the same pin and credential.
    internal static AdapterOptions BackgroundOptions(AdapterOptions connection) => new(connection.BaseUri.AbsoluteUri, connection.Token, signalR: connection.SignalR)
        { PinnedFingerprint = connection.PinnedFingerprint };
    private readonly ConcurrentDictionary<string, (string Session, SessionHints Keeper)> hints = new();
    private Task? runtime;
    internal TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(15);
    internal TimeSpan ResumeInterval { get; init; } = TimeSpan.FromSeconds(4);
    internal Func<string, string, string?> RootProof { get; init; } = (cwd, root) => ProtectedCheckoutRoot.ProveOrNull(cwd, root);

    private sealed class Refused(string code) : IOException(code) { internal string Code { get; } = code; }
    private sealed record Intent(string Provider, string NativeId, string Cwd, string? Repository = null);
    private sealed record Invocation(string Id, string Tool, JsonElement Args)
    {
        internal bool LegacyWire { get; set; }
    }
    // The forwarder reports untyped gateway errors as hub_error; only retry registration, never the user's tool.
    private static bool Transient(string code) => code is "hub_timeout" or "hub_unavailable" or "Unavailable" or "hub_error" or "rate_limited" or "native_generation_advanced";

    private string CredentialScope()
    {
        var node = NodeState.HasIdentityFiles(folder);
        var scope = node ? NodeState.NativeCredentialIdentity(folder) : LocalWakeConnection.NativeCredentialIdentity(folder);
        if (scope is null || scope != NativeJoinBinding.CredentialDigest(node ? "node" : "local", options.BaseUri.AbsoluteUri, options.PinnedFingerprint, options.Token))
            throw new Refused("native_private_connection_required");
        return scope;
    }

    private string IntentDir(string scope) => Path.Combine(folder, "native-pending-" + scope);
    private static void SafeDirectory(string path, bool create)
    {
        for (DirectoryInfo? p = new(path); p is not null; p = p.Parent)
            if (p.Exists && p.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new Refused("native_private_folder_required");
        if (create) Directory.CreateDirectory(path);
    }

    internal async Task<SessionHandle> EnsureAsync(NativeHookContext context, CancellationToken ct) =>
        (await EnsureBoundAsync(context, null, ct)).Handle;

    internal async Task<JsonElement> WithEstablishedSigningBindingAsync(NativeHookContext context, Func<NativeJoinBinding, JsonElement> sign,
        CancellationToken ct, Func<string, CancellationToken, Task<string>>? resolveCheckout = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, stopping.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var cancel = timeout.Token;
        try
        {
            cancel.ThrowIfCancellationRequested();
            if (context.Provider is not ("claude" or "codex") || context.NativeId is not { Length: 32 or 36 } ||
                !Guid.TryParseExact(context.NativeId, context.NativeId.Length == 32 ? "N" : "D", out var id)) throw new Refused("native_capture_required");
            var workspace = resolveCheckout is null
                ? await Task.Run(() => NativeCheckout.Inspect(context.Cwd).Root, cancel).WaitAsync(cancel)
                : await resolveCheckout(context.Cwd, cancel);
            var scope = CredentialScope();
            var directory = IntentDir(scope);
            SafeDirectory(directory, false);
            var intent = new Intent(context.Provider, id.ToString("D"), workspace);
            var name = NativeCheckout.Hash(JsonSerializer.Serialize(intent));
            // The same fence excludes SessionStart/open and repository remapping for this capture.
            // Retain it through the complete candidate lookup AND MAC issuance, without relay work.
            await using var membership = await NativeJoinBinding.LockAsync(Path.Combine(directory, name[..2] + ".lock"), cancel, existingOnly: true);
            if (CredentialScope() != scope) throw new Refused("native_private_connection_required");
            Func<string, CancellationToken, Task<string>>? knownCheckout = resolveCheckout is null ? null : (_, _) => Task.FromResult(workspace);
            await using var binding = await NativeJoinBinding.AcquireEstablishedAsync(folder, context.Provider, id.ToString("D"),
                Environment.MachineName, workspace, cancel, resolveCheckout: knownCheckout);
            cancel.ThrowIfCancellationRequested();
            return sign(binding);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested) { throw new Refused("native_binding_busy"); }
    }

    private async Task<(SessionHandle Handle,string? Request)> EnsureBoundAsync(NativeHookContext context, Invocation? invocation, CancellationToken ct, bool retry = true, string? expectedGeneration = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, stopping.Token);
        timeout.CancelAfter(Budget);
        var cancel = timeout.Token;
        cancel.ThrowIfCancellationRequested();
        if (context.Provider is not ("claude" or "codex") || context.NativeId is not { Length: 32 or 36 } ||
            !Guid.TryParseExact(context.NativeId, context.NativeId.Length == 32 ? "N" : "D", out var id)) throw new Refused("native_capture_required");
        var checkout = await Task.Run(() => NativeCheckout.Inspect(context.Cwd), cancel).WaitAsync(cancel);
        var evidence = checkout.Preflight(options.BaseUri.IsLoopback);
        var intent = new Intent(context.Provider, id.ToString("D"), checkout.Root);
        var scope = CredentialScope();
        var directory = IntentDir(scope);
        SafeDirectory(directory, true);
        var path = Path.Combine(directory, NativeCheckout.Hash(JsonSerializer.Serialize(intent)) + ".json");

        var delay = TimeSpan.FromMilliseconds(250);
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            // Fixed stripes bound lock-file count even for terminally denied startup events.
            await using (var intentLock = await NativeJoinBinding.LockAsync(Path.Combine(directory, Path.GetFileName(path)[..2] + ".lock"), cancel))
            try
            {
                if (CredentialScope() != scope) throw new Refused("native_private_connection_required");
                if (File.Exists(path))
                {
                    if (!NodeState.IsPrivateFile(path) || new FileInfo(path).Length > 65536 ||
                        JsonSerializer.Deserialize<Intent>(File.ReadAllBytes(path)) is not { } saved || (saved with { Repository = null }) != (intent with { Repository = null }))
                        throw new IOException("The pending native intent cannot be verified.");
                    intent = saved;
                }
                else
                {
                    await using var capacity = await NativeJoinBinding.LockAsync(Path.Combine(directory, "capacity.lock"), cancel);
                    if (Directory.EnumerateFiles(directory, "*.json").Take(512).Count() >= 512) throw new Refused("native_pending_capacity");
                    NodeState.WritePrivately(path, JsonSerializer.SerializeToUtf8Bytes(intent));
                }
                var resolution = await forwarder.ForwardAsync(RelayTools.SessionOpen, evidence, cancel);
                if (!resolution.Ok) throw new Refused(resolution.Code);
                if (resolution.Code != "Ok" || resolution.Version is null || resolution.Data is not { } data ||
                    ChatLocators.Text(data, "repositoryId") is not { Length: > 0 and <= RelayLimits.MaxIdentifierLength } repo || repo.Any(char.IsControl))
                    throw new Refused("invalid_hub_reply");
                if (intent.Repository is not null && intent.Repository != repo) throw new Refused("native_repository_changed");
                if (intent.Repository is null)
                {
                    intent = intent with { Repository = repo };
                    NodeState.WritePrivately(path, JsonSerializer.SerializeToUtf8Bytes(intent));
                }
                await using var binding = await NativeJoinBinding.AcquireAsync(folder, intent.Provider, intent.NativeId, Environment.MachineName, repo, checkout.Root, cancel);
                if (!forwarder.MatchesNativeBinding(binding)) throw new Refused("native_private_connection_required");
                if (expectedGeneration is not null && binding.Generation != expectedGeneration) throw new Refused("native_stamp_invalid");
                var handle = binding.CurrentHandle;
                var client = new ChatLocation(intent.Provider, intent.NativeId);
                if (handle is not null)
                {
                    // Verify the exact durable handle, never resume a nearby session by chat name or repo.
                    var probe = await ownChecks.ProbeNativeAsync(binding, cancel);
                    if (!probe.Ok && probe.Version is not null && probe.Code == RelayErrorCodes.SessionUnknown) handle = null;
                    else if (!probe.Ok) throw new Refused(probe.Code);
                    else if (probe.Version is null || !SessionHandoff.IsSessionReply(RelayTools.GetStatus, JsonSerializer.SerializeToElement(new { sessionId = handle.SessionId }), probe))
                        throw new Refused("native_handle_unverified");
                }
                if (handle is null)
                {
                    var handoff = SessionHandoff.ForFolder(folder, client);
                    var protectedRoot = await ProtectedRootAsync(context.Cwd, checkout.Root, cancel);
                    var opened = await handoff.OpenNativeJoinAsync(binding, forwarder, cancel, context.DisplayTitle, evidence.GetProperty("nativePreflight"), protectedRoot);
                    if (!opened.Completed) throw new Refused(opened.Code);
                    handle = binding.CurrentHandle ?? throw new Refused("native_handle_unverified");
                }
                if (handle.Client != client || handle.Chat != client || handle.Unmapped) throw new Refused("native_handle_conflict");
                // Generation changes are observed while the same cross-process lease still excludes a stale completion.
                if (options.SignalR && (!hints.TryGetValue(binding.BindingKey, out var prior) || prior.Session != handle.SessionId))
                {
                    if (prior.Keeper is not null) await prior.Keeper.DisposeAsync();
                    var hint = new SessionHints(options);
                    hints[binding.BindingKey] = (handle.SessionId, hint);
                    await hint.ObserveSessionAsync(JsonSerializer.SerializeToElement(new { repositoryId = handle.RepositoryId }),
                        new(true, "Ok", JsonSerializer.SerializeToElement(new { sessionId = handle.SessionId, sessionToken = handle.SessionToken })), cancel);
                }
                var request = invocation is null ? null : await BindInvocationAsync(binding, handle, invocation, cancel);
                File.Delete(path);
                return (handle, request);
            }
            catch (Refused error) when (Transient(error.Code))
            {
                if (!retry) throw;
            }
            catch (Refused) { File.Delete(path); throw; }
            await Task.Delay(delay, cancel);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 4000));
        }
    }

    // Eligibility only: a refused or stalled proof means this join gets no protected scope, never that the join fails.
    private async Task<string?> ProtectedRootAsync(string cwd, string root, CancellationToken ct)
    {
        try { return await Task.Run(() => RootProof(cwd, root), ct).WaitAsync(TimeSpan.FromSeconds(3), ct); }
        catch (TimeoutException) { return null; }
    }

    // Reserve before forwarding. A lost reply stays on its original session, even after process restart.
    private static async Task<string> BindInvocationAsync(NativeJoinBinding binding, SessionHandle handle, Invocation invocation, CancellationToken ct)
    {
        var now = TimeProvider.System.GetUtcNow().ToUnixTimeSeconds();
        if (await NativeJoinHook.BindReadInvocationAsync(binding, invocation.Tool, invocation.Id, invocation.Args, now + 120, now, admit: false, ct: ct) is { } refusal)
            throw new Refused(refusal);
        var result = MutationFence(binding, handle, invocation.Id, invocation.Tool, invocation.Args, reserve: true);
        invocation.LegacyWire = result.Legacy;
        return result.Request;
    }

    // The signed endpoint checks both domains and reserves mutations under its already-authenticated
    // lease, before any registration/status traffic. A rejected read never poisons a legacy retry.
    internal static async Task<string?> AdmitStampedInvocationAsync(NativeJoinBinding binding, string tool, string id, JsonElement args, long expires, long now,
        CancellationToken ct, Func<string, byte[], CancellationToken, Task>? writeReadCache = null, Func<TimeSpan, CancellationToken, Task>? retryDelay = null)
    {
        try
        {
            if (!RelayToolCatalog.TryGet(tool, out var definition) || binding.CurrentHandle is not { } handle) return "native_invocation_invalid";
            _ = MutationFence(binding, handle, id, tool, args, reserve: false);
            if (await NativeJoinHook.BindReadInvocationAsync(binding, tool, id, args, expires, now, admit: !definition.IsMutation, ct: ct,
                writeReadCache: writeReadCache, retryDelay: retryDelay) is { } refusal) return refusal;
            if (definition.IsMutation) _ = MutationFence(binding, handle, id, tool, args, reserve: true);
            return null;
        }
        catch (Refused error) { return error.Code; }
    }

    private static (string Request, bool Legacy) MutationFence(NativeJoinBinding binding, SessionHandle handle, string id, string tool, JsonElement args, bool reserve)
    {
        var directory = Path.Combine(Path.GetDirectoryName(binding.StatePath)!, "calls-" + binding.BindingKey);
        SafeDirectory(directory, false);
        var request = NativeCheckout.Hash(binding.BindingKey + ":" + id);
        var path = Path.Combine(directory, request + ".json");
        var canonical = NativeCheckout.Hash(JsonSerializer.Serialize(new[] { binding.Generation, handle.SessionId, tool, NativeJoinHook.Canonical(args) }));
        if (File.Exists(path))
        {
            if (!NodeState.IsPrivateFile(path) || new FileInfo(path).Length is not (64 or 67)) throw new Refused("native_invocation_conflict");
            var prior = File.ReadAllText(path);
            if (prior.Length == 64)
            {
                var legacy = NativeCheckout.Hash(JsonSerializer.Serialize(new[] { binding.Generation, handle.SessionId, tool, args.GetRawText() }));
                if (prior == legacy) return (request, true);
                // The inactive hook checkpoint also wrote unversioned canonical digests. Preserve
                // their canonical wire, without changing or deleting either kind of old evidence.
                if (prior == canonical) return (request, false);
            }
            else if (prior == "v2:" + canonical) return (request, false);
            throw new Refused("native_invocation_conflict");
        }
        if (reserve)
        {
            SafeDirectory(directory, true);
            if (Directory.EnumerateFiles(directory, "*.json").Take(4096).Count() == 4096) throw new Refused("native_invocation_capacity");
            NodeState.WritePrivately(path, System.Text.Encoding.ASCII.GetBytes("v2:" + canonical));
        }
        return (request, false);
    }

    internal async Task<AdapterReply> CallAsync(NativeHookContext context, string invocation, string tool, JsonElement args, CancellationToken ct, string? expectedGeneration = null)
    {
        if (invocation is not { Length: > 0 and <= 256 } || invocation.Any(char.IsControl) || args.ValueKind != JsonValueKind.Object ||
            !RelayToolCatalog.TryGet(tool, out var definition) || tool is RelayTools.SessionOpen or RelayTools.SessionBind) return new(false, "native_invocation_invalid");
        if (args.TryGetProperty("sessionToken", out _) || args.TryGetProperty("sessionId", out _) || args.TryGetProperty("_cocernNative", out _) || ProtectedCheckoutRoot.NamedIn(args))
            return new(false, "native_invocation_conflict");
        try
        {
            var admission = definition.IsMutation ? new Invocation(invocation, tool, args) : null;
            var (handle, request) = await EnsureBoundAsync(context, admission, ct, expectedGeneration: expectedGeneration);
            var payload = JsonNode.Parse(admission?.LegacyWire == true ? args.GetRawText() : NativeJoinHook.Canonical(args))!.AsObject();
            if (payload.ContainsKey("repositoryId") && (string?)payload["repositoryId"] != handle.RepositoryId) return new(false, "native_repository_conflict");
            payload["repositoryId"] = handle.RepositoryId;
            if (definition.RequiresSession || tool == RelayTools.GetStatus)
            { payload["sessionId"] = handle.SessionId; payload["sessionToken"] = handle.SessionToken; }
            if (definition.IsMutation && request is not null) payload["requestId"] = request;
            var arguments = RelayForwarder.WithOrigin(JsonSerializer.SerializeToElement(payload), background: false);
            if (tool == RelayTools.ControlRequest) arguments = CoordinationTools.WithOwnSession(arguments, handle.SessionId);
            var reply = tool == RelayTools.CheckInbox ? await InboxWait.CheckAsync(modelCalls, arguments, InboxWait.PollInterval, ct) :
                await ConditionalRead.ForwardAsync(modelCalls, tool, arguments, ct);
            if (reply.Ok && options.SignalR && tool == RelayTools.CheckInbox)
                foreach (var entry in hints.Values.Where(entry => entry.Session == handle.SessionId)) entry.Keeper.ResetHint();
            return reply;
        }
        catch (Refused error) { return new(false, error.Code); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, "native_join_pending"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        { return new(false, "native_private_checkout_required"); }
    }

    internal Task ResumeAsync(CancellationToken ct) => runtime ??= ResumeLoopAsync(ct);

    private async Task ResumeLoopAsync(CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, stopping.Token);
        var cancel = lifetime.Token;
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                try
                {
                    var directory = IntentDir(CredentialScope());
                    SafeDirectory(directory, false);
                    if (Directory.Exists(directory))
                    foreach (var file in Directory.EnumerateFiles(directory, "*.json").Take(512))
                    {
                        try
                        {
                            if (!NodeState.IsPrivateFile(file) || new FileInfo(file).Length > 65536) continue;
                            var intent = JsonSerializer.Deserialize<Intent>(File.ReadAllBytes(file));
                            if (intent is null || Path.GetFileName(file) != NativeCheckout.Hash(JsonSerializer.Serialize(intent with { Repository = null })) + ".json") continue;
                            await EnsureBoundAsync(new(intent.Provider, intent.NativeId, intent.Cwd), null, cancel, retry: false);
                        }
                        catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { }
                        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or JsonException) { }
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                await Task.Delay(ResumeInterval, cancel);
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync();
        if (runtime is not null) await runtime;
        foreach (var entry in hints.Values) await entry.Keeper.DisposeAsync();
        stopping.Dispose();
    }
}
