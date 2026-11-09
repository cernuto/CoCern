using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CoCern.Adapter.Node;

/// <summary>A private, connection-scoped opening attempt. Never serialize a lease into a relay reply.</summary>
internal sealed class NativeJoinBinding : IAsyncDisposable
{
    internal sealed class MissingEstablishedBinding() : IOException("An established native binding is not present.");
    internal string Folder { get; }
    internal string Provider { get; }
    internal string NativeId { get; }
    internal string RepositoryId { get; }
    /// <summary>The checkout the chat is in now. One binding serves every checkout of its repository; the frozen proof keeps the checkout it was made in.</summary>
    internal string Workspace { get; private set; }
    /// <summary>
    /// The binding's identity: its handle ownership, call fences and signatures derive from it. A binding made per chat uses its own file key; one
    /// adopted from a chat's older per-checkout binding keeps that binding's key, so its session handle and in-flight calls stay valid.
    /// </summary>
    internal string BindingKey { get; }
    internal string StatePath => Path.Combine(Folder, NodeState.SessionsFolder, stateFolder, fileKey + ".json");
    internal string LockPath => Path.ChangeExtension(StatePath, ".lock");
    /// <summary>
    /// Where this binding's call fences live: beside the state that owns <see cref="BindingKey"/>. An adopted binding keeps its older
    /// binding's fences, so an uncertain call made before adoption is retried on the same fence and request identity.
    /// </summary>
    internal string CallsDirectory => Path.Combine(Folder, NodeState.SessionsFolder, BindingKey == fileKey ? stateFolder : CheckoutFolder, "calls-" + BindingKey);
    /// <summary>
    /// The chat moved to another checkout of this repository since the hub last recorded one: the next join tells the hub, with that checkout's
    /// evidence. A checkout an older hub would not record (<see cref="CheckoutNotRecorded"/>) is not reported again until the chat moves on.
    /// </summary>
    internal bool Moved => Workspace != savedWorkspace && Workspace != unrecorded;
    /// <summary>The optional field of the private nativeJoin envelope naming the checkout a moved chat is in now. It is outside the participant proof.</summary>
    internal const string CurrentWorkspaceField = "currentWorkspace";
    /// <summary>
    /// Per-chat bindings live in their own folder. Older adapters read every file in <see cref="CheckoutFolder"/> with the per-checkout schema and
    /// refuse the whole folder on an unknown one, so a per-chat file must never appear there while older adapters may still run on this PC.
    /// </summary>
    private const string ChatFolder = "native-chats", CheckoutFolder = "native";
    private readonly string credentialIdentity, fileKey, stateFolder;
    private string savedWorkspace;
    private string? unrecorded, recordingRefused;
    private JsonElement arguments;
    private JsonElement? resolution;
    private FileStream? owner, adoptedFence;
    private string? currentSessionId;
    private const int MaxStateBytes = 16 * 1024;
    private const int StateVersion = 2;
    /// <summary>The retryable refusal when a chat's older binding is held by another (older) adapter: adopting it later keeps one identity.</summary>
    internal const string LegacyBindingBusy = "native_legacy_binding_busy";

    /// <summary>Dispatch observation only. No ID-only credential lookup, file creation or adoption.</summary>
    internal static IReadOnlyList<SessionHandle> DispatchSnapshot(string folder) => DispatchRecords(folder).Select(item => item.Handle).ToArray();

    internal static string? DispatchGeneration(string folder, SessionHandle handle) =>
        // The handle carries a generation-specific ownership digest, not the stable binding record's filename.
        handle.NativeBindingKey is not null ? DispatchRecords(folder)
            .FirstOrDefault(record => record.Handle == handle)?.Generation : null;

    private sealed record DispatchRecord(string Path, SessionHandle Handle, string Generation, DateTime Confirmed, bool PerChat);

    /// <summary>
    /// Only one binding per chat may be dispatched, or no fence would pass. A chat has one binding per repository (<see cref="ChatKey"/>), which
    /// always wins; a chat that ran in more than one checkout before that has one older binding per checkout, which still counts until the chat's
    /// next join adopts one (<see cref="AdoptLegacy"/>). Among those: the one confirmed most recently (its state file last written by an
    /// authenticated open, or touched by <see cref="Confirm"/> after a live probe), then the newest current session, then the binding's file name.
    /// The others stay on disk unchanged.
    /// </summary>
    private static List<DispatchRecord> Latest(IEnumerable<DispatchRecord> records) =>
        records.GroupBy(record => record.Handle.Chat).Select(group => group.OrderByDescending(record => record.PerChat).ThenByDescending(record => record.Confirmed)
            .ThenByDescending(record => record.Handle.OpenedAt).ThenBy(record => record.Path, StringComparer.Ordinal).First()).ToList();

