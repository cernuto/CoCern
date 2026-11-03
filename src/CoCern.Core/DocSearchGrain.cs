using Orleans;
using Orleans.Concurrency;
using CoCern.Contracts;

namespace CoCern.Core;

/// <summary>How many of one searched file's sections matched, for the directory's usage counts.</summary>
[GenerateSerializer, Immutable]
public sealed record DocPathMatches([property: Id(0)] string Path, [property: Id(1)] int Matches);

/// <summary>
/// A search over a repository's shared index: the outcome the directory returns, every file that answered (<see cref="Searched"/>, also
/// those with no match), and <see cref="FellBack"/> when a file's recorded commit was gone and it was read at the branch head instead, so
/// the directory schedules a full resync.
/// </summary>
[GenerateSerializer, Immutable]
public sealed record DocIndexSearch([property: Id(0)] DocSearchOutcome Outcome, [property: Id(1)] DocPathMatches[] Searched, [property: Id(2)] bool FellBack);

/// <summary>For tests and diagnostics: whether the index is built, the files the directory lists, the files indexed, and the git reads so far.</summary>
[GenerateSerializer, Immutable]
public sealed record DocSearchStats([property: Id(0)] bool Built, [property: Id(1)] int Documents, [property: Id(2)] int Indexed, [property: Id(3)] int Reads);

public interface IDocSearchGrain : IGrainWithStringKey
{
    /// <summary>
    /// <paramref name="version"/> is the directory's <see cref="DocSearchGrain.ViewVersion"/>: when it is not the version the index follows,
    /// the grain reads the directory again first, so a patch that never arrived cannot leave it behind. Empty skips the check.
    /// </summary>
    [ReadOnly] Task<DocIndexSearch> SearchAsync(string query, string? spec, string? path, int limit, TimeSpan timeout, string version);
    /// <summary>
    /// A sync's changes: starts rereading the paths whose hash changed, without waiting for them. Before the first search builds the index
    /// it does nothing: the build reads the directory then.
    /// </summary>
    Task PatchAsync(DocRef[] upserts, string[] removed);
    [ReadOnly] Task<DocSearchStats> StatsAsync();
}

/// <summary>
/// One repository's shared search index (keyed by the repository ID), so a search loads no document grain. The first search reads the
/// directory's documents and loads each at its recorded commit, as a document grain does (falling back to the branch head only when the
/// commit is gone), at most <see cref="RelayLimits.MaxDocFanOut"/> reads at a time; a search past its timeout searches what has loaded and
/// counts the rest as not searched, while they keep loading. Patches, and a search that finds the directory changed, reread only paths
/// whose directory hash changed. Searches are <see cref="ReadOnlyAttribute"/> and run side by side; nothing is persisted, so a deactivated
/// index is built again on next use.
/// </summary>
[GrainType("cocern.docsearch")]
public sealed class DocSearchGrain(IDocSource source, ITokenCounter tokens) : Grain, IDocSearchGrain
{
    private enum Loaded { Indexed, Empty, Failed }

    /// <summary>One read of one file version; <see cref="Batch"/> is the search that started it, for its peak concurrency.</summary>
    private sealed class Load(DocRef doc, Batch batch)
    {
        public DocRef Doc { get; } = doc;
        public Batch Batch { get; } = batch;
        public Task<Loaded> Task { get; set; } = null!;
    }

    private sealed class Batch { public int Running, Peak; }

    private readonly SemaphoreSlim gate = new(RelayLimits.MaxDocFanOut);
    private readonly Dictionary<string, string> loadedFor = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Load> loads = new(StringComparer.Ordinal);
    private DocSearchIndex? index;
    /// <summary>What each path should hold, from the directory's view and the patches since; null until built.</summary>
    private Dictionary<string, DocRef>? wanted;
    /// <summary>The <see cref="ViewVersion"/> of the directory view last read, and that read while it runs (or last ran).</summary>
    private string followed = "";
    private Task? reading;
    private bool fellBack;
    private int reads;

    private string Repository => this.GetPrimaryKeyString();

