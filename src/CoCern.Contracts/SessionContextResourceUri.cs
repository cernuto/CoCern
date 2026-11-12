using System.Globalization;

namespace CoCern.Contracts;

public sealed record SessionContextResourceAddress(string RepositoryId, string ContextId, string? EntryId = null, long EntryVersion = 0, int Part = 0, string? Generation = null);

/// <summary>Public addresses select data; repository authorization is always performed separately.</summary>
public static class SessionContextResourceUri
{
    public const string ManifestTemplate = "cocern://repositories/{repositoryId}/session-context/{contextId}/manifest";
    public const string PartTemplate = "cocern://repositories/{repositoryId}/session-context/{contextId}/generations/{generation}/entries/{entryId}/versions/{entryVersion}/parts/{part}";
    public static string ManifestUri(string repositoryId, string contextId) => $"cocern://repositories/{Uri.EscapeDataString(repositoryId)}/session-context/{contextId}/manifest";
    public static string PartUri(string repositoryId, string contextId, string entryId, long version, int part, string generation) =>
        $"cocern://repositories/{Uri.EscapeDataString(repositoryId)}/session-context/{contextId}/generations/{generation}/entries/{entryId}/versions/{version.ToString(CultureInfo.InvariantCulture)}/parts/{part.ToString(CultureInfo.InvariantCulture)}";

    public static bool TryParse(string? value, out SessionContextResourceAddress address)
    {
        address = null!;
        if (value is null || value.Length > 1024 || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != "cocern" || uri.Authority != "repositories" || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        var parts = uri.AbsolutePath.Split('/');
        if (parts.Length is not (5 or 12) || parts[0] != "" || parts[2] != "session-context" || !SessionContextLimits.ValidId(parts[3])) return false;
        var repository = Uri.UnescapeDataString(parts[1]);
        if (repository.Length is 0 or > 256 || repository.Any(c => char.IsControl(c) || c is '/' or '\\')) return false;
        if (parts.Length == 5 && parts[4] == "manifest") address = new(repository, parts[3]);
        else if (parts.Length == 12 && parts[4] == "generations" && Guid.TryParseExact(parts[5], "N", out _)
            && parts[6] == "entries" && SessionContextLimits.ValidId(parts[7]) && parts[8] == "versions" && parts[10] == "parts"
            && long.TryParse(parts[9], NumberStyles.None, CultureInfo.InvariantCulture, out var version) && version > 0
            && int.TryParse(parts[11], NumberStyles.None, CultureInfo.InvariantCulture, out var part) && part >= 0)
            address = new(repository, parts[3], parts[7], version, part, parts[5]);
        else return false;
        // Reject aliases, dot-path normalization and alternate numeric/escape spellings.
        return value == (address.EntryId is null ? ManifestUri(repository, address.ContextId) : PartUri(repository, address.ContextId, address.EntryId, address.EntryVersion, address.Part, address.Generation!));
    }
}
