using CoCern.Adapter.Node;
using CoCern.Contracts;

namespace CoCern.Adapter.Setup;

/// <summary>Starts the installed hub's own enrolled node without changing any app connection.</summary>
internal static class LocalNodeSetup
{
    internal static async Task<int> RunAsync(string hubData, string folder, SetupEnvironment environment, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(hubData) || !Path.IsPathFullyQualified(folder))
            throw new AdapterConfigurationException("Local node setup needs absolute hub-data and node folders.");
        hubData = Path.GetFullPath(hubData); folder = Path.GetFullPath(folder);
        foreach (var path in new[] { hubData, folder })
            for (DirectoryInfo? directory = new(path); directory is not null; directory = directory.Parent)
                if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new AdapterConfigurationException("Local node setup cannot follow linked installation folders.");
        var recordPath = Path.Combine(hubData, LocalHubRecord.FileName);
        if (!File.Exists(recordPath) || new FileInfo(recordPath).Length > 4096 ||
            LocalHubRecord.Read(hubData) is not { } record || !AdapterOptions.TryParseOrigin(record.Url, out var hub) ||
            !hub.IsLoopback || hub.Scheme != Uri.UriSchemeHttps || !CertificateFingerprint.IsValid(record.Fingerprint))
            throw new AdapterConfigurationException("The installed hub has no valid pinned loopback connection. Start its hub before setting up the local node.");
        var fingerprint = CertificateFingerprint.Normalize(record.Fingerprint)!;
        string? joinCode = null;
        if (NodeState.HasIdentityFiles(folder))
        {
            if (NodeState.NativeCredentialIdentity(folder) is null || NodeState.ReadEnrollment(folder) is not { } enrolled ||
                !string.Equals(enrolled.Hub.GetLeftPart(UriPartial.Authority), hub.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(enrolled.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                throw new AdapterConfigurationException("The local node has incomplete or different enrollment. Its identity and credentials were retained; inspect this installation rather than replacing them.");
        }
        else
        {
            var codePath = Path.Combine(hubData, "join-code");
            if (!NodeState.IsPrivateFile(codePath) || new FileInfo(codePath).Length is < 8 or > 1024)
                throw new AdapterConfigurationException("The installed hub's private enrollment code is unavailable. The node was not enrolled.");
            joinCode = File.ReadAllText(codePath).Trim();
            if (joinCode.Length < 8 || joinCode.Any(char.IsControl))
                throw new AdapterConfigurationException("The installed hub's private enrollment code is invalid. The node was not enrolled.");
        }
        var startup = environment.RunKey.Get(LogonStart.ValueName);
        if (startup is not null && !RepairCommand.OwnsStartup(startup, folder, environment.AdapterDll, environment.DotnetPath))
            throw new AdapterConfigurationException("A different CoCern node starts at sign-in. Its registration was retained; use the existing installation instead of installing a second node.");
        // The code remains in memory for this enrollment only. App MCP entries, roles and native bindings stay under Connect's ownership.
        return await SetupCommand.RunAsync(new(hub, SkipClientConfig: true, folder, JoinCode: joinCode, Fingerprint: fingerprint), environment, ct, rememberJoinCode: false);
    }
}
