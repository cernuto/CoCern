using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoCern.Contracts;

namespace CoCern.Adapter.Node;

internal enum EditClaimDirt { Unknown, Clean, Dirty }

/// <summary>One saved PRE attempt for one path. <c>ObservedActivity</c> is set only by a successful POST with changed content and Git dirty proof.</summary>
internal sealed record EditClaimAttempt(string IntentId, string RelativePath, string? BeforeFingerprint, string? AfterFingerprint, DateTimeOffset? ObservedActivity, DateTimeOffset PreparedAt, bool Settled);

/// <summary>One claim, meaning one path at one generation, however many intents share it. No token, proof or file content is stored, only SHA-256 fingerprints.</summary>
internal sealed record EditClaimTracking(string RelativePath, long Generation, DateTimeOffset LeaseUntil, long RenewSequence = 0, bool RenewPending = false, string? ReleaseOutcome = null, string? OwnFingerprint = null);

/// <summary>Read fresh at every use. Anything that cannot be proven answers null or <see cref="EditClaimDirt.Unknown"/>.</summary>
internal interface IEditClaimPhysicalState
{
    string? Fingerprint(string relativePath);
    EditClaimDirt Status(string relativePath);
    string? RelativeKey(string reportedPath);
}

internal interface IEditClaimLifecycleTransport
{
    Task<AdapterReply> RenewAsync(string relativePath, long generation, string invocation);
    Task<AdapterReply> CleanReleaseAsync(string relativePath, long generation, string invocation);
    Task<AdapterReply> ReadAsync(string relativePath, long generation);
}

/// <summary>The sidecar beside the ledger, under the same repository/session/generation/checkout partition, so a saved attempt only ever correlates within its own native generation.</summary>
internal sealed class EditClaimLifecycleStore(string directory)
{
    private const int ByteLimit = 1024 * 1024;
    private const int AttemptLimit = 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    internal sealed record Document(int Schema, List<EditClaimAttempt> Attempts, List<EditClaimTracking> Claims);

    private string DataPath => Path.Combine(directory, "edit-claim-lifecycle.json");
    private string GatePath => Path.Combine(directory, "edit-claim-lifecycle.lock");

    /// <summary>The ledger creates and secures this directory. Creating it here would look like a lost ledger to the ledger's own check.</summary>
    internal bool Exists => Directory.Exists(directory);

    internal Task<FileStream> SweepGateAsync(CancellationToken ct) => NativeJoinBinding.LockAsync(Path.Combine(directory, "edit-claim-sweep.lock"), ct);

    internal async Task<Document?> ReadAsync(CancellationToken ct)
    {
        if (!Exists) return null;
        try
        {
            await using var gate = await NativeJoinBinding.LockAsync(GatePath, ct);
            return Load();
        }
        catch (Exception error) when (Unavailable(error)) { return null; }
    }

    internal async Task<bool> UpdateAsync(Action<Document> change, CancellationToken ct)
    {
        if (!Exists) return false;
        try
        {
            await using var gate = await NativeJoinBinding.LockAsync(GatePath, ct);
            var document = Load();
            change(document);
            // Settled attempts age out first. An attempt still open at the bound refuses, and the PRE that asked then refuses too.
            while (document.Attempts.Count > AttemptLimit && document.Attempts.FindIndex(attempt => attempt.Settled) is >= 0 and var oldest) document.Attempts.RemoveAt(oldest);
            if (document.Attempts.Count > AttemptLimit || document.Claims.Count > AttemptLimit) return false;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Json);
            if (bytes.Length > ByteLimit) return false;
            NodeState.WritePrivately(DataPath, bytes);
            return true;
        }
        catch (Exception error) when (Unavailable(error)) { return false; }
    }

    private Document Load()
    {
        if (!File.Exists(DataPath)) return new(1, [], []);
        if (!NodeState.IsPrivateFile(DataPath) || new FileInfo(DataPath).Length > ByteLimit) throw new IOException("The file claim lifecycle record is not private or bounded.");
        using var parsed = JsonDocument.Parse(File.ReadAllBytes(DataPath), new JsonDocumentOptions { MaxDepth = 8 });
        if (!NativeJoinHook.Unique(parsed.RootElement)) throw new InvalidDataException();
        var document = parsed.RootElement.Deserialize<Document>(Json) ?? throw new InvalidDataException();
        if (document.Schema != 1 || document.Attempts is null || document.Claims is null) throw new InvalidDataException();
        return document;
    }

    private static bool Unavailable(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException;
}

