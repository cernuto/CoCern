namespace CoCern.Domain;

// Candidate internal-domain contracts, not registered tools or trusted wire input.
// Snapshot/signal values must come from existing authenticated aggregate producers.
public sealed record EventWaitRequest(string WaitId, string Kind, string Target, string Revision,
    DateTimeOffset ExpiresAt, string? PeerSessionId = null, string? ReplyTo = null,
    long? BaselineRevision = null, string? QualifyingData = null);

public sealed record EventWaitSignal(string RepositoryId, string Kind, string Target, string Revision,
    string Outcome = "satisfied", string? PeerSessionId = null, string? ReplyTo = null,
    string? QualifyingData = null);

public sealed record EventWaitOwnerWatch(string RepositoryId, string OwnerSessionId, string WaitId,
    string WatchId, string SourceSessionId, DateTimeOffset ExpiresAt);
public sealed record EventWaitLifecycle(EventWaitOwnerWatch Watch, string Outcome);
