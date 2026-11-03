using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;
using CoCern.Contracts;
using CoCern.Contracts.Dashboard;

namespace CoCern.Core;

/// <summary>What a document grain needs to load its file: where it is, and the commit and hash the directory recorded.</summary>
[GenerateSerializer, Immutable]
public sealed record DocRef(
    [property: Id(0)] string RepositoryId, [property: Id(1)] string Path, [property: Id(2)] string Spec,
    [property: Id(3)] string Commit, [property: Id(4)] string Hash, [property: Id(5)] string Branch);

/// <summary>One document as the directory lists it; <see cref="Bytes"/> is its UTF-8 size, summed against the repository's byte cap.</summary>
[GenerateSerializer, Immutable]
public sealed record DocEntry(
    [property: Id(0)] string Path, [property: Id(1)] string Spec, [property: Id(2)] string Commit,
    [property: Id(3)] string Hash, [property: Id(4)] int Sections, [property: Id(5)] int Tokens, [property: Id(6)] int Bytes = 0);

/// <summary>
/// One search hit: the best chunk of one section. <see cref="Tokens"/> is what the whole section costs. <see cref="Coverage"/> is the
/// IDF-weighted share of the query's content terms (stop words left out) the chunk contains, from 0 to 1; <see cref="Score"/> is the
/// BM25 score scaled by it.
/// </summary>
[GenerateSerializer, Immutable]
public sealed record DocHit(
    [property: Id(0)] string Path, [property: Id(1)] string Anchor, [property: Id(2)] string Heading, [property: Id(3)] string Snippet,
    [property: Id(4)] float Score, [property: Id(5)] int Tokens, [property: Id(6)] string Commit, [property: Id(7)] bool HeadingMatch,
    [property: Id(8)] float Coverage)
{
    /// <summary>
    /// Best first by <see cref="Score"/>, then a heading match. Every document of a repository shares one index (<see cref="DocSearchIndex"/>),
    /// so BM25 scores compare across documents; the coverage scaling keeps a hit that misses a rare query term (such as the path) below one
    /// that has them all.
    /// </summary>
    public static IOrderedEnumerable<DocHit> Ranked(IEnumerable<DocHit> hits) =>
        hits.OrderByDescending(hit => hit.Score).ThenByDescending(hit => hit.HeadingMatch)
            .ThenBy(hit => hit.Path, StringComparer.Ordinal).ThenBy(hit => hit.Anchor, StringComparer.Ordinal);
}

/// <summary>
/// A section from one chunk (and a character offset in it) on: the chunks in order, so the text put together is exactly the section
/// text from that point. <see cref="Chunks"/>[0] already starts at <see cref="FirstOffset"/>.
/// </summary>
[GenerateSerializer, Immutable]
public sealed record DocSectionChunks(
    [property: Id(0)] string Path, [property: Id(1)] string Anchor, [property: Id(2)] string Heading, [property: Id(3)] string Commit,
    [property: Id(4)] int Tokens, [property: Id(5)] int FirstChunk, [property: Id(6)] string[] Chunks, [property: Id(7)] int[] ChunkTokens,
    [property: Id(8)] int FirstOffset = 0);

/// <summary>A search over the directory's documents. <see cref="NotSearched"/> documents failed to load or did not load before the search timeout.</summary>
[GenerateSerializer, Immutable]
public sealed record DocSearchOutcome(
    [property: Id(0)] DocHit[] Hits, [property: Id(1)] int Documents, [property: Id(2)] int NotSearched, [property: Id(3)] bool PathUnknown,
    [property: Id(4)] int PeakConcurrency);

/// <summary>A file left out only for room, with the bytes it needs (0 when skipped for the document count before it was read).</summary>
[GenerateSerializer, Immutable]
public sealed record DocWaiting([property: Id(0)] string Path, [property: Id(1)] long Bytes);

[GenerateSerializer, Immutable]
public sealed record DocDirectoryView(
    [property: Id(0)] string Branch, [property: Id(1)] string Commit, [property: Id(2)] bool NeedsResync, [property: Id(3)] DateTimeOffset? SyncedAt,
    [property: Id(4)] DocEntry[] Entries, [property: Id(5)] string[] Skipped, [property: Id(6)] string Fingerprint = "", [property: Id(7)] DocWaiting[]? CapacitySkipped = null);

