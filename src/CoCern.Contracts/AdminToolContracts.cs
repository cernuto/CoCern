using CoCern.Contracts.Dashboard;

namespace CoCern.Contracts;

/// <summary>Whether the hub certificate's automatic backup can restore it. A listener that is off has no certificate to back up.</summary>
public static class CertificateBackupStates
{
    public const string Off = "off";
    public const string Missing = "missing";
    public const string Matches = "matches";
    /// <summary>The backup is another certificate, or cannot be read as one; the hub would refuse to start from it.</summary>
    public const string Mismatch = "mismatch";
}

/// <summary>
/// <c>hub_diagnose</c>'s reply. It holds no token, join code, private key or file path, and no session label: labels are untrusted text
/// that a diagnosis does not need, and the certificate's fingerprint is what every PC already pins.
/// </summary>
public sealed record HubDiagnosis
{
    public string Version { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public long UptimeMinutes { get; init; }
    public string[] Addresses { get; init; } = [];
    public string? CertificateFingerprint { get; init; }
    public string CertificateBackup { get; init; } = CertificateBackupStates.Off;
    public bool DiscoveryEnabled { get; init; }
    public bool NetworkEnabled { get; init; }
    public long? AnalyticsSnapshotAgeMinutes { get; init; }
    /// <summary>Why there is no snapshot age; null when there is one.</summary>
    public string? AnalyticsUnavailable { get; init; }
    /// <summary>Open sessions on the hub, of every repository; null when <see cref="SessionsUnavailable"/> says why not.</summary>
    public int? SessionCount { get; init; }
    public int SessionLimit { get; init; } = RelayLimits.MaxRecords;
    public string? SessionsUnavailable { get; init; }
    /// <summary>The read stopped at its time budget, so <see cref="SessionCount"/> may be low.</summary>
    public bool SessionsPartial { get; init; }
    /// <summary>The least recently seen first, so a trimmed list keeps the PCs most likely to need attention.</summary>
    public HubDiagnosisPc[] Pcs { get; init; } = [];
    public bool Truncated { get; init; }
    public int MorePcs { get; init; }
    /// <summary>A PC's adapter version is not known to the hub yet, which a reader could otherwise take as every PC being current.</summary>
    public string VersionNote { get; init; } = "PCs do not report an adapter version to this hub yet, so this does not say whether a PC's version is current.";
}

/// <summary>One enrolled PC in <see cref="HubDiagnosis"/>. Its name was announced by the PC and is untrusted text.</summary>
public sealed record HubDiagnosisPc
{
    public string MachineId { get; init; } = "";
    public string Name { get; init; } = "";
    public DateTimeOffset? LastSeenAt { get; init; }
    public bool SeenRecently { get; init; }
    public int SessionCount { get; init; }
}

/// <summary><c>add_pc</c>'s reply: the dashboard's Add another PC status as it is, under a note when it holds the join code.</summary>
public sealed record AddPcReply
{
    public const string SecretNotice = "This holds the hub's join code, a secret: don't forward or store it.";
    public string? SecretWarning { get; init; }
    public DashboardAddPcStatus Status { get; init; } = new();
    /// <summary>Some addresses were left out so that each command in the reply stays whole.</summary>
    public bool Truncated { get; init; }
    public int MoreAddresses { get; init; }
}
