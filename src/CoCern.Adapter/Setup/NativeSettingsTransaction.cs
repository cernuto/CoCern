using System.Text.Json;
using System.Security.Cryptography;
using CoCern.Adapter.Node;

namespace CoCern.Adapter.Setup;

// Explicit, Windows-only future application path. Planning alone never writes settings.
internal static class NativeSettingsTransaction
{
    private sealed record Entry(string Path, byte[] Before, byte[] After);
    private sealed record Journal(string Kind, string Profile, Entry[] Entries);

    internal static void ApplyPromotion(string folder, string profile, string dotnet, string adapter,
        NativeActivation.PermissionEvidence? evidence, NativeActivation.PlanResult preview, TimeProvider clock) =>
        Apply(folder, profile, "promotion", preview, () => ClientSettings.PlanNativePromotion(folder, profile, dotnet, adapter,
            NativeActivation.Choice.ActivateOwnedNative, evidence, clock.GetUtcNow()),
            () => NativeActivation.RegistrationCurrent(folder, profile, "codex", evidence, clock.GetUtcNow(), true));

    internal static void ApplyFileClaims(string folder, string profile, string dotnet, string adapter, string provider,
        NativeActivation.Choice choice, NativeActivation.PlanResult preview) =>
        Apply(folder, profile, "file-claims-" + provider, preview,
            () => NativeActivation.PlanFileClaims(folder, profile, dotnet, adapter, choice, provider),
            () => NativeActivation.RegistrationCurrent(folder, profile, provider, null, DateTimeOffset.UtcNow, false));

    private static string JournalPath(string folder) => Path.Combine(folder, "native-settings-transaction.json");
    private static FileStream Gate(string folder) => new(Path.Combine(folder, "native-settings-transaction.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    private static string[] Targets(string profile, string kind) => kind switch
    {
        "promotion" => [Path.Combine(profile, "hooks.json"), Path.Combine(profile, "config.toml")],
        "file-claims-codex" => [Path.Combine(profile, "hooks.json")],
        "file-claims-claude" => [Path.Combine(profile, "settings.json")],
        _ => throw new ClientSettingsException("Unsupported native settings transaction.")
    };

    private static void Apply(string folder, string profile, string kind, NativeActivation.PlanResult preview, Func<NativeActivation.PlanResult> replan, Func<bool> verify)
    {
        if (!OperatingSystem.IsWindows()) throw new ClientSettingsException("Native settings transactions require Windows file sharing guarantees.");
        folder = Path.GetFullPath(folder); profile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile));
        using var gate = Gate(folder);
        if (File.Exists(JournalPath(folder))) throw new ClientSettingsException("A native settings transaction needs recovery first.");
        var targets = Targets(profile, kind);
        if (preview.Refusal is not null || preview.Plans.Count != targets.Length || preview.Snapshots.Count != targets.Length ||
            !preview.Plans.Select(p => p.Path).SequenceEqual(targets, StringComparer.OrdinalIgnoreCase))
            throw new ClientSettingsException("The native settings preview is invalid.");
        // Replan before taking write handles: the ordinary settings readers cannot open a file
        // while our exclusive writer is held. The locked snapshots below close that gap.
        var fresh = replan();
        if (fresh.Refusal is not null || fresh.Plans.Count != preview.Plans.Count ||
            fresh.Plans.Where((p, i) => !SamePlan(p, preview.Plans[i])).Any() || fresh.Dependencies.Count != preview.Dependencies.Count ||
            fresh.Dependencies.Where((d, i) => !string.Equals(d.Path, preview.Dependencies[i].Path, StringComparison.OrdinalIgnoreCase) ||
                !d.Digest.AsSpan().SequenceEqual(preview.Dependencies[i].Digest)).Any())
            throw new ClientSettingsException("Native settings or verification changed since preview; review a fresh plan.");
        var streams = new List<FileStream>();
        var dependencies = new List<FileStream>();
        try
        {
            foreach (var path in targets)
            {
                // Refuse absent files and links rather than creating a reservation that a crash could make ambiguous.
                if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new ClientSettingsException("Native settings must exist as ordinary files before applying this plan.");
                streams.Add(new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read));
            }
            foreach (var dependency in preview.Dependencies)
            {
                var targetIndex = Array.FindIndex(targets, path => string.Equals(path, dependency.Path, StringComparison.OrdinalIgnoreCase));
                FileStream input;
                if (targetIndex >= 0) input = streams[targetIndex];
                else
                {
                    if (!File.Exists(dependency.Path) || (File.GetAttributes(dependency.Path) & FileAttributes.ReparsePoint) != 0)
                        throw new ClientSettingsException("An ownership input is missing or is a link.");
                    input = new FileStream(dependency.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    dependencies.Add(input);
                }
                input.Position = 0;
                if (!SHA256.HashData(input).AsSpan().SequenceEqual(dependency.Digest)) throw new ClientSettingsException("A native ownership input changed since preview.");
            }
            if (!verify() || streams.Where((stream, i) => preview.Snapshots[i] is not { } snapshot || !Read(stream).AsSpan().SequenceEqual(snapshot)).Any())
                throw new ClientSettingsException("Native settings or verification changed since preview; review a fresh plan.");
            var entries = targets.Select((path, i) => new Entry(path, Read(streams[i]), fresh.Plans[i].IsUnchanged ? Read(streams[i]) : fresh.Plans[i].PlannedContent.ToArray())).ToArray();
            if (entries.Any(entry => entry.After.Length > 1024 * 1024)) throw new ClientSettingsException("Native settings are too large to recover.");
            var journal = JsonSerializer.SerializeToUtf8Bytes(new Journal(kind, profile, entries));
            if (journal.Length > 4 * 1024 * 1024) throw new ClientSettingsException("Native settings journal would be too large to recover.");
            NodeState.WritePrivately(JournalPath(folder), journal);
            for (var i = 0; i < entries.Length; i++) if (!entries[i].Before.AsSpan().SequenceEqual(entries[i].After)) Write(streams[i], entries[i].After);
            File.Delete(JournalPath(folder));
        }
        finally { foreach (var stream in dependencies) stream.Dispose(); foreach (var stream in streams) stream.Dispose(); }
    }