/// <summary>
/// One sync's changes. <see cref="Full"/> replaces the skipped list and clears a pending full resync. <see cref="Skipped"/> are files
/// left out (too large, too many sections, or past the document limit); <see cref="Unskipped"/> are files no longer skipped.
/// <see cref="CapacitySkipped"/> are the ones among <see cref="Skipped"/> left out only for room (the document or byte limit), which a
/// later sync reconsiders once there is room; a path skipped for any other reason is no longer one.
/// </summary>
[GenerateSerializer, Immutable]
public sealed record DocDirectoryUpdate(
    [property: Id(0)] string Branch, [property: Id(1)] string Commit, [property: Id(2)] bool Full, [property: Id(3)] DocEntry[] Upserts,
    [property: Id(4)] string[] Removed, [property: Id(5)] string[] Skipped, [property: Id(6)] string[] Unskipped, [property: Id(7)] string Fingerprint = "",
    [property: Id(8)] DocWaiting[]? CapacitySkipped = null);

/// <summary>For tests and diagnostics: what a document grain holds now.</summary>
[GenerateSerializer, Immutable]
public sealed record DocGrainStats([property: Id(0)] bool Loaded, [property: Id(1)] string Commit, [property: Id(2)] string Hash, [property: Id(3)] int IndexRuns, [property: Id(4)] int Reads, [property: Id(5)] int Sections);

/// <summary>Utilization since tracking started for the current path; independent of the document's in-memory index lifetime.</summary>
[GenerateSerializer, Immutable]
public sealed record DocUsage(
    [property: Id(0)] long Searches, [property: Id(1)] long MatchedSections, [property: Id(2)] long SectionReads,
    [property: Id(3)] DateTimeOffset? LastUsedAt, [property: Id(4)] DateTimeOffset TrackingSince);

