namespace CoCern.Adapter;

/// <summary>
/// The secrets this process has held (client token, join code, machine credential, binding secrets, session tokens), so a raw exception
/// message written to a log cannot carry one. Insurance, not a fix for a known leak: values are registered where they are first held.
/// </summary>
internal static class SecretScrub
{
    internal const string Marker = "[secret removed]";
    private const int MinLength = 8;
    private const int MaxValues = 4096;
    private static readonly Lock Gate = new();
    private static readonly HashSet<string> Values = new(StringComparer.Ordinal);
    private static string[] _ordered = [];

    /// <summary>Ignores null and anything shorter than 8 characters. Thread-safe. Holds at most 4,096 values.</summary>
    internal static void Add(string? secret)
    {
        if (secret is null || secret.Length < MinLength) return;
        lock (Gate)
        {
            // When full, a further value is simply not held: nothing is evicted.
            if (Values.Count >= MaxValues || !Values.Add(secret)) return;
            Volatile.Write(ref _ordered, [.. Values.OrderByDescending(value => value.Length)]);
        }
    }

    /// <summary>Replaces every held value with Marker, longest first, by ordinal comparison.</summary>
    internal static string Clean(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var ordered = Volatile.Read(ref _ordered);
        foreach (var secret in ordered)
            if (text.Contains(secret, StringComparison.Ordinal)) text = text.Replace(secret, Marker, StringComparison.Ordinal);
        return text;
    }
}