    /// <summary>The session the hub answered <c>SessionUnknown</c> for, as marked beside this binding's state; null when none is marked or the mark cannot be trusted.</summary>
    private static string? UnknownSession(string statePath)
    {
        try
        {
            var path = Path.ChangeExtension(statePath, UnknownExtension);
            if (!File.Exists(path)) return null;
            RequirePrivate(path, 1024);
            using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 2 });
            var value = Object(document.RootElement, "version", "sessionId");
            return value["version"].GetInt32() == 1 && NodeState.IsFileId(Text(value["sessionId"])) ? Text(value["sessionId"]) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException or KeyNotFoundException or FormatException) { return null; }
    }

    private const string UnknownExtension = ".unknown";

    /// <summary>
    /// Records that the hub answered <c>SessionUnknown</c> for this exact dispatched native session, so it is no longer dispatched or polled, here or after
    /// a restart. Only the binding's state is consulted; nothing is deleted, and the mark applies to that one session ID only: the chat's own next open
    /// (<see cref="Complete"/>, <see cref="Supersede"/>, <see cref="AdvanceExpired"/>) or a live probe (<see cref="Confirm"/>) clears it.
    /// </summary>
    internal static void MarkSessionUnknown(string folder, SessionHandle handle)
    {
        if (handle.NativeBindingKey is null) return;
        try
        {
            if (DispatchRecords(folder).FirstOrDefault(record => record.Handle == handle) is not { } record) return;
            // Under the same reader fence as observation: a writer mid-join holds the lease, and this mark then waits for a later answer.
            using var reader = new FileStream(Path.ChangeExtension(record.Path, ".lock"), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (DispatchRecords(folder, record.Path).SingleOrDefault()?.Handle != handle) return;
            NodeState.WritePrivately(Path.ChangeExtension(record.Path, UnknownExtension), JsonSerializer.SerializeToUtf8Bytes(new { version = 1, sessionId = handle.SessionId }));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    private void ClearUnknown()
    {
        try { File.Delete(Path.ChangeExtension(StatePath, UnknownExtension)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } // A stale mark names an older session and no longer matches.
    }

    /// <summary>
    /// The authenticated probe found this binding's current session live: this is the checkout the chat uses now. Its state's write time moves forward,
    /// so it outranks the chat's other bindings (<see cref="Latest"/>), and any SessionUnknown mark is cleared. Nothing in the frozen state changes.
    /// </summary>
    internal void Confirm()
    {
        CheckLease();
        if (currentSessionId is null) return;
        try { File.SetLastWriteTimeUtc(StatePath, DateTime.UtcNow); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } // Selection then falls back to the session's age.
        ClearUnknown();
    }

    private static IReadOnlyList<DispatchRecord> DispatchRecords(string folder, string? exact = null)
    {
        var result = new List<DispatchRecord>();
        try
        {
            var sessions = Path.Combine(folder, NodeState.SessionsFolder);
            if (!Directory.Exists(Path.Combine(sessions, CheckoutFolder)) && !Directory.Exists(Path.Combine(sessions, ChatFolder))) return result;
            folder = CanonicalDirectory(folder);
            var identity = ConnectionIdentity(folder);
            sessions = Path.Combine(folder, NodeState.SessionsFolder);
            RejectLink(sessions);
            var reads = (exact is null ? StateFiles(sessions, CheckoutFolder).Concat(StateFiles(sessions, ChatFolder)) : [exact])
                .Select(path => ReadRecord(folder, identity, path)).OfType<StoredRecord>().ToList();
            // An older per-checkout binding is inert once its chat has any binding for that repository, with a session or not.
            var adopted = exact is null ? ChatScopes(folder, identity, sessions) : [];
            // A session the hub already answered SessionUnknown for is neither dispatched nor polled again until the chat reopens it.
            result.AddRange(reads.Where(read => !read.Unknown && (read.Record.PerChat || !adopted.Contains(Scope(read.State)))).Select(read => read.Record));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        return exact is null ? Latest(result) : result;
    }

    private static IEnumerable<string> StateFiles(string sessions, string name)
    {
        var directory = Path.Combine(sessions, name);
        if (!Directory.Exists(directory)) return [];
        RejectLink(directory);
        return Directory.EnumerateFiles(directory, "*.json").Take(2048);
    }

    private static (string Provider, string NativeId, string Machine, string Repository) Scope(State state) => (state.Provider, state.NativeId, state.Machine, state.Repository);

    /// <summary>Every chat and repository that has a per-chat binding here, whether or not it holds a session now. Read without the writer fence: only scope fields are used.</summary>
    private static HashSet<(string Provider, string NativeId, string Machine, string Repository)> ChatScopes(string folder, string identity, string sessions)
    {
        var scopes = new HashSet<(string, string, string, string)>();
        foreach (var path in StateFiles(sessions, ChatFolder))
            if (TryPeek(path, folder, identity) is { } state) scopes.Add(Scope(state));
        return scopes;
    }

    /// <summary>A binding's state read without its writer fence, for scope fields only; null when it cannot be read or verified.</summary>
    private static State? TryPeek(string path, string folder, string identity)
    {
        try
        {
            RequirePrivate(path, MaxStateBytes);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxStateBytes) return null;
            var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
            return ParseState(bytes, path, folder, identity);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException or KeyNotFoundException or FormatException) { return null; }
    }

    private sealed record StoredRecord(DispatchRecord Record, State State, bool Unknown);

    /// <summary>One binding's state with a verified current handle, read under its writer fence; null when it is busy, foreign, invalid or holds no session.</summary>
    private static StoredRecord? ReadRecord(string folder, string identity, string path)
    {
        try
        {
            // Read under the existing writer fence. A busy/replaced record is retried by the next observation.
            var lockPath = Path.ChangeExtension(path, ".lock");
            RequirePrivate(lockPath, 0);
            using var reader = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            RequirePrivate(path, MaxStateBytes);
            var state = ParseState(File.ReadAllBytes(path), path, folder, identity);
            if (state.Current is not { } id) return null;
            var snapshot = new NativeJoinBinding(folder, state, identity, reader);
            if (snapshot.CurrentHandle is not { } current || current.SessionId != id) return null;
            return new(new(path, current, snapshot.Generation, File.GetLastWriteTimeUtc(path), state.Version == StateVersion), state, UnknownSession(path) == current.SessionId);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException or KeyNotFoundException or FormatException) { return null; }
    }

    /// <summary>
    /// A binding's stored state. <see cref="Key"/> is its identity (<see cref="BindingKey"/>); <see cref="Workspace"/> is the checkout the hub last
    /// recorded; <see cref="Unrecorded"/> is a later checkout an older hub would not record.
    /// </summary>
    private sealed record State(int Version, string FileKey, string Key, string Provider, string NativeId, string Machine, string Repository,
        string Workspace, JsonElement Arguments, JsonElement? Resolution, string? Current, string? Unrecorded = null)
    {
        /// <summary>The checkout the chat last joined from, recorded by the hub or not.</summary>
        internal string InUse => Unrecorded ?? Workspace;
    }

    /// <summary>A chat's one binding per repository, for this private connection and machine. No checkout is part of it.</summary>
    private static string ChatKey(string folder, string identity, string provider, string nativeId, string machine, string repository) =>
        Digest("cocern-native-binding-v2", folder, identity, provider, nativeId, machine, repository);

    /// <summary>The older per-checkout binding key. Such files are still read, and adopted by the chat's next join, never written.</summary>
    private static string CheckoutKey(string folder, string identity, string provider, string nativeId, string machine, string repository, string workspace) =>
        Digest("cocern-native-binding-v1", folder, identity, provider, nativeId, machine, repository, workspace);

    /// <summary>
    /// Validates a stored binding against its own path and this connection: version 1 in <see cref="CheckoutFolder"/> (per checkout, keyed by its
    /// frozen checkout) or version 2 in <see cref="ChatFolder"/> (per chat and repository, whose identity is its own key or the key of the version 1
    /// binding it adopted). Throws when invalid.
    /// </summary>
    private static State ParseState(byte[] bytes, string path, string folder, string identity)
    {
        var fileName = Path.GetFileName(path);
        var expected = Path.GetFileName(Path.GetDirectoryName(path)) == ChatFolder ? StateVersion : 1;
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        var root = Object(document.RootElement, "version", "scope", "key", "workspace", "unrecorded", "openArguments", "currentSessionId", "resolution");
        var args = root["openArguments"].Clone();
        var native = args.GetProperty("nativeJoin");
        var provider = Text(native.GetProperty("provider")); var nativeId = Text(native.GetProperty("nativeId"));
        var anchor = Text(native.GetProperty("workspace")); var machine = Text(args.GetProperty("machineId")); var repo = Text(args.GetProperty("repositoryId"));
        if (provider is not ("claude" or "codex") || !TryNativeId(nativeId, out var parsed) || parsed.ToString("D") != nativeId || !Path.IsPathFullyQualified(anchor))
            throw Failure("The private native binding is invalid.");
        ValidateIdentifier(machine); ValidateIdentifier(repo); ValidateArguments(args, provider, nativeId, machine, repo, anchor);
        var legacy = CheckoutKey(folder, identity, provider, nativeId, machine, repo, anchor);
        var version = root["version"].GetInt32();
        if (version != expected) throw Failure("The private native binding version is invalid.");
        string fileKey, key, workspace;
        string? unrecorded = null;
        if (version == 1 && !root.ContainsKey("key") && !root.ContainsKey("workspace") && !root.ContainsKey("unrecorded")) (fileKey, key, workspace) = (legacy, legacy, anchor);
        else if (version == StateVersion && root.TryGetValue("key", out var storedKey) && root.TryGetValue("workspace", out var storedWorkspace))
        {
            (fileKey, key, workspace) = (ChatKey(folder, identity, provider, nativeId, machine, repo), Text(storedKey), Text(storedWorkspace));
            if (root.TryGetValue("unrecorded", out var skipped)) unrecorded = Text(skipped);
            if (key != fileKey && key != legacy || !ValidCheckout(workspace) || unrecorded is not null && !ValidCheckout(unrecorded))
                throw Failure("The private native binding is invalid.");
        }
        else throw Failure("The private native binding version is invalid.");
        if (fileName != fileKey + ".json" || Text(root["scope"]) != fileKey) throw Failure("The private native binding belongs to another scope.");
        JsonElement? evidence = null;
        if (root.TryGetValue("resolution", out var prior))
        {
            if (!CoCern.Contracts.NativeGitOrigin.ValidCheckoutEvidence(prior)) throw Failure("The private native checkout evidence is invalid.");
            evidence = prior.Clone();
        }
        string? current = null;
        if (root.TryGetValue("currentSessionId", out var value) && !NodeState.IsFileId(current = Text(value))) throw Failure("The private native session reference is invalid.");
        return new(version, fileKey, key, provider, nativeId, machine, repo, workspace, args, evidence, current, unrecorded);
    }

    private static bool ValidCheckout(string path) => path.Length <= 32768 && !path.Any(char.IsControl) && Path.IsPathFullyQualified(path);

    /// <summary>
    /// A chat that ran in several checkouts of a repository before bindings were per chat has one older binding per checkout, each with its own
    /// session. Its first join since adopts the one dispatch would choose (<see cref="Latest"/>, preferring one the hub has not answered
    /// <c>SessionUnknown</c> for): its frozen proof, identity and session reference are copied unchanged, so the hub resumes that same session and
    /// its handle stays owned. Its evidence and checkout stay as the hub last heard them; a join from another checkout is then a move. The older files
    /// are left as they are and are no longer dispatched. Null when the chat has no older binding with a session here.
    /// <para>An older binding of this chat that another (older) adapter holds right now cannot be read. Adoption then waits for a later join
    /// (a retryable refusal) instead of minting a second identity, which would give the chat a second hub session.</para>
    /// </summary>
    private static State? AdoptLegacy(string folder, string identity, string sessions, string statePath, string fileKey,
        string provider, string nativeId, string machine, string repository)
    {
        var scope = (provider, nativeId, machine, repository);
        var candidates = new List<StoredRecord>();
        foreach (var path in StateFiles(sessions, CheckoutFolder))
        {
            if (ReadRecord(folder, identity, path) is { } read) { if (Scope(read.State) == scope) candidates.Add(read); }
            else if (TryPeek(path, folder, identity) is { } busy && Scope(busy) == scope && busy.Current is not null && Held(Path.ChangeExtension(path, ".lock")))
                throw new NativeJoin.Refused(LegacyBindingBusy);
        }
        var chosen = candidates.OrderBy(read => read.Unknown).ThenByDescending(read => read.Record.Confirmed).ThenByDescending(read => read.Record.Handle.OpenedAt)
            .ThenBy(read => read.Record.Path, StringComparer.Ordinal).FirstOrDefault();
        if (chosen is null) return null;
        if (chosen.Unknown)
            NodeState.WritePrivately(Path.ChangeExtension(statePath, UnknownExtension), JsonSerializer.SerializeToUtf8Bytes(new { version = 1, sessionId = chosen.Record.Handle.SessionId }));
        return chosen.State with { Version = StateVersion, FileKey = fileKey };
    }

    /// <summary>Whether a writer holds this lease now (a sharing violation), as opposed to it being missing or unreadable.</summary>
    private static bool Held(string lockPath)
    {
        try { using var reader = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.Read); return false; }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    // Only native dispatchers require a live binding. Legacy routes keep their existing checks.
    internal static IWakeDispatcher ForDispatch(string folder, SessionHandle handle, IWakeDispatcher inner) =>
        handle.NativeBindingKey is null ? inner : new FencedDispatcher(folder, handle, inner);

    private sealed class FencedDispatcher(string folder, SessionHandle expected, IWakeDispatcher inner) : IWakeDispatcher, IWakeDispatchFence
    {
        public bool IsBusy(string thread) => inner.IsBusy(thread);
        public Task<bool> QueueAsync(string thread, string prompt, CancellationToken ct) => inner.QueueAsync(thread, prompt, ct);

        public IDisposable? TryAcquire(string thread)
        {
            if (expected.Chat?.ThreadId != thread) return null;
            var matches = DispatchRecords(folder).Where(row => row.Handle.Chat == expected.Chat).Take(2).ToArray();
            if (matches.Length != 1 || matches[0].Handle != expected) return null;
            var path = matches[0].Path;
            FileStream? binding = null, handle = null;
            try
            {
                // A writer replaces the binding under its private .lock. Also pin the exact private handle file
                // so an update during a slow external queue cannot swap its credential/location on Windows.
                binding = new FileStream(Path.ChangeExtension(path, ".lock"), FileMode.Open, FileAccess.Read, FileShare.Read);
                handle = new FileStream(NodeState.SessionHandlePath(folder, expected.SessionId), FileMode.Open, FileAccess.Read, FileShare.Read);
                if (DispatchRecords(folder, path).SingleOrDefault()?.Handle != expected) return null;
                var guard = new DispatchGuard(binding, handle);
                binding = handle = null;
                return guard;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
            finally { handle?.Dispose(); binding?.Dispose(); }
        }
    }

    private sealed class DispatchGuard(FileStream binding, FileStream handle) : IDisposable
    {
        public void Dispose() { try { handle.Dispose(); } finally { binding.Dispose(); } }
    }
    internal string Secret { get { CheckLease(); return arguments.GetProperty("nativeJoin").GetProperty("bindingSecret").GetString()!; } }
    internal string Generation { get { CheckLease(); return arguments.GetProperty("nativeJoin").GetProperty("generation").GetString()!; } }
    internal string OpenRequestId { get { CheckLease(); return arguments.GetProperty("requestId").GetString()!; } }
    internal string OwnershipKey => Digest("cocern-native-owner-v1", BindingKey, Secret, Generation, OpenRequestId);
    internal JsonElement FrozenOpenArguments { get { CheckLease(); return arguments.Clone(); } }
    internal string? CurrentHandleReference { get { CheckLease(); return currentSessionId; } }
    internal SessionHandle? CurrentHandle
    {
        get
        {
            CheckLease();
            if (currentSessionId is null) return null;
            var handle = OwnedHandle(currentSessionId);
            return handle ?? throw Failure("The private native session reference could not be verified.");
        }
    }

    private NativeJoinBinding(string folder, State state, string identity, FileStream lease)
    {
        (Folder, Provider, NativeId, RepositoryId, Workspace, savedWorkspace, unrecorded) = (folder, state.Provider, state.NativeId, state.Repository, state.InUse, state.Workspace, state.Unrecorded);
        (credentialIdentity, fileKey, BindingKey, arguments, resolution, currentSessionId, owner) =
            (identity, state.FileKey, state.Key, state.Arguments.Clone(), state.Resolution?.Clone(), state.Current, lease);
        stateFolder = state.Version == StateVersion ? ChatFolder : CheckoutFolder;
    }

    /// <summary>
    /// The hub refused this open's <see cref="CurrentWorkspaceField"/> (a hub from before per-chat sessions) and the open is retried without it.
    /// The session stays the chat's; the hub still records the checkout the proof was made in. The completing save keeps the last recorded
    /// checkout and notes this one as unrecorded, so it is not reported again until the chat moves on.
    /// </summary>
    internal void CheckoutNotRecorded()
    {
        CheckLease();
        recordingRefused = Workspace;
    }

    /// <summary>
    /// The chat now works in <paramref name="workspace"/>, a verified checkout of this repository. Nothing is written: the move is saved with the
    /// hub's answer to the join that reports it (<see cref="Complete"/>), so a refused or lost join is retried, and the previous checkout's frozen
    /// evidence no longer applies.
    /// </summary>
    private void MoveTo(string workspace)
    {
        if (workspace == Workspace) return;
        Workspace = workspace;
        resolution = null;
    }

    /// <summary>
    /// Writes this binding's state. With <paramref name="answered"/> the hub has just answered an open from the current checkout, which it recorded
    /// unless it refused the field (<see cref="CheckoutNotRecorded"/>); otherwise the recorded checkout is unchanged.
    /// </summary>
    private void Save(JsonElement frozen, string? current, JsonElement? evidence, bool answered)
    {
        var (recorded, skipped) = !answered ? (savedWorkspace, unrecorded) : recordingRefused == Workspace ? (savedWorkspace, Workspace) : (Workspace, null);
        Write(StatePath, fileKey, BindingKey, recorded, skipped, frozen, current, evidence);
        (savedWorkspace, unrecorded) = (recorded, skipped);
    }

    /// <summary>Provider names are at most 16 characters; native IDs accept only UUID N (32) or D (36) forms.</summary>
    internal static async Task<NativeJoinBinding> AcquireAsync(string folder, string provider, string nativeId,
        string machineId, string repositoryId, string workspace, CancellationToken ct, bool existingOnly = false,
        Func<string, CancellationToken, Task<string>>? resolveCheckout = null, Func<NativeJoinBinding, bool>? authenticate = null,
        Func<string, string>? normalizeWorkspace = null)
    {
        ct.ThrowIfCancellationRequested();
        if (provider is not { Length: > 0 and <= 16 } || nativeId is not { Length: 32 or 36 })
            throw new ArgumentException("A bounded provider and N/D native UUID are required.");
        provider = provider.ToLowerInvariant();
        if (provider is not ("codex" or "claude") || !TryNativeId(nativeId, out var id))
            throw new ArgumentException("A supported provider and native chat UUID are required.");
        nativeId = id.ToString("D");
        folder = CanonicalDirectory(folder);
        ValidateIdentifier(machineId); ValidateIdentifier(repositoryId);
        if (authenticate is not null && !existingOnly) throw Failure("Authenticated lookup cannot create a binding.");
        workspace = authenticate is null ? await (resolveCheckout ?? ResolveCheckoutAsync)(workspace, ct) : OpaqueSignedWorkspace(workspace);
        var identity = ConnectionIdentity(folder);
        // One binding per chat and repository: the checkout is verified above and recorded, never part of the key.
        var key = ChatKey(folder, identity, provider, nativeId, machineId, repositoryId);
        var sessions = Path.Combine(folder, NodeState.SessionsFolder);
        if (!existingOnly) Directory.CreateDirectory(sessions);
        RejectLink(sessions);
        var directory = Path.Combine(sessions, ChatFolder);
        if (!existingOnly) Directory.CreateDirectory(directory);
        RejectLink(directory);
        var statePath = Path.Combine(directory, key + ".json");
        if (existingOnly) RequirePrivate(statePath, MaxStateBytes);
        var lease = await LockAsync(Path.ChangeExtension(statePath, ".lock"), ct, existingOnly);
        FileStream? fence = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (ConnectionIdentity(folder) != identity) throw Failure("The private connection changed while joining.");
            RejectLink(statePath, allowMissing: true);
            State state;
            if (File.Exists(statePath))
            {
                RequirePrivate(statePath, MaxStateBytes);
                try { state = ParseState(File.ReadAllBytes(statePath), statePath, folder, identity); }
                catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
                { throw Failure("The private native binding is corrupt; it was left unchanged."); }
                if (state.Version != StateVersion || state.Provider != provider || state.NativeId != nativeId || state.Machine != machineId || state.Repository != repositoryId)
                    throw Failure("The private native binding is invalid.");
            }
            else
            {
                if (existingOnly) throw Failure("The private native binding does not exist.");
                if (AdoptLegacy(folder, identity, sessions, statePath, key, provider, nativeId, machineId, repositoryId) is { } adopted) state = adopted;
                else
                {
                    var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                    var generation = Guid.NewGuid().ToString("N");
                    var requestId = Guid.NewGuid().ToString("N");
                    // The proof keeps the checkout it was made in: the hub's participant derives from it, so it must never change.
                    var frozen = JsonSerializer.SerializeToElement(new { repositoryId, machineId, requestId,
                        nativeJoin = new { provider, nativeId, workspace, bindingSecret = secret, generation } });
                    state = new(StateVersion, key, key, provider, nativeId, machineId, repositoryId, workspace, frozen, null, null);
                }
                ct.ThrowIfCancellationRequested();
                Write(statePath, key, state.Key, state.Workspace, state.Unrecorded, state.Arguments, state.Current, state.Resolution);
            }
            // An adopted binding shares its proof and session with the older file it came from. While this lease lasts, also hold that file's
            // lease, so an older adapter still running here can neither advance nor supersede the same session underneath this one.
            var adoptedLock = Path.Combine(sessions, CheckoutFolder, state.Key + ".lock");
            if (state.Key != state.FileKey && File.Exists(adoptedLock)) fence = await LockAsync(adoptedLock, ct, existingOnly: true);
            var binding = new NativeJoinBinding(folder, state, identity, lease) { adoptedFence = fence };
            // A creating join from another checkout of the repository is a move; lookups keep the checkout the hub last heard.
            if (!existingOnly) binding.MoveTo(workspace);
            if (authenticate is not null)
            {
                // Only private-store reads and the existing lease precede authentication. In particular,
                // never follow an unauthenticated checkout (including a UNC path) to find its key.
                if (!authenticate(binding)) throw Failure("The native invocation could not be authenticated.");
                var physical = resolveCheckout is null ? await ResolveCheckoutAsync(workspace, ct, normalizeWorkspace) : await resolveCheckout(workspace, ct);
                if (physical != workspace)
                    throw Failure("The authenticated native checkout changed.");
            }
            _ = binding.CurrentHandle; // A missing/foreign active handle is not an invitation to choose another one.
            return binding;
        }
        catch
        {
            if (fence is not null) await fence.DisposeAsync();
            await lease.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// The chat's one established binding on this machine that last joined from <paramref name="workspace"/>: a chat that moved has no established
    /// binding here until its next join reports the move. Older per-checkout bindings are not established; the chat's next join adopts one.
    /// </summary>
    internal static async Task<NativeJoinBinding> AcquireEstablishedAsync(string folder, string provider, string nativeId,
        string machineId, string workspace, CancellationToken ct, Func<string, CancellationToken, Task<string>>? resolveCheckout = null)
    {
        if (provider is not ("claude" or "codex") || !TryNativeId(nativeId, out var id)) throw Failure("A native capture is required.");
        nativeId = id.ToString("D");
        folder = CanonicalDirectory(folder);
        workspace = await (resolveCheckout ?? ResolveCheckoutAsync)(workspace, ct); // Trusted provider event, never a tool stamp.
        var identity = ConnectionIdentity(folder);
        var directory = Path.Combine(folder, NodeState.SessionsFolder, ChatFolder);
        RejectLink(Path.GetDirectoryName(directory)!, allowMissing: true); RejectLink(directory, allowMissing: true);
        if (!Directory.Exists(directory))
            throw new MissingEstablishedBinding();
        var paths = Directory.EnumerateFiles(directory, "*.json").Take(2049).ToArray();
        if (paths.Length > 2048) throw Failure("The established native binding lookup exceeds its bound.");
        var repositories = new List<string>();
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            RequirePrivate(path, MaxStateBytes);
            // Membership is fenced by the caller's existing intent stripe. Read immutable scope
            // fields without denying replacement by unrelated participants; never skip a busy candidate.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > MaxStateBytes) throw Failure("The private native binding exceeds its bound.");
            var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
            State state;
            // Foreign/stale credential scopes, other chats' records and unreadable ones are not this chat's established binding. A corrupt record
            // of this chat is refused by the join that follows a missing binding, never replaced.
            try { state = ParseState(bytes, path, folder, identity); }
            catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException) { continue; }
            if (state.Provider != provider || state.NativeId != nativeId || state.Machine != machineId || state.Current is null || state.InUse != workspace) continue;
            repositories.Add(state.Repository);
        }
        var held = new List<NativeJoinBinding>();
        try
        {
            foreach (var repository in repositories.Order(StringComparer.Ordinal))
                held.Add(await AcquireAsync(folder, provider, nativeId, machineId, repository, workspace, ct,
                    existingOnly: true, resolveCheckout: (_, _) => Task.FromResult(workspace)));
            var matches = held.Where(binding => binding.CurrentHandle is not null && binding.Workspace == workspace).ToArray();
            if (matches.Length == 0) throw new MissingEstablishedBinding();
            if (matches.Length != 1) throw Failure("One established native binding is required.");
            var selected = matches[0]; held.Remove(selected);
            return selected;
        }
        finally { foreach (var binding in held) await binding.DisposeAsync(); }
    }

    internal bool MatchesFolder(string folder) => CanonicalDirectory(folder) == Folder;
    internal bool MatchesClient(ChatLocation? client) => client is not null && client.Provider == Provider &&
        TryNativeId(client.ThreadId, out var id) && id.ToString("D") == NativeId;
    internal bool MatchesConnection(AdapterOptions authenticatedOptions)
    {
        CheckLease();
        return CredentialDigest(NodeState.HasIdentityFiles(Folder) ? "node" : "local", authenticatedOptions.BaseUri.AbsoluteUri,
            authenticatedOptions.PinnedFingerprint, authenticatedOptions.Token) == credentialIdentity;
    }

    // Only SessionHandoff's authenticated exact open may advance the frozen pair after SessionUnknown.
    internal bool AdvanceExpired(JsonElement expected)
    {
        CheckLease();
        if (expected.GetRawText() != arguments.GetRawText()) return false;
        var next = System.Text.Json.Nodes.JsonNode.Parse(expected.GetRawText())!.AsObject();
        next["requestId"] = Guid.NewGuid().ToString("N");
        next["nativeJoin"]!["generation"] = Guid.NewGuid().ToString("N");
        var frozen = JsonSerializer.SerializeToElement(next);
        var adoptionPath = Path.ChangeExtension(StatePath, ".adopt");
        if (File.Exists(adoptionPath)) { RequirePrivate(adoptionPath, 1024); File.Delete(adoptionPath); }
        Save(frozen, null, null, answered: false);
        arguments = frozen;
        resolution = null;
        currentSessionId = null;
        ClearUnknown();
        return true;
    }

    // Only SessionHandoff's authenticated-open boundary creates an ownership marker and calls this method.
    internal bool Complete(SessionHandle handle)
    {
        CheckLease();
        if (currentSessionId is not null && currentSessionId != handle.SessionId) return false;
        var saved = OwnedHandle(handle.SessionId);
        if (saved is null || saved.SessionToken != handle.SessionToken) return false;
        Save(arguments, handle.SessionId, resolution, answered: true); // Also records a move the hub just heard.
        currentSessionId = handle.SessionId;
        ClearUnknown(); // The hub just answered this exact open with this session.
        try { File.Delete(Path.ChangeExtension(StatePath, ".adopt")); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } // A confirmed reference already preserves identity; expiry clears a retained sidecar.
        return true;
    }

    // Only SessionHandoff's authenticated-open boundary calls this, after the unchanged frozen open was answered with a session
    // other than the referenced one. The frozen arguments stay; only the reference moves, and only this binding's own dead handle is retired.
    internal bool Supersede(SessionHandle handle, string previousSessionId, bool retirePrevious)
    {
        CheckLease();
        if (currentSessionId is null || currentSessionId != previousSessionId || previousSessionId == handle.SessionId) return false;
        var saved = OwnedHandle(handle.SessionId);
        if (saved is null || saved.SessionToken != handle.SessionToken) return false;
        SessionHandle? retired = null;
        try { retired = OwnedHandle(previousSessionId); }
        catch (IOException) { } // An unverifiable old file is not this binding's to remove; leave it unreferenced.
        Save(arguments, handle.SessionId, resolution, answered: true);
        currentSessionId = handle.SessionId;
        ClearUnknown();
        if (retirePrevious && retired is not null)
        {
            try { File.Delete(NodeState.SessionHandlePath(Folder, previousSessionId)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } // The reference already moved; an unreferenced native handle is inert.
        }
        return true;
    }

    internal SessionHandle? OwnedHandle(string sessionId)
    {
        CheckLease();
        var path = NodeState.SessionHandlePath(Folder, sessionId);
        if (!File.Exists(path)) return null;
        RequirePrivate(path, 64 * 1024);
        var handle = NodeState.ReadSessionHandle(path);
        return handle is not null && handle.NativeBindingKey == OwnershipKey && handle.RepositoryId == RepositoryId &&
            MatchesClient(handle.Client) && MatchesClient(handle.Chat) && !handle.Unmapped ? handle : null;
    }

    // Only a private cache entry for this exact genuine provider chat may be offered for adoption.
    // Freeze its ID separately from the opening proof, so a lost reply or a missing file cannot turn a retry into a fresh allocation.
    internal SessionHandle? AdoptionHandle()
    {
        CheckLease();
        if (currentSessionId is not null) return null;
        var intentPath = Path.ChangeExtension(StatePath, ".adopt");
        string? selected = null;
        if (File.Exists(intentPath))
        {
            RequirePrivate(intentPath, 1024);
            try
            {
                using var intent = JsonDocument.Parse(File.ReadAllBytes(intentPath));
                var value = Object(intent.RootElement, "version", "sessionId");
                if (value["version"].GetInt32() != 1 || !NodeState.IsFileId(selected = Text(value["sessionId"])))
                    throw Failure("The private native adoption intent is invalid.");
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
            { throw Failure("The private native adoption intent is invalid."); }
        }
        var directory = Path.Combine(Folder, NodeState.SessionsFolder);
        RejectLink(directory);
        var paths = Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.json").Take(2049).ToArray() : [];
        if (paths.Length > 2048) throw Failure("The classic handle lookup exceeds its bound.");
        var matches = new List<SessionHandle>();
        foreach (var path in paths)
        {
            RequirePrivate(path, 64 * 1024);
            var handle = NodeState.ReadSessionHandle(path);
            if (handle is null || !string.Equals(handle.RepositoryId, RepositoryId, StringComparison.OrdinalIgnoreCase) || handle.Unmapped || !MatchesClient(handle.Chat) ||
                handle.Client is not null && !MatchesClient(handle.Client)) continue;
            if (handle.NativeBindingKey is not null && handle.NativeBindingKey != OwnershipKey) continue; // Prior native generations are not classic adoption candidates.
            matches.Add(handle);
        }
        if (matches.Count > 1 || selected is not null && (matches.Count != 1 || matches[0].SessionId != selected))
            throw Failure("The classic adoption handle is missing or ambiguous.");
        if (matches.Count == 0) return null;
        var result = matches[0];
        if (selected is null) NodeState.WritePrivately(intentPath, JsonSerializer.SerializeToUtf8Bytes(new { version = 1, sessionId = result.SessionId }));
        return result;
    }

    private void CheckLease()
    {
        ObjectDisposedException.ThrowIf(owner is null, this);
        if (ConnectionIdentity(Folder) != credentialIdentity) throw Failure("The private connection changed while joining.");
    }

    internal static async Task<FileStream> LockAsync(string path, CancellationToken ct, bool existingOnly = false)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            FileStream? lease = null;
            try
            {
                if (File.Exists(path)) { RejectLink(path); lease = new(path, FileMode.Open, FileAccess.Write, FileShare.None); }
                else if (existingOnly) throw Failure("The private native lease does not exist.");
                else lease = NodeState.CreatePrivateFile(path);
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 11 or 32 or 33 or 80 or 183)
            {
                if (elapsed.Elapsed > TimeSpan.FromSeconds(30)) throw Failure("Another native join still owns this binding.");
                await Task.Delay(25, ct);
                continue;
            }
            try { RequirePrivate(path, 0); return lease; }
            catch { await lease.DisposeAsync(); throw; }
        }
    }

    /// <param name="evidence">The checkout evidence to freeze on first use.</param>
    /// <param name="displayLabel">The chat's title for this attempt: display text sent beside the proof, never part of the frozen arguments or the identity.</param>
    /// <param name="protectedRoot">The physical root proven for this attempt: on the wire only, never in the frozen arguments, the state file or the participant proof.</param>
    internal JsonElement OpenWireArguments(JsonElement? evidence, string? displayLabel = null, string? protectedRoot = null)
    {
        CheckLease();
        if (evidence is { } offered)
        {
            var same = resolution is not { } previous || JsonElement.DeepEquals(previous, offered);
            if (!same && resolution is { } saved && !saved.TryGetProperty("repositoryId", out _) && offered.TryGetProperty("repositoryId", out var addedRepository) &&
                string.Equals(addedRepository.GetString(), RepositoryId, StringComparison.OrdinalIgnoreCase))
            {
                var original = System.Text.Json.Nodes.JsonNode.Parse(offered.GetRawText())!.AsObject();
                original.Remove("repositoryId");
                same = JsonElement.DeepEquals(saved, JsonSerializer.SerializeToElement(original));
            }
            if (!CoCern.Contracts.NativeGitOrigin.ValidCheckoutEvidence(offered) ||
                offered.TryGetProperty("repositoryId", out var repository) && !string.Equals(repository.GetString(), RepositoryId, StringComparison.OrdinalIgnoreCase) || !same)
                throw Failure("The private native opening evidence changed.");
            if (resolution is null)
            {
                // A move's evidence is saved only with the hub's answer (Complete), together with the checkout it belongs to.
                if (!Moved) Save(arguments, currentSessionId, offered, answered: false);
                resolution = offered.Clone();
            }
        }
        var anchor = arguments.GetProperty("nativeJoin").GetProperty("workspace").GetString();
        if (resolution is null && displayLabel is null && protectedRoot is null && Workspace == anchor) return arguments.Clone();
        var wire = System.Text.Json.Nodes.JsonNode.Parse(arguments.GetRawText())!.AsObject();
        if (resolution is { } frozen) wire["nativeJoin"]!["resolution"] = System.Text.Json.Nodes.JsonNode.Parse(frozen.GetRawText());
        if (displayLabel is not null) wire["label"] = displayLabel;
        // On the wire only: never in the frozen arguments, the state file or the participant proof, so a retry and an older state file are unaffected.
        if (protectedRoot is not null) wire["nativeJoin"]![ProtectedCheckoutRoot.Field] = protectedRoot;
        // The checkout the chat is in now, when it is not the one the proof was made in. Also outside the proof: the hub records it as the join's working copy.
        if (Workspace != anchor) wire["nativeJoin"]![CurrentWorkspaceField] = Workspace;
        return JsonSerializer.SerializeToElement(wire);
    }

    private static void Write(string path, string fileKey, string key, string workspace, string? unrecorded, JsonElement frozen, string? current, JsonElement? resolution)
    {
        var state = new Dictionary<string, object?> { ["version"] = StateVersion, ["scope"] = fileKey, ["key"] = key, ["workspace"] = workspace, ["openArguments"] = frozen };
        if (unrecorded is not null) state["unrecorded"] = unrecorded;
        if (resolution is not null) state["resolution"] = resolution;
        if (current is not null) state["currentSessionId"] = current;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state);
        if (bytes.Length > MaxStateBytes) throw Failure("The private native opening attempt exceeds its bound.");
        NodeState.WritePrivately(path, bytes);
    }

    private static void ValidateArguments(JsonElement frozen, string provider, string nativeId, string machine, string repo, string workspace)
    {
        var args = Object(frozen, "repositoryId", "machineId", "requestId", "nativeJoin");
        var native = Object(args["nativeJoin"], "provider", "nativeId", "workspace", "bindingSecret", "generation");
        Span<byte> secret = stackalloc byte[32];
        if (args.Count != 4 || native.Count != 5 || Text(args["repositoryId"]) != repo || Text(args["machineId"]) != machine ||
            !Guid.TryParseExact(Text(args["requestId"]), "N", out _) || Text(native["provider"]) != provider ||
            Text(native["nativeId"]) != nativeId || Text(native["workspace"]) != workspace ||
            !Guid.TryParseExact(Text(native["generation"]), "N", out _) ||
            !Convert.TryFromBase64String(Text(native["bindingSecret"]), secret, out var count) || count != 32)
            throw Failure("The private native opening attempt is invalid.");
    }

    private static Dictionary<string, JsonElement> Object(JsonElement value, params string[] keys)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Failure("The private native binding is invalid.");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateObject())
            if (!keys.Contains(item.Name, StringComparer.Ordinal) || !fields.TryAdd(item.Name, item.Value)) throw Failure("The private native binding is invalid.");
        return fields;
    }
    private static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Failure("The private native binding is invalid.");
    private static void ValidateIdentifier(string value)
    {
        if (value is not { Length: > 0 and <= NodeState.MaxIdentifierLength } || string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)) throw new ArgumentException("A bounded native scope identifier is required.");
    }
    private static bool TryNativeId(string value, out Guid id)
    {
        id = default;
        return value is { Length: 32 or 36 } && Guid.TryParseExact(value, value.Length == 32 ? "N" : "D", out id);
    }
    private static Task<string> ResolveCheckoutAsync(string workspace, CancellationToken ct) => ResolveCheckoutAsync(workspace, ct, null);

    private static async Task<string> ResolveCheckoutAsync(string workspace, CancellationToken ct, Func<string, string>? normalize)
    {
        workspace = CanonicalDirectory(workspace, normalize);
        var checkout = await Task.Run(() => CoordinationTools.NativeGitTopLevel(workspace), ct).WaitAsync(ct);
        if (checkout is null) throw Failure("The native event checkout could not be verified with Git.");
        checkout = CanonicalDirectory(checkout, normalize);
        var prefix = Path.EndsInDirectorySeparator(checkout) ? checkout : checkout + Path.DirectorySeparatorChar;
        if (!string.Equals(workspace, checkout, StringComparison.Ordinal) && !workspace.StartsWith(prefix, StringComparison.Ordinal))
            throw Failure("Git returned a checkout outside the native event directory.");
        return checkout;
    }

    private static string OpaqueSignedWorkspace(string path)
    {
        if (path.Length is not (> 0 and <= 32768) || path.Any(char.IsControl)) throw new ArgumentException("A bounded signed checkout spelling is required.");
        // Do not call any path-normalization API here. GetFullPath itself can expand short UNC
        // names on Windows. Only a verbatim spelling matching the private key can authenticate.
        return path;
    }

    private static string LexicalDirectory(string path, Func<string, string>? normalize = null)
    {
        if (!Path.IsPathFullyQualified(path) || path.Length > 32768 || path.Any(char.IsControl)) throw new ArgumentException("An absolute native event directory is required.");
        return Path.TrimEndingDirectorySeparator(normalize is null ? Path.GetFullPath(path) : normalize(path));
    }

    private static string CanonicalDirectory(string path, Func<string, string>? normalize = null)
    {
        path = LexicalDirectory(path, normalize);
        if (!Directory.Exists(path)) throw Failure("The private connection or native checkout directory is missing.");
        RejectLink(path);
        if (OperatingSystem.IsWindows())
        {
            // Resolve the actual directory instead of uppercasing: NTFS can enable case sensitivity per directory.
            using var handle = OpenDirectory(path, 0, 7, nint.Zero, 3, 0x02000000, nint.Zero);
            if (handle.IsInvalid) throw Failure("The native directory could not be canonicalized.");
            var name = new StringBuilder(32768);
            var length = FinalDirectory(handle, name, (uint)name.Capacity, 0);
            if (length == 0 || length >= name.Capacity) throw Failure("The native directory could not be canonicalized.");
            path = name.ToString();
            if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) path = @"\\" + path[8..];
            else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
            path = Path.TrimEndingDirectorySeparator(path);
        }
        else
        {
            // A lexical Unix path does not identify its physical directory when any ancestor is a symlink.
            // Refuse such paths rather than opening a second lock namespace through an alias.
            for (DirectoryInfo? ancestor = new(path); ancestor is not null; ancestor = ancestor.Parent) RejectLink(ancestor.FullName);
        }
        return path;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle OpenDirectory(string path, uint access, uint share, nint security, uint mode, uint flags, nint template);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint FinalDirectory(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
    private static void RejectLink(string path, bool allowMissing = false)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw Failure("Linked private native binding paths are not supported.");
        }
        catch (FileNotFoundException) when (allowMissing) { }
        catch (DirectoryNotFoundException) when (allowMissing) { }
    }
    private static void RequirePrivate(string path, int maximum)
    {
        RejectLink(path);
        if (new FileInfo(path).Length > maximum || !NodeState.IsPrivateFile(path)) throw Failure("The native binding file is not private or exceeds its bound.");
    }
    private static string ConnectionIdentity(string folder) =>
        (NodeState.HasIdentityFiles(folder) ? NodeState.NativeCredentialIdentity(folder) : LocalWakeConnection.NativeCredentialIdentity(folder)) ??
        throw Failure("A verified private connection is required for native recovery.");
    internal static string CredentialDigest(string kind, string hub, string? pin, string token) =>
        Digest(kind, hub, pin ?? "", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))));
    private static string Digest(params string[] fields)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true);
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        foreach (var field in fields) { var encoded = utf8.GetBytes(field); writer.Write(encoded.Length); writer.Write(encoded); }
        writer.Flush();
        return Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()));
    }
    private static IOException Failure(string message) => new(message);
    public async ValueTask DisposeAsync()
    {
        var fence = Interlocked.Exchange(ref adoptedFence, null);
        if (fence is not null) await fence.DisposeAsync();
        var lease = Interlocked.Exchange(ref owner, null);
        if (lease is not null) await lease.DisposeAsync();
    }
    public override string ToString() => "Native join binding (private)";
}
