namespace CoCern.Contracts;

/// <summary>Repository spelling is chosen by configuration, never by a request. Ambiguous names fail closed.</summary>
public static class RepositoryIds
{
    public static string? Match(string supplied, IEnumerable<string> known)
    {
        if (string.IsNullOrWhiteSpace(supplied)) return null;
        string? canonical = null;
        foreach (var id in known)
        {
            if (!string.Equals(id, supplied, StringComparison.OrdinalIgnoreCase)) continue;
            if (canonical is not null && canonical != id) return null;
            canonical = id;
        }
        return canonical;
    }

    public static string[] Configured(IEnumerable<string> names)
    {
        var result = names.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
        if (result.Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Length)
            throw new InvalidOperationException("Repository configuration contains IDs that differ only by case.");
        return result;
    }
}