/// <summary>The directory's persisted state: the branch and commit it follows, and each document's entry.</summary>
[GenerateSerializer]
public sealed class DocDirectoryState
{
    [Id(0)] public string Branch { get; set; } = "";
    [Id(1)] public string Commit { get; set; } = "";
    [Id(2)] public bool NeedsResync { get; set; }
    [Id(3)] public DateTimeOffset? SyncedAt { get; set; }
    [Id(4)] public Dictionary<string, DocEntry> Docs { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Paths a sync left out, at most <see cref="RelayLimits.MaxDocsPerRepositoryCeiling"/>.</summary>
    [Id(5)] public SortedSet<string> Skipped { get; set; } = new(StringComparer.Ordinal);
    /// <summary>The sync settings (patterns, excludes, limits, branch, splitter version) the documents were indexed under; a change forces a full resync.</summary>
    [Id(6)] public string Fingerprint { get; set; } = "";
    /// <summary>The skipped paths left out only for room (<c>MaxDocuments</c> or <c>MaxRepositoryBytes</c>), reconsidered once there is room.</summary>
    /// Each with the bytes it needs (0 when not yet read), so a sync rereads them only when the smallest fits.
    [Id(7)] public Dictionary<string, long> CapacitySkipped { get; set; } = new(StringComparer.Ordinal);
    /// <summary>At most one record per current indexed path. Missing records in older state have no historical counters.</summary>
    [Id(8)] public Dictionary<string, DocUsage> Usage { get; set; } = new(StringComparer.Ordinal);
}

public static class DocKeys
{
    /// <summary>A document grain's key: <c>{repository}/{path}</c>, with the repository ID escaped.</summary>
    public static string Document(string repositoryId, string path) => Uri.EscapeDataString(repositoryId) + "/" + path;
    public static readonly GrainType DocumentType = GrainType.Create("cocern.doc");
    /// <summary>The repository's shared search index, <see cref="DocSearchGrain"/>, keyed by the repository ID itself.</summary>
    public static readonly GrainType SearchType = GrainType.Create("cocern.docsearch");
}

public interface IDocumentGrain : IGrainWithStringKey
{
    Task<DocSectionChunks?> SectionAsync(DocRef doc, string anchor);
    /// <summary>Pushes new content from a sync; an unchanged content hash is not re-indexed. Returns whether it re-indexed.</summary>
    Task<bool> ReplaceAsync(DocRef doc, string markdown);
    /// <summary>The file was deleted: drop the index now.</summary>
    Task DropAsync();
    Task<DocGrainStats> StatsAsync();
}

public interface IDocDirectoryGrain : IGrainWithStringKey
{
    [ReadOnly] Task<DocDirectoryView> ReadAsync();
    Task ApplyAsync(DocDirectoryUpdate update);
    Task MarkResyncAsync();
    [ReadOnly] Task<DocSearchOutcome> SearchAsync(string query, string? spec, string? path, int limit, TimeSpan timeout);
    [ReadOnly] Task<DocSectionChunks?> SectionAsync(string path, string anchor);
}

/// <summary>
/// One synced document's sections (markdown, text or OpenAPI), keyed by <c>{repository}/{path}</c>, for <c>doc_section</c> only: searches
/// go to the repository's <see cref="DocSearchGrain"/>, so only a document someone reads is loaded. Nothing is persisted: idle deactivation
/// (the grain type's collection age, <c>Relay:DocCache:IdleMinutes</c>) drops the sections, and the next read loads the file again with
/// <c>git show &lt;commit&gt;:&lt;path&gt;</c> at the commit the directory passes.
/// </summary>
[GrainType("cocern.doc")]
public sealed class DocumentGrain(IDocSource source, ITokenCounter tokens, DocCacheActivity? activity = null) : Grain, IDocumentGrain
{
    private readonly Guid observationId = Guid.NewGuid();
    private DateTimeOffset activatedAt;
    private void Observe()
    {
        if (activity is null) return;
        var key = this.GetPrimaryKeyString();
        var separator = key.IndexOf('/');
        activity.Update(observationId, new DashboardDocGrain
        {
            Kind = "Document", Repository = Uri.UnescapeDataString(key[..separator]), Path = key[(separator + 1)..],
            ActivatedAt = activatedAt, Loaded = loadedFor is not null, Commit = commit,
            Sections = sections.Count, Reads = reads, IndexRuns = indexRuns
        });
    }
    /// <summary>Separates a section anchor from a chunk number: <c>errors~2</c> is the second chunk of the section <c>errors</c>.</summary>
    public const char ChunkSeparator = '~';
    /// <summary>Separates a chunk number from a character offset in it: <c>errors~2+1500</c>.</summary>
    public const char OffsetSeparator = '+';