/// <summary>
/// The Part 6 lifecycle around <see cref="EditClaimLedger"/>. Git's fresh per-file state is the only commit-or-discard signal; a shell command naming git only says when to look.
/// A claim renews only with an own change on record, the file still dirty and still holding that change's content. It releases only when Git says clean, nothing still needs it, and the guard itself made the grant.
/// Everything uncertain stays recorded and does nothing, so the claim lapses at the hub. A pre-owned grant is never renewed or released here: renewing could shorten a lease the chat took deliberately.
/// </summary>
internal sealed class EditClaimLifecycle(EditClaimLedger ledger, EditClaimLifecycleStore store, IEditClaimPhysicalState physical, IEditClaimLifecycleTransport transport, TimeProvider? clock = null)
{
    internal const int LeaseSeconds = 120;
    private const string ReleaseUnknown = "release-unknown", ReleaseBlocked = "release-blocked", Settled = "settled";
    private static readonly TimeSpan RenewWithin = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan OpenAttempt = TimeSpan.FromSeconds(LeaseSeconds);
    private static readonly TimeSpan SweepWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HubMargin = TimeSpan.FromSeconds(30);
    private static readonly string[] UnsettledLease = ["acquire-pending", "unknown", "grant-unconfirmed", "ownership-unknown", "release-pending", ReleaseUnknown];
    private readonly TimeProvider time = clock ?? TimeProvider.System;

    private sealed record Held(EditClaimIntent Intent, EditClaimLease Lease);

    /// <summary>After the guard abstained, before the edit: saves the attempt and its before-fingerprint. False leaves no way to find this edit later, so the caller refuses it.</summary>
    public async Task<bool> PrepareAsync(string intentId, IReadOnlyList<string> paths, CancellationToken ct)
    {
        if (await ReadReceiptAsync(intentId, ct) is not { Outcome: "held" } receipt) return false;
        var now = time.GetUtcNow();
        var before = paths.ToDictionary(path => path, path => physical.Fingerprint(path), StringComparer.Ordinal);
        return await store.UpdateAsync(document =>
        {
            foreach (var path in paths)
            {
                // A replayed PRE keeps the first before-fingerprint, so it can never rewrite what the change is measured against.
                if (document.Attempts.Any(attempt => attempt.IntentId == intentId && attempt.RelativePath == path)) continue;
                var lease = receipt.Targets.FirstOrDefault(target => target.ResourceId == path);
                if (lease?.Generation is not > 0) throw new InvalidDataException();
                document.Attempts.Add(new(intentId, path, before[path], null, null, now, false));
                // The guard's fresh read has just confirmed this exact generation, so an unanswered release of it did not take effect.
                Change(document, path, lease.Generation!.Value, now, row => row.ReleaseOutcome == ReleaseUnknown ? row with { ReleaseOutcome = null } : row);
            }
        }, ct);
    }

    /// <summary>The POST for one tool call. Correlates only by the saved attempt and its held receipt; nothing in the payload can assert ownership, and a reported path can only refuse. A replay finds its attempt settled and records nothing.</summary>
    public async Task ObserveAsync(string intentId, bool succeeded, string? reportedPath, CancellationToken ct)
    {
        if (await store.ReadAsync(ct) is not { } document) return;
        var open = document.Attempts.Where(attempt => attempt.IntentId == intentId && !attempt.Settled).ToArray();
        if (open.Length == 0 || await ReadReceiptAsync(intentId, ct) is not { Outcome: "held" } receipt) return;
        if (reportedPath is not null && (physical.RelativeKey(reportedPath) is not { } key || receipt.Targets.All(target => target.ResourceId != key))) succeeded = false;
        var now = time.GetUtcNow();
        var results = new List<(EditClaimAttempt Attempt, long? Generation)>();
        foreach (var attempt in open)
        {
            var lease = receipt.Targets.FirstOrDefault(target => target.ResourceId == attempt.RelativePath);
            string? after = null;
            DateTimeOffset? observed = null;
            if (succeeded && lease is { Generation: > 0, Outcome: "held" })
            {
                after = physical.Fingerprint(attempt.RelativePath);
                if (attempt.BeforeFingerprint is not null && after is not null && attempt.BeforeFingerprint != after && physical.Status(attempt.RelativePath) == EditClaimDirt.Dirty) observed = now;
            }
            results.Add((attempt with { AfterFingerprint = after, ObservedActivity = observed, Settled = true }, lease?.Generation));
        }
        await store.UpdateAsync(current =>
        {
            foreach (var (attempt, generation) in results)
            {
                var index = current.Attempts.FindIndex(row => row.IntentId == attempt.IntentId && row.RelativePath == attempt.RelativePath);
                if (index < 0 || current.Attempts[index].Settled) continue;
                current.Attempts[index] = attempt;
                if (attempt.ObservedActivity is not null && generation is { } grant)
                    Change(current, attempt.RelativePath, grant, now, row => row with { OwnFingerprint = attempt.AfterFingerprint });
            }
        }, ct);
    }

