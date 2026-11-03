using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CoCern.Domain.Messages;

/// <summary>
/// What an outbox does with a typed message its target refuses for good. A failure a later attempt can get past (storage, or the call
/// itself) is retried in either class.
/// </summary>
public enum DeliveryClass
{
    /// <summary>A state transfer nothing sends again: lost, it would leave two aggregates disagreeing for good, so it is retried until it lands.</summary>
    Retry,

    /// <summary>A report its sender sends again whole and its target orders by a stamp: a refused one is dropped and counted, and a later one replaces it.</summary>
    DropAndCount,

    /// <summary>
    /// Advice (an attention item, a standing order's step) its target may refuse for good: a refusal would repeat on every retry, so it is
    /// dropped. Nothing is lost that the sender relies on; an attention item a standing order raised and its session refused counts as a
    /// failed run of that order.
    /// </summary>
    Advisory
}

/// <summary>
/// A message one aggregate sends another through its outbox. Each has one shape, one sender and one delivery class: an operation sent
/// in two shapes, or by two senders that resend differently, is two messages. Its fences (a sequence, a close's number, a time) are
/// required fields, so an emitter cannot leave one out without its target saying so.
/// </summary>
public interface IEffectMessage<TSelf> where TSelf : class, IEffectMessage<TSelf>
{
    /// <summary>The internal operation the message is sent as (one of <see cref="Operations"/>).</summary>
    static abstract string Operation { get; }

    /// <summary>The kind of aggregate that receives it (one of <see cref="AggregateKinds"/>).</summary>
    static abstract string TargetKind { get; }

    static abstract DeliveryClass Delivery { get; }

    /// <summary>Reads the message from an effect's payload, or names each field that is missing or invalid.</summary>
    static abstract Decoding<TSelf> Decode(JsonObject payload);

    /// <summary>The effect's payload: the one shape <see cref="Decode"/> reads back.</summary>
    JsonObject ToJson();
}

/// <summary>
/// A decoded message, or the names of its fields that were missing or invalid. Never their values: some messages carry a session's
/// secret. Whether a well-formed message is stale (older than what its target already holds) is for its handler to decide, from its
/// own state; decoding never says so.
/// </summary>
public sealed class Decoding<T> where T : class
{
    private Decoding(T? message, IReadOnlyList<string> fields) { Message = message; Fields = fields; }

    public T? Message { get; }

    /// <summary>The missing or invalid fields, by name; empty when decoded.</summary>
    public IReadOnlyList<string> Fields { get; }

    public static Decoding<T> Decoded(T message) => new(message, []);
    public static Decoding<T> Malformed(IReadOnlyList<string> fields) => new(null, fields);

    public bool TryGet([NotNullWhen(true)] out T? message, out IReadOnlyList<string> fields)
    {
        message = Message;
        fields = Fields;
        return message is not null;
    }
}

/// <summary>
/// Reads a message's fields, noting the name of each one missing or of the wrong type, so a decoder reports them all at once. A nested
/// object's fields are named with its own name in front (<c>record.principalId</c>).
/// </summary>
internal sealed class MessageReader(JsonObject payload, string prefix = "", List<string>? problems = null)
{
    private readonly List<string> problems = problems ?? [];

    private void Problem(string name) => problems.Add(prefix + name);

    /// <summary>A required, nonempty string.</summary>
    public string Text(string name)
    {
        if (payload[name] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0) return text;
        Problem(name);
        return "";
    }

    /// <summary>A string, or null when absent.</summary>
    public string? OptionalText(string name)
    {
        if (payload[name] is null) return null;
        if (payload[name] is JsonValue value && value.TryGetValue<string>(out var text)) return text;
        Problem(name);
        return null;
    }

    /// <summary>A required whole number, at least <paramref name="minimum"/>.</summary>
    public long Number(string name, long minimum = long.MinValue)
    {
        if (payload[name] is null) Problem(name);
        return OptionalNumber(name, minimum) ?? 0;
    }

    /// <summary>A whole number of at least <paramref name="minimum"/>, or null when absent.</summary>
    public long? OptionalNumber(string name, long minimum = long.MinValue)
    {
        if (payload[name] is null) return null;
        if (payload[name] is JsonValue value && value.GetValueKind() == JsonValueKind.Number && long.TryParse(value.ToJsonString(), out var number) && number >= minimum) return number;
        Problem(name);
        return null;
    }

    /// <summary>A required boolean.</summary>
    public bool Flag(string name)
    {
        if (payload[name] is JsonValue value && value.TryGetValue<bool>(out var flag)) return flag;
        Problem(name);
        return false;
    }

    /// <summary>A boolean, false when absent.</summary>
    public bool OptionalFlag(string name)
    {
        if (payload[name] is null) return false;
        if (payload[name] is JsonValue value && value.TryGetValue<bool>(out var flag)) return flag;
        Problem(name);
        return false;
    }

    /// <summary>A required time.</summary>
    public DateTimeOffset Date(string name)
    {
        if (payload[name] is null) Problem(name);
        return OptionalDate(name) ?? DateTimeOffset.MinValue;
    }

    /// <summary>A time, or null when absent.</summary>
    public DateTimeOffset? OptionalDate(string name)
    {
        if (payload[name] is null) return null;
        if (payload[name] is JsonValue value && value.TryGetValue<string>(out var text) && DateTimeOffset.TryParse(text, out var date)) return date;
        Problem(name);
        return null;
    }

    /// <summary>A required number, integer or not.</summary>
    public double Real(string name)
    {
        if (payload[name] is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<double>(out var number) && double.IsFinite(number)) return number;
        Problem(name);
        return 0;
    }

    /// <summary>A required array of objects, each read with its own reader (its problems named under <paramref name="name"/>).</summary>
    public IReadOnlyList<MessageReader> Objects(string name)
    {
        if (payload[name] is not JsonArray items) { Problem(name); return []; }
        var readers = new List<MessageReader>();
        foreach (var item in items)
        {
            if (item is JsonObject nested) readers.Add(new(nested, prefix + name + ".", problems));
            else Problem(name);
        }
        return readers;
    }

    /// <summary>A required array of strings, each empty or not; any other element makes the whole field invalid.</summary>
    public IReadOnlyList<string> Strings(string name)
    {
        if (payload[name] is JsonArray items && items.All(item => item is JsonValue value && value.TryGetValue<string>(out _)))
            return items.Select(item => item!.GetValue<string>()).ToArray();
        Problem(name);
        return [];
    }

    /// <summary>Whether the payload has <paramref name="name"/> at all, null or not.</summary>
    public bool Has(string name) => payload.ContainsKey(name);

    /// <summary>Any JSON value, copied, or null when absent.</summary>
    public JsonNode? Node(string name) => payload[name]?.DeepClone();

    /// <summary>A required nested object, read with its own reader (its problems named under <paramref name="name"/>).</summary>
    public MessageReader Object(string name)
    {
        if (payload[name] is JsonObject nested) return new(nested, prefix + name + ".", problems);
        Problem(name);
        return new(new JsonObject(), prefix + name + ".", []);
    }

    /// <summary>Notes <paramref name="name"/> as invalid unless <paramref name="valid"/>.</summary>
    public void Check(bool valid, string name)
    {
        if (!valid) Problem(name);
    }

    public Decoding<T> Result<T>(T message) where T : class =>
        problems.Count == 0 ? Decoding<T>.Decoded(message) : Decoding<T>.Malformed(problems.Distinct(StringComparer.Ordinal).ToArray());
}
