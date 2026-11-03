using System.Text.Json.Nodes;
using static CoCern.Domain.JsonFields;

namespace CoCern.Domain.State;

/// <summary>
/// The anomaly monitor's stored document, read and written through named, typed members only: when the next check is due, when the last
/// one began and whose findings were last applied, the last raise per named query (<see cref="QueryCheck"/>), and the revision its
/// attention items carry. Every key it stores is declared in <see cref="Shape"/>.
/// </summary>
public sealed class AnomalyState(JsonObject json)
{
    public const string NextCheckAtKey = "nextCheckAt";
    public const string CheckedAtKey = "checkedAt";
    public const string FindingsAtKey = "findingsAt";
    public const string ChecksKey = "checks";
    public const string AttentionRevisionKey = "attentionRevision";

    /// <summary>Every key the monitor stores, and the shape of each query's record.</summary>
    public static StateShape Shape { get; } = CommonState.Shape.With(
        (NextCheckAtKey, null), (CheckedAtKey, null), (FindingsAtKey, null), (AttentionRevisionKey, null), (ChecksKey, StateShape.Map(QueryCheck.Shape)));

    /// <summary>When the next check is due; none until a manager session starts the monitor. The due-work timer reads it.</summary>
    public DateTimeOffset? NextCheckAt
    {
        get => OptionalDate(json, NextCheckAtKey);
        set => Write(NextCheckAtKey, value);
    }

    /// <summary>When the last check began.</summary>
    public DateTimeOffset? CheckedAt
    {
        get => OptionalDate(json, CheckedAtKey);
        set => Write(CheckedAtKey, value);
    }

    /// <summary>The due time of the last check whose findings were applied; older findings are stale.</summary>
    public DateTimeOffset? FindingsAt
    {
        get => OptionalDate(json, FindingsAtKey);
        set => Write(FindingsAtKey, value);
    }

    /// <summary>The record for <paramref name="query"/>, created empty when the query has none.</summary>
    public QueryCheck Check(string query) => new(ObjectAt(ObjectAt(json, ChecksKey), query));

    /// <summary>Replaces <paramref name="query"/>'s record with a fresh raise.</summary>
    public void Raised(string query, string key, string period, DateTimeOffset at) =>
        ObjectAt(json, ChecksKey)[query] = new JsonObject { [QueryCheck.OpenKey] = true, [QueryCheck.RowKey] = key, [QueryCheck.PeriodKey] = period, [QueryCheck.RaisedAtKey] = at.ToString("O") };

    /// <summary>The row key of each query whose item is open, for the next check to measure.</summary>
    public IReadOnlyDictionary<string, string> OpenKeys() =>
        (json[ChecksKey] as JsonObject ?? []).Where(entry => entry.Value is JsonObject record && new QueryCheck(record).Open)
            .ToDictionary(entry => entry.Key, entry => new QueryCheck(entry.Value!.AsObject()).Key, StringComparer.Ordinal);

    /// <summary>The next revision for an attention item this monitor raises or closes; each is higher than the last, so a late copy cannot reopen an item.</summary>
    public long NextRevision()
    {
        var revision = Number(json, AttentionRevisionKey) + 1;
        json[AttentionRevisionKey] = revision;
        return revision;
    }

    private void Write(string key, DateTimeOffset? value)
    {
        if (value is { } time) json[key] = time.ToString("O");
        else json.Remove(key);
    }
}

/// <summary>One named query's last raise: the row and period it raised for, when, and whether its item is still open.</summary>
public sealed class QueryCheck(JsonObject json)
{
    public const string OpenKey = "open";
    public const string RowKey = "key";
    public const string PeriodKey = "period";
    public const string RaisedAtKey = "raisedAt";

    public static StateShape Shape { get; } = StateShape.Object((OpenKey, null), (RowKey, null), (PeriodKey, null), (RaisedAtKey, null));

    public bool Open
    {
        get => IsTrue(json, OpenKey);
        set => json[OpenKey] = value;
    }

    public string Key => Text(json, RowKey);
    public string Period => Text(json, PeriodKey);
    public DateTimeOffset RaisedAt => Date(json, RaisedAtKey);
}
