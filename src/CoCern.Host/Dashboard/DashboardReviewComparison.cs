using CoCern.Contracts.Dashboard;

namespace CoCern.Host.Dashboard;

internal static class DashboardReviewComparison
{
    internal static async Task<DashboardRevisionComparison> ReadAsync(DocRepository repo, GitCli git,
        string commit, string branch, int? pullRequest, CancellationToken token)
    {
        string? url = null;
        if (pullRequest is > 0)
        {
            var remote = await git.RunAsync(repo.ClonePath, ["remote", "get-url", "origin"], 2048, null, token);
            if (remote.ExitCode == 0 && !remote.Truncated) url = PullRequestUrl(remote.Text.Trim(), pullRequest.Value);
        }
        var docGit = new DocGit(git, repo);
        var head = await docGit.HeadAsync(branch, token);
        if (head is null || !await docGit.CommitExistsAsync(commit, token)) return new(url, null);
        var ancestor = await git.RunAsync(repo.ClonePath, ["merge-base", "--is-ancestor", commit, head], 128, null, token);
        if (ancestor.ExitCode != 0) return new(url, null);
        var count = await git.RunAsync(repo.ClonePath, ["rev-list", "--count", commit + ".." + head], 64, null, token);
        return new(url, count.ExitCode == 0 && int.TryParse(count.Text.Trim(), out var n) ? n : null);
    }

    private static string? PullRequestUrl(string origin, int number)
    {
        if (origin.StartsWith("git@", StringComparison.Ordinal))
        {
            var colon = origin.IndexOf(':');
            if (colon > 4) origin = "ssh://" + origin[..colon] + "/" + origin[(colon + 1)..];
        }
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "ssh") ||
            string.IsNullOrEmpty(uri.Host)) return null;
        var path = uri.AbsolutePath.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        var segments = path.Split('/');
        if (segments.Length != 2 || segments.Any(s => s.Length == 0 || s is "." or ".." ||
            !s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))) return null;
        var route = uri.Host.ToLowerInvariant() switch
        {
            "github.com" => "pull",
            "gitlab.com" => "-/merge_requests",
            "bitbucket.org" => "pull-requests",
            _ => null
        };
        if (route is null) return null;
        var link = new UriBuilder("https", uri.Host) { Path = $"{path}/{route}/{number}" };
        if (uri.Scheme == "https") link.Port = uri.Port;
        return link.Uri.AbsoluteUri;
    }
}