    /// <summary>Settles what can be settled for every claim this chat's guard holds. Returns when the next renewal falls due, or null with no held claim left.</summary>
    public async Task<DateTimeOffset?> SweepAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (!store.Exists) return null;
        FileStream gate;
        using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            waiting.CancelAfter(SweepWait);
            try { gate = await store.SweepGateAsync(waiting.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return now; }
        }
        await using (gate)
        {
            // Null is uncertainty about every obligation, never an empty set of them.
            if (ledger.Snapshot() is not { } snapshot || await store.ReadAsync(ct) is not { } document) return now;
            foreach (var settled in snapshot.Where(intent => intent.Outcome == "released")) ledger.RetireReleased(settled.Id);
            foreach (var resource in HeldLeases(snapshot).GroupBy(held => held.Lease.ResourceId, StringComparer.Ordinal))
            {
                var current = resource.Max(held => held.Lease.Generation!.Value);
                // Generations only rise, and this ledger holds a confirmed newer grant, so an older one is over without asking anyone.
                foreach (var older in resource.Where(held => held.Lease.Generation != current).GroupBy(held => held.Lease.Generation!.Value))
                    Settle(older.Select(held => held.Intent.Id), resource.Key, older.Key);
                await SweepClaimAsync(resource.Key, current, resource.Where(held => held.Lease.Generation == current).ToArray(), snapshot, document, ct);
            }
            return await FinishAsync(ct);
        }
    }

    private async Task SweepClaimAsync(string path, long generation, Held[] receipts, IReadOnlyList<EditClaimIntent> snapshot, EditClaimLifecycleStore.Document document, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var tracked = document.Claims.FirstOrDefault(row => row.RelativePath == path && row.Generation == generation);
        if (tracked is null)
        {
            tracked = new(path, generation, now.AddSeconds(LeaseSeconds));
            if (!await ChangeAsync(path, generation, row => row, ct)) return;
        }
        if (tracked.ReleaseOutcome == Settled) { Settle(receipts.Select(held => held.Intent.Id), path, generation); return; }
        // No saved attempt means a PRE between its ledger write and its sidecar write, which is still running.
        bool StillOpen(string intentId)
        {
            var own = document.Attempts.Where(row => row.IntentId == intentId).ToArray();
            return own.Length == 0 || own.Any(row => !row.Settled && now - row.PreparedAt < OpenAttempt);
        }
        // Duplicates are one claim: this is the receipt whose grant it is, and its request ID never changes.
        var grant = receipts.Where(held => held.Lease.NewlyAcquired).OrderBy(held => held.Lease.RequestId, StringComparer.Ordinal).FirstOrDefault();
        var dirt = physical.Status(path);
        if (grant is not null && dirt == EditClaimDirt.Clean && !receipts.Any(held => StillOpen(held.Intent.Id)) && !UnsettledSibling(snapshot, path, generation))
        {
            if (await ReleaseAsync(tracked, grant, receipts, ct)) return;
            tracked = (await store.ReadAsync(ct))?.Claims.FirstOrDefault(row => row.RelativePath == path && row.Generation == generation) ?? tracked;
        }
        else if (grant is not null && dirt == EditClaimDirt.Dirty)
        {
            var renewed = await RenewAsync(tracked, grant, ct);
            if (renewed is null) { Settle(receipts.Select(held => held.Intent.Id), path, generation); return; }
            tracked = renewed;
        }
        // A pre-owned duplicate never made a grant, so like the guard's own compensation it owes no release. Its evidence already lives on the claim row, so it can leave the ledger.
        foreach (var duplicate in receipts.Where(held => held.Intent.Targets.All(target => !target.NewlyAcquired) && !StillOpen(held.Intent.Id)))
            Settle([duplicate.Intent.Id], path, generation);
        // A claim that was not renewed lapses at the hub, and nothing else would ever tell this ledger. Ask with a read once the lease should be over.
        if (tracked.LeaseUntil + HubMargin < time.GetUtcNow()) await ReconcileAsync(tracked, receipts, ct);
    }

    /// <summary>True when the claim is settled. A definite refusal or answer is recorded; everything else stays release-unknown and replays the same invocation only if Git still says clean.</summary>
    private async Task<bool> ReleaseAsync(EditClaimTracking tracked, Held grant, Held[] receipts, CancellationToken ct)
    {
        var (path, generation) = (tracked.RelativePath, tracked.Generation);
        if (!await ChangeAsync(path, generation, row => row with { ReleaseOutcome = ReleaseUnknown }, ct)) return false;
        AdapterReply reply;
        try { reply = await transport.CleanReleaseAsync(path, generation, grant.Lease.RequestId + "-clean-release"); }
        catch (Exception error) when (Uncertain(error)) { return false; }
        // StaleClaim is the hub's own definite answer that this session holds nothing at this generation, so the receipts owe nothing further.
        if (reply is { Ok: false, Code: RelayErrorCodes.StaleClaim } || reply.Ok && ReplyGeneration(reply) == generation && reply.Data is { } data && data.TryGetProperty("released", out var released) && released.ValueKind == JsonValueKind.True)
        {
            await ChangeAsync(path, generation, row => row with { ReleaseOutcome = Settled }, ct);
            Settle(receipts.Select(held => held.Intent.Id), path, generation);
            return true;
        }
        if (reply is { Ok: false, Code: RelayErrorCodes.UnresolvedAction }) await ChangeAsync(path, generation, row => row with { ReleaseOutcome = ReleaseBlocked }, ct);
        return false;
    }

    /// <summary>The row after any renewal, or null when the hub says this generation is over. Refuses without an own change on record or once the file's content is no longer that change's.</summary>
    private async Task<EditClaimTracking?> RenewAsync(EditClaimTracking tracked, Held grant, CancellationToken ct)
    {
        var (path, generation) = (tracked.RelativePath, tracked.Generation);
        if (tracked.ReleaseOutcome == ReleaseUnknown || tracked.OwnFingerprint is null) return tracked;
        if (!tracked.RenewPending && tracked.LeaseUntil - time.GetUtcNow() > RenewWithin) return tracked;
        if (physical.Fingerprint(path) != tracked.OwnFingerprint) return tracked;
        // Reserved before sending, so a lost reply replays the same invocation rather than spending a new one.
        var invocation = grant.Lease.RequestId + "-renew-" + tracked.RenewSequence.ToString(CultureInfo.InvariantCulture);
        if (!await ChangeAsync(path, generation, row => row with { RenewPending = true }, ct)) return tracked;
        AdapterReply reply;
        try { reply = await transport.RenewAsync(path, generation, invocation); }
        catch (Exception error) when (Uncertain(error)) { return tracked; }
        if (reply is { Ok: false, Code: RelayErrorCodes.StaleClaim })
        {
            await ChangeAsync(path, generation, row => row with { ReleaseOutcome = Settled }, ct);
            return null;
        }
        if (!reply.Ok || ReplyGeneration(reply) != generation || ReplyLease(reply) is not { } until) return tracked;
        var renewed = tracked with { LeaseUntil = until, RenewPending = false, RenewSequence = tracked.RenewSequence + 1 };
        await ChangeAsync(path, generation, row => row with { LeaseUntil = until, RenewPending = false, RenewSequence = row.RenewSequence + 1 }, ct);
        return renewed;
    }

    private async Task ReconcileAsync(EditClaimTracking tracked, Held[] receipts, CancellationToken ct)
    {
        var (path, generation) = (tracked.RelativePath, tracked.Generation);
        AdapterReply reply;
        try { reply = await transport.ReadAsync(path, generation); }
        catch (Exception error) when (Uncertain(error)) { return; }
        if (!reply.Ok || reply.Data is not { ValueKind: JsonValueKind.Object } data || !data.TryGetProperty("held", out var held)) return;
        if (held.ValueKind == JsonValueKind.True && ReplyGeneration(reply) == generation && ReplyLease(reply) is { } until)
        {
            // A fresh read of this holder and generation: any release still unanswered did not take effect.
            await ChangeAsync(path, generation, row => row with { LeaseUntil = until, ReleaseOutcome = row.ReleaseOutcome == ReleaseUnknown ? null : row.ReleaseOutcome }, ct);
            return;
        }
        var lease = ReplyLease(reply);
        // Held false alone also means under fifteen seconds left or a blocker, so only a moved generation or a clearly past lease is definite.
        if (held.ValueKind != JsonValueKind.False || ReplyGeneration(reply) == generation && lease is { } past && past + HubMargin >= time.GetUtcNow()) return;
        await ChangeAsync(path, generation, row => row with { ReleaseOutcome = Settled }, ct);
        Settle(receipts.Select(receipt => receipt.Intent.Id), path, generation);
    }

    private async Task<DateTimeOffset?> FinishAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (ledger.Snapshot() is not { } remaining) return now;
        var live = HeldLeases(remaining).Select(held => (held.Lease.ResourceId, held.Lease.Generation!.Value)).ToHashSet();
        var known = remaining.Select(intent => intent.Id).ToHashSet(StringComparer.Ordinal);
        DateTimeOffset? due = null;
        await store.UpdateAsync(document =>
        {
            // By age as well, because an intent newer than this snapshot may have just saved its attempt.
            document.Attempts.RemoveAll(attempt => !known.Contains(attempt.IntentId) && now - attempt.PreparedAt >= OpenAttempt);
            document.Claims.RemoveAll(row => !live.Contains((row.RelativePath, row.Generation)) && row.LeaseUntil < now);
            foreach (var row in document.Claims.Where(row => live.Contains((row.RelativePath, row.Generation))))
                if (row.LeaseUntil - RenewWithin is var next && (due is null || next < due)) due = next;
        }, ct);
        return live.Count == 0 ? null : due ?? now;
    }

    private static IEnumerable<Held> HeldLeases(IEnumerable<EditClaimIntent> intents) =>
        from intent in intents where intent.Outcome == "held"
        from lease in intent.Targets where lease.Outcome == "held" && lease.Generation > 0
        select new Held(intent, lease);

    /// <summary>An acquire or release on this path whose result is not known. It may yet need this generation, so the claim stays and lapses instead.</summary>
    private static bool UnsettledSibling(IEnumerable<EditClaimIntent> intents, string path, long generation) => intents.Any(intent => intent.Targets.Any(target =>
        target.ResourceId == path && (target.Generation is null || target.Generation == generation) && UnsettledLease.Contains(target.Outcome)));

    private void Settle(IEnumerable<string> intentIds, string path, long generation)
    {
        foreach (var id in intentIds.Distinct(StringComparer.Ordinal))
        {
            if (ledger.Read(id) is not { } intent) continue;
            var targets = intent.Targets.Select(target => target.ResourceId == path && target.Generation == generation ? target with { Outcome = "released" } : target).ToArray();
            var whole = targets.All(target => target.Outcome == "released");
            if (ledger.Save(intent with { Targets = targets, Outcome = whole ? "released" : intent.Outcome }) && whole) ledger.RetireReleased(id);
        }
    }

    private async Task<EditClaimIntent?> ReadReceiptAsync(string intentId, CancellationToken ct)
    {
        // The ledger's own gate answers a sharing violation as null, and a parallel PRE holds it only briefly.
        for (var attempt = 0; ; attempt++)
        {
            if (ledger.Read(intentId) is { } receipt) return receipt;
            if (attempt >= 2) return null;
            await Task.Delay(50, ct);
        }
    }

    private Task<bool> ChangeAsync(string path, long generation, Func<EditClaimTracking, EditClaimTracking> change, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return store.UpdateAsync(document => Change(document, path, generation, now, change), ct);
    }

    private static void Change(EditClaimLifecycleStore.Document document, string path, long generation, DateTimeOffset now, Func<EditClaimTracking, EditClaimTracking> change)
    {
        var index = document.Claims.FindIndex(row => row.RelativePath == path && row.Generation == generation);
        if (index >= 0) document.Claims[index] = change(document.Claims[index]);
        else document.Claims.Add(change(new(path, generation, now.AddSeconds(LeaseSeconds))));
    }

    private static long? ReplyGeneration(AdapterReply reply) => reply.Data is { ValueKind: JsonValueKind.Object } data && data.TryGetProperty("generation", out var value) && value.TryGetInt64(out var generation) ? generation : null;
    private static DateTimeOffset? ReplyLease(AdapterReply reply) => reply.Data is { ValueKind: JsonValueKind.Object } data && ChatLocators.Text(data, "leaseUntil") is { } text &&
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var until) ? until : null;
    private static bool Uncertain(Exception error) => error is IOException or HttpRequestException or TimeoutException;
}

