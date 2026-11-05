using System.Text;

namespace CoCern.Adapter.Node;

/// <summary>Collects patch targets without executing the patch or accessing any filesystem path.</summary>
internal static class EditClaimPaths
{
    private const int MaxPatchCharacters = 1_048_576;
    private const int MaxTargets = 128;
    private static readonly UTF8Encoding Strict = new(false, true);

    internal static bool TryCollectPatchPaths(string patch, out IReadOnlyList<string> paths)
    {
        paths = [];
        if (patch is not { Length: > 0 and <= MaxPatchCharacters } || patch.Contains('\0')) return false;
        var lines = patch.Split('\n').Select(line => line.EndsWith('\r') ? line[..^1] : line).ToArray();
        var end = lines.Length;
        if (lines[^1].Length == 0) end--;
        if (end < 3 || lines[0] != "*** Begin Patch" || lines[end - 1] != "*** End Patch") return false;
        var targets = new HashSet<string>(StringComparer.Ordinal);
        var i = 1;
        while (i < end - 1)
        {
            var header = lines[i++];
            var add = header.StartsWith("*** Add File: ", StringComparison.Ordinal);
            var update = header.StartsWith("*** Update File: ", StringComparison.Ordinal);
            var delete = header.StartsWith("*** Delete File: ", StringComparison.Ordinal);
            if (!add && !update && !delete) return false;
            if (!Target(header[(add ? 14 : 17)..], targets)) return false;
            if (delete) continue;
            if (update && i < end - 1 && lines[i].StartsWith("*** Move to: ", StringComparison.Ordinal))
                if (!Target(lines[i++][13..], targets)) return false;
            var content = 0;
            while (i < end - 1 && !lines[i].StartsWith("*** ", StringComparison.Ordinal))
            {
                var line = lines[i++];
                if (update && (line == "@@" || line.StartsWith("@@ ", StringComparison.Ordinal))) continue;
                if (line.Length == 0 || (add ? line[0] != '+' : line[0] is not (' ' or '+' or '-'))) return false;
                content++;
            }
            if (content == 0) return false;
            if (update && i < end - 1 && lines[i] == "*** End of File") i++;
        }
        if (targets.Count == 0) return false;
        paths = targets.Order(StringComparer.Ordinal).ToArray();
        return true;
    }

    private static bool Target(string path, HashSet<string> targets)
    {
        if (path is not { Length: > 0 and <= 32767 } || string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl)
            || path.EndsWith(' ') || path.EndsWith('.')) return false;
        try { _ = Strict.GetByteCount(path); }
        catch (EncoderFallbackException) { return false; }
        // Collection is deliberately lexical. Canonicalization/containment and alias collapse happen before claims.
        targets.Add(path);
        return targets.Count <= MaxTargets;
    }
}
