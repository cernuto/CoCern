using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CoCern.Contracts.Dashboard;
using CoCern.Core;
using CoCern.Domain;
using CoCern.Contracts;

namespace CoCern.Storage;

public sealed partial class SqliteStore : IRelayObservationSource
{
    private const int MaximumSessions = 200;
    private const int MaximumStateBytes = 1024 * 1024;
    private const int MaximumSampleBytes = 4 * MaximumStateBytes;
    private static readonly TimeSpan ObservationBudget = TimeSpan.FromMilliseconds(100);

    public async Task<RelayObservation?> ReadObservationAsync(DateTimeOffset now, string incarnationId, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        var machines = new List<DashboardMachine>();
        var chats = new Dictionary<string, ObservedChat>(StringComparer.Ordinal);
        var truncated = false;
        var bytes = 0;
        string? discoveryPayload = null;
        int sessionCount;

        if (!await _gate.WaitAsync(0, cancellationToken)) return null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using (var count = Command($"SELECT COUNT(*) FROM grain_state WHERE state_name='session' AND grain_type='cocern.session'{OpenSessionsOnly};"))
                sessionCount = Convert.ToInt32(count.ExecuteScalar());
            using var command = Command("SELECT CASE WHEN length(CAST(payload AS BLOB)) <= $maximum THEN payload ELSE NULL END, length(CAST(payload AS BLOB)) FROM grain_state WHERE state_name='discovery' AND grain_type='cocern.discovery' ORDER BY grain_key LIMIT 1;");
            command.Parameters.AddWithValue("$maximum", MaximumStateBytes);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                if (reader.IsDBNull(0)) { truncated = true; warnings.Add("Discovery metadata exceeded the bounded observation size."); }
                else
                {
                    bytes = reader.GetInt32(1);
                    discoveryPayload = reader.GetString(0);
                }
            }
        }
        finally { _gate.Release(); }

        // Only bounded SQL/raw copies use the shared writer gate. JSON projection runs after release.
        if (discoveryPayload is not null)
        {
            try { ProjectDiscovery(discoveryPayload, now, machines, chats, warnings); }
            catch (JsonException) { truncated = true; warnings.Add("Some discovery metadata could not be observed."); }
            truncated |= warnings.Contains("The discovery session observation is truncated.") || warnings.Contains("The machine observation is truncated.");
        }

        var rawSessions = new List<string>();
        var rowsRead = 0;
        var keys = chats.Values.Select(chat => chat.GrainKey).Distinct(StringComparer.Ordinal).ToArray();
        if (Stopwatch.GetElapsedTime(started) <= ObservationBudget)
        {
            if (!await _gate.WaitAsync(0, cancellationToken)) return null;
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                // Session grain keys are relaySessionId itself, independent of repository.
                if (keys.Length > 0)
                    CopySessionRows(keys, announcedOnly: true, rawSessions, ref rowsRead, ref bytes, ref truncated, started, warnings, cancellationToken);
                if (rowsRead <= MaximumSessions)
                    CopySessionRows(keys, announcedOnly: false, rawSessions, ref rowsRead, ref bytes, ref truncated, started, warnings, cancellationToken);
            }
            finally { _gate.Release(); }
        }
        else truncated = true;

        var persisted = new Dictionary<string, DashboardSession>(StringComparer.Ordinal);
        // Sessions whose stored record says they were closed. A node's last announcement can still name one (until it announces again, or for
        // as long as its PC stays off), and such a chat is no longer one to show or to message.
        var closed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var payload in rawSessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started) > ObservationBudget) { truncated = true; break; }
            try
            {
                var session = ProjectSession(payload, incarnationId, out var closedId);
                if (closedId is not null) { closed.Add(closedId); continue; }
                if (session is null) { truncated = true; continue; }
                if (!persisted.TryGetValue(session.SessionId, out var existing) || PreferPersisted(session, existing, chats))
                    persisted[session.SessionId] = session;
            }
            catch (JsonException) { truncated = true; warnings.Add("Some session metadata could not be observed."); }
        }

        var sessions = new Dictionary<string, DashboardSession>(StringComparer.Ordinal);
        // Reserve inventory slots for announced chats even when their persisted row is absent, but never for one whose session was closed.
        foreach (var announced in chats.Values.OrderBy(chat => chat.Session.SessionId, StringComparer.Ordinal))
        {
            var chat = announced.Session;
            var open = persisted.Remove(chat.SessionId, out var session);
            if (!open && closed.Contains(chat.SessionId)) continue;
            // The stored label is the hub's own and follows a rename through session_bind; the announced one is the node's copy, which can lag.
            if (open && session!.RepositoryId == chat.RepositoryId)
                chat = session with { MachineId = chat.MachineId, MachineName = chat.MachineName, Provider = chat.Provider, IsMapped = chat.IsMapped, Reachable = chat.Reachable, Label = string.IsNullOrEmpty(session.Label) ? chat.Label : session.Label };
            sessions.Add(chat.SessionId, chat);
        }
        foreach (var session in persisted.Values)
        {
            if (sessions.Count >= MaximumSessions) { truncated = true; break; }
            sessions.TryAdd(session.SessionId, session);
        }
        if (!await ProjectSessionActivityAsync(sessions, now, started, cancellationToken))
            return null; // Retain the previous coherent observation and its timestamp rather than erase known last-call times.
        var interactions = await ReadInteractionsAsync(sessions, now, started, cancellationToken);
        if (interactions is null) return null;
        truncated |= sessionCount > MaximumSessions;
        if (truncated)
        {
            warnings.Add("The session observation is truncated to its count, time or size limit.");
            warnings.Add("Machine and reachable counts describe the bounded observed inventory; the session total counts persisted sessions.");
        }
        return new() { SessionCount = sessionCount, Machines = machines.ToArray(), Sessions = sessions.Values.ToArray(), SessionsTruncated = truncated, Interactions = interactions, Warnings = warnings.ToArray() };
    }

    private async Task<bool> ProjectSessionActivityAsync(Dictionary<string, DashboardSession> sessions, DateTimeOffset now, long started, CancellationToken cancellationToken)
    {
        if (Stopwatch.GetElapsedTime(started) > ObservationBudget) return false;
        if (sessions.Count == 0) return true;
        if (!await _gate.WaitAsync(0, cancellationToken)) return false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // One covering-index seek per bounded inventory row; never scan the retained activity history for each session.
            using var command = Command("SELECT at_ticks FROM activity_log WHERE repository_id=$repository AND session_id=$session AND kind='call' AND ok=1 AND at_ticks <= $now ORDER BY at_ticks DESC LIMIT 1;");
            var repository = command.Parameters.AddWithValue("$repository", "");
            var sessionId = command.Parameters.AddWithValue("$session", "");
            command.Parameters.AddWithValue("$now", now.UtcTicks);
            foreach (var entry in sessions.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Stopwatch.GetElapsedTime(started) > ObservationBudget) return false;
                repository.Value = entry.Value.RepositoryId;
                sessionId.Value = entry.Value.SessionId;
                if (command.ExecuteScalar() is long ticks)
                    sessions[entry.Key] = entry.Value with { LastActivityAt = new DateTimeOffset(ticks, TimeSpan.Zero) };
            }
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Leaves out closed sessions, which keep a record marked expiredAt. The state is stored as an escaped JSON string (quotes as "), so only
    /// the key itself matches: a label holding the same text is escaped once more (\\u0022) and never does.
    /// </summary>
    private const string OpenSessionsOnly = " AND CAST(payload AS TEXT) NOT LIKE '%\\u0022expiredAt\\u0022%' AND CAST(payload AS TEXT) NOT LIKE '%\\\"expiredAt\\\"%'";

    /// <summary>
    /// Copies stored sessions: those the discovery announcement names (closed ones included, so a closed chat can be left out), or the rest,
    /// open ones only, so that closed records, which are kept, never use up the bounded read before the sessions still open.
    /// </summary>
    private void CopySessionRows(string[] keys, bool announcedOnly, List<string> payloads, ref int rowsRead, ref int bytes, ref bool truncated, long started, HashSet<string> warnings, CancellationToken cancellationToken)
    {
        var parameters = string.Join(", ", Enumerable.Range(0, keys.Length).Select(index => "$key" + index));
        var filter = (keys.Length == 0 ? "" : $" AND grain_key {(announcedOnly ? "IN" : "NOT IN")} ({parameters})") + (announcedOnly ? "" : OpenSessionsOnly);
        using var command = Command($"SELECT CASE WHEN length(CAST(payload AS BLOB)) <= $maximum THEN payload ELSE NULL END, length(CAST(payload AS BLOB)) FROM grain_state WHERE state_name='session' AND grain_type='cocern.session'{filter} ORDER BY grain_key LIMIT $limit;");
        command.Parameters.AddWithValue("$maximum", MaximumStateBytes);
        command.Parameters.AddWithValue("$limit", MaximumSessions + 1 - rowsRead);
        for (var index = 0; index < keys.Length; index++) command.Parameters.AddWithValue("$key" + index, keys[index]);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowsRead++;
            if (rowsRead > MaximumSessions || Stopwatch.GetElapsedTime(started) > ObservationBudget) { truncated = true; rowsRead = MaximumSessions + 1; break; }
            if (reader.IsDBNull(0)) { truncated = true; warnings.Add("Some sessions exceeded the bounded observation size."); continue; }
            var size = reader.GetInt32(1);
            if (size > MaximumSampleBytes - bytes) { truncated = true; rowsRead = MaximumSessions + 1; break; }
            bytes += size;
            payloads.Add(reader.GetString(0));
        }
    }

    private static bool PreferPersisted(DashboardSession candidate, DashboardSession current, Dictionary<string, ObservedChat> chats)
    {
        if (chats.TryGetValue(candidate.SessionId, out var chat))
        {
            var candidateMatches = candidate.RepositoryId == chat.Session.RepositoryId;
            var currentMatches = current.RepositoryId == chat.Session.RepositoryId;
            if (candidateMatches != currentMatches) return candidateMatches;
        }
        if (candidate.IncarnationMismatch != current.IncarnationMismatch) return !candidate.IncarnationMismatch;
        if (candidate.ReportedAt != current.ReportedAt) return Nullable.Compare(candidate.ReportedAt, current.ReportedAt) > 0;
        return StringComparer.Ordinal.Compare(candidate.RepositoryId, current.RepositoryId) < 0;
    }

    public async Task<DatabaseMetrics?> ReadDatabaseMetricsAsync(int rowCountLimit = 10_000, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rowCountLimit);
        rowCountLimit = Math.Min(rowCountLimit, 10_000);
        if (!await _gate.WaitAsync(0, cancellationToken)) return null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var started = Stopwatch.GetTimestamp();
            var names = new List<string>();
            // FTS5 shadow tables are storage internals of their virtual table, so they are left out of the inventory.
            using (var command = Command("SELECT name FROM pragma_table_list WHERE schema='main' AND type IN ('table', 'virtual') AND name NOT LIKE 'sqlite_%' ORDER BY name LIMIT 33;"))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) names.Add(reader.GetString(0));
            var tables = new List<DatabaseTableMetrics>();
            var truncated = names.Count > 32;
            foreach (var name in names.Take(32))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Stopwatch.GetElapsedTime(started) > ObservationBudget) { truncated = true; break; }
                // Names come solely from engine metadata and are quoted as identifiers, never caller SQL.
                using var command = Command($"SELECT COUNT(*) FROM (SELECT 1 FROM \"{name.Replace("\"", "\"\"")}\" LIMIT $limit);");
                command.Parameters.AddWithValue("$limit", rowCountLimit);
                var count = Convert.ToInt64(command.ExecuteScalar());
                tables.Add(new() { Schema = "main", Name = Safe(name), RowCount = count, CountKind = count >= rowCountLimit ? "lower-bound" : "exact" });
            }
            long? size;
            using (var pages = Command("PRAGMA page_count;"))
            using (var pageSize = Command("PRAGMA page_size;"))
                size = checked(Convert.ToInt64(pages.ExecuteScalar()) * Convert.ToInt64(pageSize.ExecuteScalar()));
            return new() { Provider = "SQLite", Name = "CoCern", ObservedAt = DateTimeOffset.UtcNow, SizeBytes = size, Tables = tables.ToArray(), ArchivedTasks = CountArchived(), Truncated = truncated, Warning = truncated ? "Table metrics reached their count or time limit." : null };
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// The stored session as the dashboard shows it, or null. A closed session keeps its record (marked expiredAt) so its chat is told it
    /// expired; it is no longer a session to show, and <paramref name="closedId"/> names it so that an announcement of it is left out too.
    /// </summary>
    private static DashboardSession? ProjectSession(string payload, string incarnation, out string? closedId)
    {
        closedId = null;
        using var envelope = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 64 });
        using var document = Document(envelope.RootElement);
        var state = document.RootElement;
        var id = Text(state, "sessionId");
        if (string.IsNullOrEmpty(id)) return null;
        if (Property(state, "expiredAt").ValueKind == JsonValueKind.String) { closedId = id; return null; }
        var inbox = Property(state, "inbox");
        var effects = Property(envelope.RootElement, "Effects");
        var bindingVersion = Property(state, "bindingVersion");
        var hasVersion = bindingVersion.ValueKind == JsonValueKind.Number && bindingVersion.TryGetInt64(out var reportedVersion) && reportedVersion >= 0;
        var binding = Property(state, "binding");
        var hasBinding = binding.ValueKind == JsonValueKind.Object && binding.EnumerateObject().Any();
        return new()
        {
            SessionId = id, Label = Text(state, "label"), MachineId = Text(state, "machineId"), RepositoryId = Text(state, "repositoryId"),
            ReportedAt = Date(state, "observedAt"), FreshUntil = Date(state, "freshUntil"),
            OpenedAt = Date(state, "openedAt"),
            BindingVersion = bindingVersion.ValueKind == JsonValueKind.Number && bindingVersion.TryGetInt64(out var version) && version >= 0 ? version : null,
            HasBindingReport = hasBinding ? true : hasVersion ? false : null,
            IncarnationMismatch = RawText(state, "hubIncarnation") != incarnation,
            UnreadMessages = inbox.ValueKind == JsonValueKind.Object ? inbox.EnumerateObject().Count() : inbox.ValueKind == JsonValueKind.Undefined ? 0 : null,
            PendingEffects = effects.ValueKind == JsonValueKind.Array ? effects.GetArrayLength() : null
        };
    }

    private static void ProjectDiscovery(string payload, DateTimeOffset now, List<DashboardMachine> machines, Dictionary<string, ObservedChat> chats, HashSet<string> warnings)
    {
        using var envelope = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 64 });
        using var document = Document(envelope.RootElement);
        var source = Property(document.RootElement, "machines");
        if (source.ValueKind != JsonValueKind.Object) return;
        foreach (var property in source.EnumerateObject())
        {
            if (machines.Count >= 256) { warnings.Add("The machine observation is truncated."); break; }
            var machine = property.Value;
            var id = Safe(property.Name);
            var name = Text(machine, "machineName");
            var lastSeen = Date(machine, "lastSeen");
            var live = lastSeen is { } seen && seen <= now && now - seen <= DiscoveryLimits.LivenessTimeout;
            var announced = Property(machine, "chats");
            var count = announced.ValueKind == JsonValueKind.Array ? announced.GetArrayLength() : 0;
            machines.Add(new() { MachineId = id, Name = name, LastSeenAt = lastSeen, Reachable = live, SessionCount = count });
            if (announced.ValueKind != JsonValueKind.Array) continue;
            foreach (var chat in announced.EnumerateArray().Take(64))
            {
                var grainKey = RawText(chat, "relaySessionId");
                var provider = Text(chat, "provider");
                var session = new DashboardSession
                {
                    SessionId = Safe(grainKey), RepositoryId = Text(chat, "repositoryId"), Label = Text(chat, "label"), Provider = provider,
                    MachineId = id, MachineName = name, IsMapped = provider.Length > 0 && !provider.Equals(ChatProviders.Unknown, StringComparison.OrdinalIgnoreCase),
                    Reachable = live && (provider.Equals(ChatProviders.Codex, StringComparison.OrdinalIgnoreCase) ? Flag(chat, "wakeable") : provider.Equals(ChatProviders.Claude, StringComparison.OrdinalIgnoreCase) && Flag(chat, "waiterRunning")),
                    ReportedAt = lastSeen, FreshUntil = lastSeen?.Add(DiscoveryLimits.LivenessTimeout)
                };
                if (string.IsNullOrEmpty(session.SessionId)) continue;
                var candidate = new ObservedChat(grainKey, session);
                if (chats.TryGetValue(session.SessionId, out var current))
                {
                    if (PreferAnnouncement(candidate.Session, current.Session) || (candidate.Session == current.Session && StringComparer.Ordinal.Compare(candidate.GrainKey, current.GrainKey) < 0)) chats[session.SessionId] = candidate;
                }
                else if (chats.Count < MaximumSessions) chats.Add(session.SessionId, candidate);
                else warnings.Add("The discovery session observation is truncated.");
            }
        }
    }

    private static JsonDocument Document(JsonElement envelope)
    {
        var json = Property(Property(envelope, "Value"), "Json");
        return JsonDocument.Parse(json.ValueKind == JsonValueKind.String ? json.GetString()! : "{}", new JsonDocumentOptions { MaxDepth = 64 });
    }


    private sealed record ObservedChat(string GrainKey, DashboardSession Session);

    private static bool PreferAnnouncement(DashboardSession candidate, DashboardSession current)
    {
        if (candidate.Reachable != current.Reachable) return candidate.Reachable;
        if (candidate.IsMapped != current.IsMapped) return candidate.IsMapped;
        if (candidate.ReportedAt != current.ReportedAt) return Nullable.Compare(candidate.ReportedAt, current.ReportedAt) > 0;
        var comparison = StringComparer.Ordinal.Compare(candidate.RepositoryId, current.RepositoryId);
        if (comparison == 0) comparison = StringComparer.Ordinal.Compare(candidate.MachineId, current.MachineId);
        if (comparison == 0) comparison = StringComparer.Ordinal.Compare(candidate.Provider, current.Provider);
        if (comparison == 0) comparison = StringComparer.Ordinal.Compare(candidate.Label, current.Label);
        return comparison < 0;
    }

    private static string RawText(JsonElement value, string name) { var item = Property(value, name); return item.ValueKind == JsonValueKind.String ? item.GetString()! : ""; }
    private static JsonElement Property(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var result) ? result : default;
    private static string Text(JsonElement value, string name) { var item = Property(value, name); return item.ValueKind == JsonValueKind.String ? Safe(item.GetString()!) : ""; }
    private static bool Flag(JsonElement value, string name) => Property(value, name).ValueKind == JsonValueKind.True;
    private static DateTimeOffset? Date(JsonElement value, string name) { var item = Property(value, name); return item.ValueKind == JsonValueKind.String && item.TryGetDateTimeOffset(out var date) ? date : null; }
    private static string Safe(string value)
    {
        var result = new StringBuilder(Math.Min(value.Length, 256));
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format) continue;
            if (result.Length + rune.Utf16SequenceLength > 256) break;
            result.Append(rune.ToString());
        }
        return result.ToString();
    }
}



