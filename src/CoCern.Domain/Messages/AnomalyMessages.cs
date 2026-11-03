using System.Text.Json.Nodes;

namespace CoCern.Domain.Messages;

/// <summary><see cref="Operations.AnomalyStart"/>: a manager session opened, so its repository's monitor schedules a first check, unless one is due.</summary>
public sealed record AnomalyStart(string RepositoryId) : IInternalRequest<AnomalyStart>
{
    public static string Operation => Operations.AnomalyStart;
    public static string TargetKind => AggregateKinds.Anomaly;

    public JsonObject ToJson() => new() { ["repositoryId"] = RepositoryId };

    public static Decoding<AnomalyStart> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        return read.Result(new AnomalyStart(read.Text("repositoryId")));
    }
}

/// <summary>
/// <see cref="Operations.AnomalySchedule"/>: a due check begins, so the next is scheduled <see cref="IntervalSeconds"/> from now and
/// nothing else changes. The hub then runs the queries off the grain and sends <see cref="AnomalyCheck"/>.
/// </summary>
public sealed record AnomalySchedule(string RepositoryId, long IntervalSeconds) : IInternalRequest<AnomalySchedule>
{
    public static string Operation => Operations.AnomalySchedule;
    public static string TargetKind => AggregateKinds.Anomaly;

    public JsonObject ToJson() => new() { ["repositoryId"] = RepositoryId, ["intervalSeconds"] = IntervalSeconds };

    public static Decoding<AnomalySchedule> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        return read.Result(new AnomalySchedule(read.Text("repositoryId"), read.Number("intervalSeconds")));
    }
}

/// <summary>
/// <see cref="Operations.AnomalyCheck"/>: what one check found, for the check due at <see cref="CheckAt"/>. Findings for a check no newer
/// than the last applied one are stale and change nothing, so a late run never undoes a newer one.
/// </summary>
public sealed record AnomalyCheck(string RepositoryId, DateTimeOffset CheckAt, IReadOnlyList<AnomalyFinding> Findings) : IInternalRequest<AnomalyCheck>
{
    public static string Operation => Operations.AnomalyCheck;
    public static string TargetKind => AggregateKinds.Anomaly;

    public JsonObject ToJson() => new()
    {
        ["repositoryId"] = RepositoryId, ["checkAt"] = CheckAt.ToString("O"), ["findings"] = new JsonArray(Findings.Select(finding => (JsonNode)finding.ToJson()).ToArray())
    };

    public static Decoding<AnomalyCheck> Decode(JsonObject payload)
    {
        var read = new MessageReader(payload);
        var findings = read.Objects("findings").Select(AnomalyFinding.Read).ToArray();
        return read.Result(new AnomalyCheck(read.Text("repositoryId"), read.Date("checkAt"), findings));
    }
}

/// <summary>
/// One named query's verdict in a check: the snapshot's <see cref="Period"/>, and either the worst row out of range (<see cref="Anomaly"/>)
/// or, when the row an open item names was measured with enough samples and is back in range, <see cref="InRange"/>. Neither means too
/// little data to say.
/// </summary>
public sealed record AnomalyFinding(string Query, string Period, AnomalyRow? Anomaly = null, bool InRange = false)
{
    public JsonObject ToJson()
    {
        var json = new JsonObject { ["query"] = Query, ["period"] = Period };
        if (Anomaly is { } row) json["anomaly"] = new JsonObject { ["key"] = row.Key, ["value"] = row.Value, ["baseline"] = row.Baseline, ["samples"] = row.Samples };
        else if (InRange) json["inRange"] = true;
        return json;
    }

    /// <summary>Reads a finding as the hub's judge writes it.</summary>
    public static AnomalyFinding Decode(JsonObject json)
    {
        var read = new MessageReader(json);
        var finding = Read(read);
        return read.Result(finding).TryGet(out var decoded, out var fields) ? decoded : throw new ArgumentException("A finding lacks " + string.Join(", ", fields) + ".");
    }

    internal static AnomalyFinding Read(MessageReader read)
    {
        AnomalyRow? row = null;
        if (read.Has("anomaly"))
        {
            var anomaly = read.Object("anomaly");
            row = new AnomalyRow(anomaly.OptionalText("key") ?? "", anomaly.Real("value"), anomaly.Real("baseline"), anomaly.Number("samples", minimum: 0));
        }
        return new AnomalyFinding(read.Text("query"), read.Text("period"), row, read.OptionalFlag("inRange"));
    }
}

/// <summary>A row out of range: its key, the current value against its baseline (or fixed limit), and how many samples it was measured on.</summary>
public sealed record AnomalyRow(string Key, double Value, double Baseline, long Samples);