    private static bool Natural(string text, out int value) => int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value);
    private IReadOnlyList<DocSection> sections = [];
    private Dictionary<string, int> byAnchor = new(StringComparer.Ordinal);
    private string? loadedFor;
    private string? contentHash;
    private string commit = "";
    private int indexRuns, reads;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        activatedAt = DateTimeOffset.UtcNow;
        Observe();
        return Task.CompletedTask;
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        activity?.Remove(observationId);
        return Task.CompletedTask;
    }

    public async Task<DocSectionChunks?> SectionAsync(DocRef doc, string anchor)
    {
        await EnsureLoadedAsync(doc);
        // name, name~chunk, or name~chunk+offset (a character offset in that chunk, for a chunk too long for one reply).
        var separator = anchor.LastIndexOf(ChunkSeparator);
        var name = separator < 0 ? anchor : anchor[..separator];
        var first = 1;
        var offset = 0;
        if (separator >= 0)
        {
            var position = anchor[(separator + 1)..].Split(OffsetSeparator);
            if (position.Length > 2 || !Natural(position[0], out first) || first < 1 || position.Length == 2 && !Natural(position[1], out offset)) return null;
        }
        if (!byAnchor.TryGetValue(name, out var index) || first > sections[index].Chunks.Count) return null;
        var section = sections[index];
        var chunks = section.Chunks.Skip(first - 1).Select(chunk => (chunk.Text, chunk.Tokens)).ToArray();
        if (offset > 0)
        {
            if (offset >= chunks[0].Text.Length) return null;
            chunks[0] = (chunks[0].Text[offset..], tokens.Count(chunks[0].Text[offset..]));
        }
        return new(doc.Path, section.Anchor, section.HeadingPath, commit, section.Tokens, first, chunks.Select(chunk => chunk.Text).ToArray(), chunks.Select(chunk => chunk.Tokens).ToArray(), offset);
    }

    public Task<bool> ReplaceAsync(DocRef doc, string markdown)
    {
        var hash = DocHash.Of(markdown);
        // A push is the content itself, so a pending load for an older version is no longer needed.
        var changed = hash != contentHash;
        if (changed) Index(doc.Path, markdown, hash);
        commit = doc.Commit;
        loadedFor = doc.Hash;
        Observe();
        return Task.FromResult(changed);
    }

    public Task DropAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    public Task<DocGrainStats> StatsAsync() => Task.FromResult(new DocGrainStats(loadedFor is not null, commit, contentHash ?? "", indexRuns, reads, sections.Count));

    /// <summary>
    /// Loads the file at the directory's recorded commit when this activation has not loaded that version yet. When the commit is gone
    /// (a force push, then garbage collection) it reads the branch head; the search grain's read of the same file is what schedules the
    /// full resync. When the read fails for another reason (a timeout, a file over its limit), nothing is loaded and the next call tries again.
    /// </summary>
    private async Task EnsureLoadedAsync(DocRef doc)
    {
        if (loadedFor == doc.Hash) return;
        reads++;
        Observe();
        var content = await source.ReadAsync(doc.RepositoryId, doc.Path, doc.Commit);
        if (content is null)
        {
            // Only a commit that is really gone falls back to the branch head; a failed or refused read tries again on the next call.
            if (doc.Branch.Length == 0 || await source.CommitExistsAsync(doc.RepositoryId, doc.Commit)) return;
            content = await source.ReadHeadAsync(doc.RepositoryId, doc.Path, doc.Branch);
            if (content is null) return;
        }
        if (content.Hash != contentHash) Index(doc.Path, content.Text, content.Hash);
        commit = content.Commit;
        loadedFor = doc.Hash;
        Observe();
    }

    /// <summary>
    /// Splits the whole file again, replacing its sections; sections the new version lacks disappear. A file its splitter refuses (the sync
    /// skips those) has none.
    /// </summary>
    private void Index(string path, string content, string hash)
    {
        sections = DocFormats.Build(path, content, tokens, out _) ?? [];
        byAnchor = sections.Select((section, index) => (section.Anchor, index)).ToDictionary(item => item.Anchor, item => item.index, StringComparer.Ordinal);
        contentHash = hash;
        indexRuns++;
    }
}

/// <summary>
/// One repository's document directory (keyed by the repository ID), persisted through the hub's grain storage: the branch and commit
/// it follows, and per document its path, spec, indexed commit, content hash, section count and token total. Searches go to the
/// repository's one <see cref="DocSearchGrain"/> and sections to the one document grain asked for; both are counted as each document's
/// usage here. Reads are <see cref="ReadOnlyAttribute"/>, so searches run side by side; a sync's update waits for them.
/// </summary>
[GrainType("cocern.docdir")]
public sealed class DocDirectoryGrain([PersistentState("docs", "relay")] IPersistentState<DocDirectoryState> state, DocCacheActivity? activity = null) : Grain, IDocDirectoryGrain
{
    private readonly Guid observationId = Guid.NewGuid();
    // ReadOnly requests may overlap; serialize their metadata writes and copy state only once this is held.
    private readonly SemaphoreSlim usageWrites = new(1);
    private DateTimeOffset activatedAt;
    private void Observe()
    {
        if (activity is null) return;
        var current = state.State;
        var paths = current.Docs.Keys.Where(path => path.Length <= DocCacheActivity.MaxDocumentPathLength)
            .Order(StringComparer.Ordinal).Take(DocCacheActivity.MaxDocumentPaths).ToArray();
        var usage = current.Usage ?? [];
        activity.Update(observationId, new DashboardDocGrain
        {
            Kind = "Directory", Repository = Repository, ActivatedAt = activatedAt,
            Loaded = true, Commit = current.Commit, Documents = current.Docs.Count, DocumentPaths = paths,
            DocumentUsage = paths.Where(path => usage.GetValueOrDefault(path) is not null).ToDictionary(path => path, path => new DashboardDocUsage
            {
                Searches = usage[path].Searches, MatchedSections = usage[path].MatchedSections, SectionReads = usage[path].SectionReads,
                LastUsedAt = usage[path].LastUsedAt, TrackingSince = usage[path].TrackingSince
            }, StringComparer.Ordinal)
        });
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        activatedAt = DateTimeOffset.UtcNow;
        // Older persisted directories have no utilization history. Establish a new, durable tracking period, without inventing past use.
        if (state.State.Docs.Keys.Any(path => state.State.Usage?.GetValueOrDefault(path) is null))
        {
            var candidate = Copy(state.State);
            ReconcileUsage(candidate, activatedAt);
            try { await CommitAsync(candidate); }
            catch (Exception) { /* Tracking is best effort: inventory and document access remain available. */ }
        }
        Observe();
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        activity?.Remove(observationId);
        return Task.CompletedTask;
    }