    // Recovery restores only known before/after states. An unrecognized partial or external edit
    // remains untouched with the private journal retained for manual recovery.
    internal static void Recover(string folder, string profile, string kind)
    {
        if (!OperatingSystem.IsWindows()) throw new ClientSettingsException("Native settings transactions require Windows file sharing guarantees.");
        folder = Path.GetFullPath(folder); profile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile));
        using var gate = Gate(folder);
        var journalPath = JournalPath(folder);
        if (!NodeState.IsPrivateFile(journalPath) || new FileInfo(journalPath).Length > 4 * 1024 * 1024)
            throw new ClientSettingsException("The private native settings journal is unavailable or invalid.");
        var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllBytes(journalPath)) ?? throw new ClientSettingsException("The native settings journal is invalid.");
        var targets = Targets(profile, kind);
        if (journal.Kind != kind || !string.Equals(journal.Profile, profile, StringComparison.OrdinalIgnoreCase) || journal.Entries.Length != targets.Length ||
            !journal.Entries.Select(e => e.Path).SequenceEqual(targets, StringComparer.OrdinalIgnoreCase))
            throw new ClientSettingsException("The native settings journal does not match this profile.");
        var streams = new List<FileStream>();
        try
        {
            foreach (var path in targets)
            {
                if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new ClientSettingsException("Native settings recovery needs the original files.");
                streams.Add(new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read));
            }
            for (var i = 0; i < streams.Count; i++)
            {
                var current = Read(streams[i]);
                if (!current.AsSpan().SequenceEqual(journal.Entries[i].Before) && !current.AsSpan().SequenceEqual(journal.Entries[i].After))
                    throw new ClientSettingsException("Native settings changed or were partially written; retain the private journal and recover manually.");
            }
            for (var i = streams.Count - 1; i >= 0; i--) if (!Read(streams[i]).AsSpan().SequenceEqual(journal.Entries[i].Before)) Write(streams[i], journal.Entries[i].Before);
            File.Delete(journalPath);
        }
        finally { foreach (var stream in streams) stream.Dispose(); }
    }

    private static bool SamePlan(ClientPlan left, ClientPlan right) =>
        string.Equals(left.Path, right.Path, StringComparison.OrdinalIgnoreCase) &&
        left.IsUnchanged == right.IsUnchanged && (left.IsUnchanged || left.PlannedContent.SequenceEqual(right.PlannedContent));
    private static byte[] Read(FileStream stream)
    {
        if (stream.Length > 1024 * 1024) throw new ClientSettingsException("Native settings are too large.");
        stream.Position = 0; var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes); return bytes;
    }
    private static void Write(FileStream stream, byte[] bytes)
    {
        stream.Position = 0; stream.Write(bytes); stream.SetLength(bytes.Length); stream.Flush(flushToDisk: true);
    }
}
