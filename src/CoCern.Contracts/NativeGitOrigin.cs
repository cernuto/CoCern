using System.Text.RegularExpressions;

namespace CoCern.Contracts;

/// <summary>Credential-free identity for a conservative subset of network Git origins. No DNS, filesystem or network access.</summary>
public static class NativeGitOrigin
{
    /// <summary>Only bounded credential-free origin identity and local path digests may cross the private resolution boundary.</summary>
    public static bool ValidCheckoutEvidence(System.Text.Json.JsonElement value)
    {
        if (value.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
        {
            if (!seen.Add(field.Name) || field.Value.ValueKind != System.Text.Json.JsonValueKind.String) return false;
            var text = field.Value.GetString()!;
            if (field.Name == "origin") { if (Canonical(text) != text) return false; }
            else if (field.Name is not ("checkout" or "common") || text.Length != 64 || !text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        }
        return seen.Count > 0;
    }

    public static string? Canonical(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Any(char.IsControl) || value.Contains('\\')) return null;
        var suffix = value.IndexOfAny(['?', '#']);
        var raw = suffix < 0 ? value : value[..suffix];
        string host, path;
        if (raw.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "ssh") ||
                uri.HostNameType != UriHostNameType.Dns || uri.Port != -1 && uri.Port != (uri.Scheme == "https" ? 443 : 22)) return null;
            var slash = raw.IndexOf('/', raw.IndexOf("://", StringComparison.Ordinal) + 3);
            if (slash < 0) return null;
            host = uri.IdnHost; path = raw[(slash + 1)..];
        }
        else
        {
            // SCP syntax requires user@host; a drive letter or local path is never a remote.
            var match = Regex.Match(raw, @"\A[A-Za-z0-9._-]+@([A-Za-z0-9.-]+):(.+)\z", RegexOptions.CultureInvariant);
            if (!match.Success) return null;
            host = match.Groups[1].Value; path = match.Groups[2].Value;
        }
        if (host.EndsWith('.') || !Regex.IsMatch(host, @"\A[a-zA-Z0-9.-]+\z") || !host.Contains('.') ||
            !Regex.IsMatch(path, @"\A[A-Za-z0-9._/-]+\z") || path.Split('/').Any(part => part is "" or "." or "..")) return null;
        if (path.EndsWith(".git", StringComparison.Ordinal)) path = path[..^4];
        if (path.Length == 0 || path.EndsWith('/') || path.Split('/').Any(part => part is "." or "..")) return null;
        return $"https://{host.ToLowerInvariant()}/{path}";
    }
}