/// <summary>Physical truth for one checkout, proven again at every use because <see cref="ProtectedCheckoutRoot.Prove(string)"/> is point-in-time.</summary>
internal sealed class EditClaimCheckout(string root, string cwd) : IEditClaimPhysicalState
{
    private const long FingerprintLimit = 64L * 1024 * 1024;
    private const string Absent = "absent";

    public string? Fingerprint(string relativePath)
    {
        if (Verified(relativePath) is not { } path) return null;
        try
        {
            if (!File.Exists(path)) return Directory.Exists(path) ? null : Absent;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > FingerprintLimit || !EditClaimResolver.SingleRegularLink(stream.SafeFileHandle)) return null;
            return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Clean means Git succeeded, reports nothing against HEAD or the index, and the file is not hidden from that report. An ignored file has no commit to wait for, and an assume-unchanged or
    /// skip-worktree file reads clean whatever it holds, so both are unknown. So is any answer that does not name exactly this file. A staged change, an untracked file and a conflict are all dirty.
    /// </summary>
    public EditClaimDirt Status(string relativePath)
    {
        if (Verified(relativePath) is not { } path) return EditClaimDirt.Unknown;
        // icase because the key is folded and core.ignorecase may be off, which would otherwise match nothing and read as clean.
        var spec = ":(top,literal,icase)" + Path.GetRelativePath(root, path).Replace('\\', '/');
        var status = CoordinationTools.NativeGitOutput(root, "-c", "core.fsmonitor=false", "status", "--porcelain=v1", "-z", "--no-renames", "--untracked-files=all", "--ignored=matching", "--ignore-submodules=none", "--", spec);
        if (status is null) return EditClaimDirt.Unknown;
        var entries = status.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (entries.Any(entry => entry.Length < 4 || entry[2] != ' ' || entry.StartsWith("!!", StringComparison.Ordinal) || !Same(entry[3..], relativePath))) return EditClaimDirt.Unknown;
        var result = EditClaimDirt.Dirty;
        if (entries.Length == 0)
        {
            var listed = CoordinationTools.NativeGitOutput(root, "-c", "core.fsmonitor=false", "ls-files", "-v", "-z", "--full-name", "--", spec);
            if (listed is null) return EditClaimDirt.Unknown;
            var rows = listed.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (rows.Any(row => row.Length < 3 || row[0] != 'H' || row[1] != ' ' || !Same(row[2..], relativePath))) return EditClaimDirt.Unknown;
            // Neither tracked nor reported, yet still on disk: Git did not see this file, whatever the reason.
            if (rows.Length == 0 && File.Exists(path)) return EditClaimDirt.Unknown;
            result = EditClaimDirt.Clean;
        }
        return Verified(relativePath) == path ? result : EditClaimDirt.Unknown;
    }

    public string? RelativeKey(string reportedPath)
    {
        var resolved = EditClaimResolver.Resolve(root, cwd, [reportedPath]);
        return resolved is { Supported: true, Targets.Count: 1 } ? resolved.Targets[0].RelativeKey : null;
    }

    private string? Verified(string relativePath)
    {
        try
        {
            if (ProtectedCheckoutRoot.Prove(cwd) != root) return null;
            var resolved = EditClaimResolver.Resolve(root, root, [relativePath]);
            if (!resolved.Supported || resolved.Targets.Count != 1 || resolved.Targets[0].RelativeKey != relativePath) return null;
            var path = resolved.Targets[0].IoPath;
            // A deleted file can't be opened, so its nearest surviving ancestor answers for containment.
            for (var nearest = path; nearest is not null && nearest.Length >= root.Length; nearest = Path.GetDirectoryName(nearest))
                if (File.Exists(nearest) || Directory.Exists(nearest)) return ProtectedCheckoutRoot.Contains(root, nearest) ? path : null;
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private static bool Same(string reported, string relativePath) => string.Equals(reported, relativePath, StringComparison.OrdinalIgnoreCase);
}