    private string Repository => this.GetPrimaryKeyString();

    public Task<DocDirectoryView> ReadAsync() => Task.FromResult(new DocDirectoryView(state.State.Branch, state.State.Commit, state.State.NeedsResync, state.State.SyncedAt,
        state.State.Docs.Values.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray(), state.State.Skipped.ToArray(), state.State.Fingerprint,
        (state.State.CapacitySkipped ?? []).OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => new DocWaiting(entry.Key, entry.Value)).ToArray()));

    /// <summary>
    /// Builds the update on a copy and makes it current only once it is stored. A failed write leaves the old commit and documents in
    /// place (and throws to the sync), so the next sync still sees its head as new and applies it again.
    /// </summary>
    public Task ApplyAsync(DocDirectoryUpdate update)
    {
        var candidate = Copy(state.State);
        candidate.Branch = update.Branch;
        candidate.Commit = update.Commit;
        candidate.Fingerprint = update.Fingerprint;
        candidate.SyncedAt = DateTimeOffset.UtcNow;
        foreach (var path in update.Removed) candidate.Docs.Remove(path);
        foreach (var entry in update.Upserts) candidate.Docs[entry.Path] = entry;
        ReconcileUsage(candidate, candidate.SyncedAt.Value);
        if (update.Full) { candidate.NeedsResync = false; candidate.Skipped.Clear(); candidate.CapacitySkipped.Clear(); }
        foreach (var path in update.Unskipped) { candidate.Skipped.Remove(path); candidate.CapacitySkipped.Remove(path); }
        var forRoom = (update.CapacitySkipped ?? []).GroupBy(waiting => waiting.Path, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Last().Bytes, StringComparer.Ordinal);
        foreach (var path in update.Skipped)
        {
            if (candidate.Skipped.Count < RelayLimits.MaxDocsPerRepositoryCeiling) candidate.Skipped.Add(path);
            // Skipped for another reason this time (too large, malformed): no longer waiting for room.
            if (forRoom.TryGetValue(path, out var bytes) && candidate.Skipped.Contains(path)) candidate.CapacitySkipped[path] = bytes; else candidate.CapacitySkipped.Remove(path);
            // A file waiting for room that the full list cannot hold would be lost for good: a full resync lists every file again.
            if (forRoom.ContainsKey(path) && !candidate.Skipped.Contains(path)) candidate.NeedsResync = true;
        }
        return CommitAsync(candidate);
    }

    /// <summary>Marks a full resync as needed. Like <see cref="ApplyAsync"/>, the mark counts only once stored, so a failed write is retried by the next mark.</summary>
    public Task MarkResyncAsync()
    {
        if (state.State.NeedsResync) return Task.CompletedTask;
        var candidate = Copy(state.State);
        candidate.NeedsResync = true;
        return CommitAsync(candidate);
    }

    /// <summary>
    /// Writes <paramref name="candidate"/> as the state. On failure it reloads what storage holds (a write that did land, or the previous
    /// state, which also refreshes the ETag), keeps the previous state if even that fails, and rethrows.
    /// </summary>
    private async Task CommitAsync(DocDirectoryState candidate)
    {
        var previous = state.State;
        state.State = candidate;
        try { await state.WriteStateAsync(); }
        catch (Exception)
        {
            state.State = previous;
            try { await state.ReadStateAsync(); }
            catch (Exception) { state.State = previous; }
            throw;
        }
        finally { Observe(); }
    }

    private static DocDirectoryState Copy(DocDirectoryState source) => new()
    {
        Branch = source.Branch, Commit = source.Commit, NeedsResync = source.NeedsResync, SyncedAt = source.SyncedAt, Fingerprint = source.Fingerprint,
        Docs = new Dictionary<string, DocEntry>(source.Docs, StringComparer.Ordinal), Skipped = new SortedSet<string>(source.Skipped, StringComparer.Ordinal),
        CapacitySkipped = new Dictionary<string, long>(source.CapacitySkipped ?? [], StringComparer.Ordinal),
        Usage = new Dictionary<string, DocUsage>(source.Usage ?? [], StringComparer.Ordinal)
    };

    private static void ReconcileUsage(DocDirectoryState candidate, DateTimeOffset now)
    {
        foreach (var path in candidate.Usage.Keys.Where(path => !candidate.Docs.ContainsKey(path)).ToArray()) candidate.Usage.Remove(path);
        foreach (var path in candidate.Docs.Keys)
            if (candidate.Usage.GetValueOrDefault(path) is null) candidate.Usage[path] = new DocUsage(0, 0, 0, null, now);
    }

    /// <summary>
    /// One write for an operation's answered documents. A storage failure can lose utilization increments, but cannot turn a successful
    /// document response into a failure. CommitAsync restores persisted state and its ETag before the next operation tries again.
    /// </summary>
    private async Task RecordUsageAsync(IReadOnlyList<(string Path, int Matches)> searches, string? sectionPath = null)
    {
        if (searches.Count == 0 && sectionPath is null) return;
        await usageWrites.WaitAsync();
        try
        {
            var candidate = Copy(state.State);
            var now = DateTimeOffset.UtcNow;
            ReconcileUsage(candidate, now);
            foreach (var (path, matches) in searches)
                if (candidate.Usage.TryGetValue(path, out var usage))
                    candidate.Usage[path] = usage with { Searches = usage.Searches + 1, MatchedSections = usage.MatchedSections + matches, LastUsedAt = now };
            if (sectionPath is not null && candidate.Usage.TryGetValue(sectionPath, out var sectionUsage))
                candidate.Usage[sectionPath] = sectionUsage with { SectionReads = sectionUsage.SectionReads + 1, LastUsedAt = now };
            await CommitAsync(candidate);
        }
        catch (Exception) { /* Usage is best effort; never replace a successful document answer with a statistics failure. */ }
        finally { usageWrites.Release(); }
    }

    public async Task<DocSearchOutcome> SearchAsync(string query, string? spec, string? path, int limit, TimeSpan timeout)
    {
        var current = state.State;
        if (path is not null && !current.Docs.ContainsKey(path)) return new([], 0, 0, true, 0);
        DocIndexSearch result;
        try { result = await GrainFactory.GetGrain<IDocSearchGrain>(Repository).SearchAsync(query, spec, path, limit, timeout, DocSearchGrain.ViewVersion(current.Commit, current.SyncedAt)); }
        catch (Exception)
        {
            // The index failing to answer leaves every candidate not searched, as a failing document did.
            var candidates = current.Docs.Values.Count(entry => (path is null || entry.Path == path) && (spec is null || entry.Spec == spec));
            return new([], candidates, candidates, false, 0);
        }
        if (result.FellBack) this.AsReference<IDocDirectoryGrain>().MarkResyncAsync().Ignore();
        // Each document that answered counts one search and its matched sections; one not loaded in time counts nothing.
        await RecordUsageAsync(result.Searched.Select(item => (item.Path, item.Matches)).ToArray());
        return result.Outcome;
    }

    public async Task<DocSectionChunks?> SectionAsync(string path, string anchor)
    {
        if (!state.State.Docs.TryGetValue(path, out var entry)) return null;
        var section = await GrainFactory.GetGrain<IDocumentGrain>(DocKeys.Document(Repository, path)).SectionAsync(Ref(entry), anchor);
        if (section is not null) await RecordUsageAsync([], path);
        return section;
    }

    private DocRef Ref(DocEntry entry) => new(Repository, entry.Path, entry.Spec, entry.Commit, entry.Hash, state.State.Branch);
}
