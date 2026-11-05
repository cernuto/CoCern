using System.Text.Json;

namespace CoCern.Adapter.Node;

/// <summary>
/// Which sessions an owner armed, so a replacement backend can tell its own chats from any other located handle. One private file per session and owner,
/// <c>armed\&lt;sessionId&gt;.&lt;ownerPid&gt;.json</c> in the connection folder, holding the owner's ID and start time and the session ID, and no token.
/// The owner is what survives a backend swap: the development launcher when there is one, otherwise this process, whose records only it can match.
/// So a deploy under the same launcher re-arms, and a restart of the client does not: that chat waits for its next call.
/// </summary>
internal sealed class ArmedChats(string connectionFolder, ProcessFacts? owner, Func<ProcessFacts, bool>? isAlive = null)
{
    /// <summary>Set by the development launcher on each backend it starts; absent from any other adapter.</summary>
    private const string LauncherMarker = "COCERN_DEVELOPMENT_GENERATION";
    private readonly string folder = Path.Combine(connectionFolder, "armed");
    private readonly Func<ProcessFacts, bool> alive = isAlive ?? OwningProcess.IsAlive;

    internal bool HasOwner => owner is not null;

    public static ArmedChats ForThisProcess(string connectionFolder) => new(connectionFolder, OwnerOf(Environment.ProcessId, Environment.GetEnvironmentVariable, OwningProcess.Read));

    /// <summary>The launcher that started <paramref name="processId"/>, else that process itself; null where processes can't be read. A parent younger than its child is a reused ID, not a launcher.</summary>
    internal static ProcessFacts? OwnerOf(int processId, Func<string, string?> environment, Func<int, ProcessFacts?> read)
    {
        if (read(processId) is not { } self) return null;
        return !string.IsNullOrEmpty(environment(LauncherMarker)) && read(self.ParentId) is { } launcher && launcher.StartedUtc <= self.StartedUtc ? launcher : self;
    }

    /// <summary>Remembers that this owner armed <paramref name="session"/>. False when it could not, which costs only the re-arm after a swap.</summary>
    public bool Record(string session)
    {
        if (owner is null || !NodeState.IsFileId(session)) return false;
        try
        {
            Directory.CreateDirectory(folder);
            NodeState.WritePrivately(PathFor(session, owner.Id), JsonSerializer.SerializeToUtf8Bytes(new { ownerPid = owner.Id, ownerStartedUtc = owner.StartedUtc, sessionId = session }));
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    /// <summary>Whether this owner armed <paramref name="session"/>. The start time keeps a later process that reuses the ID from matching.</summary>
    public bool IsArmedHere(string session) =>
        owner is not null && NodeState.IsFileId(session) && ReadRecord(PathFor(session, owner.Id)) is { } record && record.Session == session &&
        record.Owner.Id == owner.Id && (record.Owner.StartedUtc - owner.StartedUtc).Duration() < OwningProcess.StartTolerance;

    /// <summary>Drops records of owners that have ended, of sessions whose handle has gone, and any that can't be read.</summary>
    public void Prune(Func<string, bool> handleExists)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in ChatLocators.Files(folder, "*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (ReadRecord(file) is not { } record || !alive(record.Owner) || !handleExists(record.Session)) File.Delete(file);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Tried again by the next adapter that starts. */ }
        }
    }

    private string PathFor(string session, int ownerId) => Path.Combine(folder, $"{session}.{ownerId}.json");

    private static (ProcessFacts Owner, string Session)? ReadRecord(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var data = document.RootElement;
            if (data.ValueKind != JsonValueKind.Object || ChatLocators.Text(data, "sessionId") is not { } session || !NodeState.IsFileId(session) ||
                !data.TryGetProperty("ownerPid", out var pid) || !pid.TryGetInt32(out var id) || id <= 0 ||
                !data.TryGetProperty("ownerStartedUtc", out var started) || !started.TryGetDateTime(out var at)) return null;
            return (new ProcessFacts(id, 0, "", at.ToUniversalTime()), session);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or FormatException) { return null; }
    }
}