    /// <summary>A directory's version as a search passes it: every applied sync sets a new <c>SyncedAt</c>.</summary>
    public static string ViewVersion(string commit, DateTimeOffset? syncedAt) =>
        commit + "@" + (syncedAt?.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "");

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        index?.Dispose();
        return Task.CompletedTask;
    }

    public async Task<DocIndexSearch> SearchAsync(string query, string? spec, string? path, int limit, TimeSpan timeout, string version)
    {
        await EnsureCurrentAsync(version);
        var docs = wanted!;
        if (path is not null && !docs.ContainsKey(path)) return new(new([], 0, 0, true, 0), [], TakeFellBack());
        var candidates = docs.Values.Where(doc => (path is null || doc.Path == path) && (spec is null || doc.Spec == spec)).OrderBy(doc => doc.Path, StringComparer.Ordinal).ToArray();
        var batch = new Batch();
        var pending = candidates.Select(doc => Ensure(doc, batch)).OfType<Load>().ToArray();
        try { await Task.WhenAll(pending.Select(load => load.Task)).WaitAsync(timeout); }
        catch (TimeoutException) { /* Past the search timeout: search what has loaded; the rest keep loading. */ }
        // A file answers once its read finished, even with nothing to index (a refused read keeps what it held, as a document grain does).
        var waiting = pending.Where(load => !load.Task.IsCompletedSuccessfully || load.Task.Result == Loaded.Failed).Select(load => load.Doc.Path).ToHashSet(StringComparer.Ordinal);
        var answered = candidates.Select(doc => doc.Path).Where(candidate => !waiting.Contains(candidate)).ToHashSet(StringComparer.Ordinal);
        // The candidates already applied spec and path from the directory's entries, which a same-content patch may have changed.
        var result = index!.Search(query, null, null, limit, answered.Contains);
        var searched = candidates.Where(doc => answered.Contains(doc.Path)).Select(doc => new DocPathMatches(doc.Path, result.Matches.GetValueOrDefault(doc.Path))).ToArray();
        var peak = pending.Select(load => load.Batch.Peak).DefaultIfEmpty(0).Max();
        return new(new(result.Hits, candidates.Length, candidates.Length - answered.Count, false, peak), searched, TakeFellBack());
    }

    public Task PatchAsync(DocRef[] upserts, string[] removed)
    {
        // Not built yet: the first search builds from the directory as it is then.
        if (wanted is null) return Task.CompletedTask;
        Forget(removed);
        foreach (var doc in upserts) wanted[doc.Path] = doc;
        // Only a changed hash is read again, in the background so searches go on; a failed read is retried by the next search.
        var batch = new Batch();
        foreach (var doc in upserts) Ensure(doc, batch);
        return Task.CompletedTask;
    }

    public Task<DocSearchStats> StatsAsync() => Task.FromResult(new DocSearchStats(wanted is not null, wanted?.Count ?? 0, index?.Count ?? 0, reads));

    private bool TakeFellBack()
    {
        var fell = fellBack;
        fellBack = false;
        return fell;
    }

    /// <summary>
    /// Reads the directory on first use, and again when <paramref name="version"/> says it changed since. Searches at once share one read;
    /// a failed read throws to the search and is tried again by the next one.
    /// </summary>
    private async Task EnsureCurrentAsync(string version)
    {
        // The second pass covers a read that started before the directory moved on.
        for (var pass = 0; pass < 2; pass++)
        {
            if (wanted is not null && (version.Length == 0 || version == followed)) return;
            if (reading is null || reading.IsCompleted) reading = ReadViewAsync();
            await reading;
        }
    }

    private async Task ReadViewAsync()
    {
        var view = await GrainFactory.GetGrain<IDocDirectoryGrain>(Repository).ReadAsync();
        var entries = view.Entries.ToDictionary(entry => entry.Path, entry => new DocRef(Repository, entry.Path, entry.Spec, entry.Commit, entry.Hash, view.Branch), StringComparer.Ordinal);
        if (wanted is null) { index = new DocSearchIndex(tokens); wanted = entries; }
        else
        {
            Forget(wanted.Keys.Where(path => !entries.ContainsKey(path)).ToArray());
            foreach (var (path, doc) in entries) wanted[path] = doc;
        }
        followed = ViewVersion(view.Commit, view.SyncedAt);
    }

    /// <summary>Drops removed paths from the index; a read still running for one finds it gone and indexes nothing.</summary>
    private void Forget(string[] removed)
    {
        foreach (var path in removed) { wanted!.Remove(path); loadedFor.Remove(path); loads.Remove(path); }
        index!.Apply([], removed);
    }

    /// <summary>The read that brings <paramref name="doc"/> up to date: null when it is, the read in flight for that version, or a new one.</summary>
    private Load? Ensure(DocRef doc, Batch batch)
    {
        if (loadedFor.GetValueOrDefault(doc.Path) == doc.Hash) return null;
        if (loads.TryGetValue(doc.Path, out var running) && running.Doc.Hash == doc.Hash) return running;
        var load = new Load(doc, batch);
        loads[doc.Path] = load;
        load.Task = LoadAsync(load);
        return load;
    }

    /// <summary>
    /// Reads the file at its recorded commit and indexes it, as <c>DocumentGrain.EnsureLoadedAsync</c> does: only a commit that is really
    /// gone falls back to the branch head; a refused or missing read indexes nothing and is tried again by the next search; a read that
    /// throws counts as not searched. A newer patch or a removal while it read has the last word.
    /// </summary>
    private async Task<Loaded> LoadAsync(Load load)
    {
        var doc = load.Doc;
        try
        {
            await gate.WaitAsync();
            try
            {
                load.Batch.Peak = Math.Max(load.Batch.Peak, ++load.Batch.Running);
                reads++;
                var content = await source.ReadAsync(doc.RepositoryId, doc.Path, doc.Commit);
                if (content is null)
                {
                    if (doc.Branch.Length == 0 || await source.CommitExistsAsync(doc.RepositoryId, doc.Commit)) return Loaded.Empty;
                    fellBack = true;
                    content = await source.ReadHeadAsync(doc.RepositoryId, doc.Path, doc.Branch);
                    if (content is null) return Loaded.Empty;
                }
                if (wanted?.GetValueOrDefault(doc.Path)?.Hash != doc.Hash || index is null) return Loaded.Empty;
                index.Upsert(new DocSearchDocument(doc.Path, doc.Spec, content.Commit, content.Hash, content.Text));
                loadedFor[doc.Path] = doc.Hash;
                return Loaded.Indexed;
            }
            finally { load.Batch.Running--; gate.Release(); }
        }
        catch (Exception) { return Loaded.Failed; }
        finally { if (loads.GetValueOrDefault(doc.Path) == load) loads.Remove(doc.Path); }
    }
}
