using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoCern.Contracts;

namespace CoCern.Adapter;

internal sealed record NativeCheckout(string Root, string? Common, string? Origin)
{
    internal static NativeCheckout Inspect(string cwd)
    {
        if (!Path.IsPathFullyQualified(cwd) || cwd.Length > 32768 || cwd.Any(char.IsControl) ||
            CoordinationTools.NativeGitTopLevel(cwd) is not { } root) throw new IOException("The native event needs a verified Git checkout.");
        var common = CoordinationTools.NativeGitRead(root, "rev-parse", "--path-format=absolute", "--git-common-dir");
        var origin = NativeGitOrigin.Canonical(CoordinationTools.NativeGitRead(root, "config", "--local", "--get-all", "remote.origin.url"));
        return new(root, common is not null && Path.IsPathFullyQualified(common) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(common)) : null, origin);
    }

    internal JsonElement Preflight(bool local, string? repositoryId = null)
    {
        var evidence = new Dictionary<string,string>();
        if (!string.IsNullOrEmpty(repositoryId))
        {
            if (!CoCern.Contracts.NativeGitOrigin.ValidRepositoryHint(repositoryId)) throw new IOException("The configured repository ID is invalid.");
            evidence["repositoryId"] = repositoryId;
        }
        if (local)
        {
            evidence["checkout"] = Hash(Root);
            if (Common is not null) evidence["common"] = Hash(Common);
        }
        if (Origin is not null) evidence["origin"] = Origin;
        // No remote identity is still a valid preflight: the hub may have exactly one authorized clone-less repository.
        return JsonSerializer.SerializeToElement(new { nativePreflight = evidence });
    }

    internal static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
