namespace CoCern.Contracts;

/// <summary>A bounded retained-context read, sampled on demand rather than carried in dashboard snapshots.</summary>
public sealed record DashboardRetainedContextReply<T> where T : class
{
    public bool Ok { get; init; }
    public string Code { get; init; } = "";
    public DateTimeOffset SampledAt { get; init; }
    public string RepositoryId { get; init; } = "";
    public long Version { get; init; }
    public T? Data { get; init; }
}

public sealed record DashboardRetainedContextList
{
    public DashboardRetainedContextSummary[] Items { get; init; } = [];
    public long CatalogRevision { get; init; }
    public string? NextCursor { get; init; }
    public bool Truncated { get; init; }
}

public sealed record DashboardRetainedContextRepositories
{
    public string[] Items { get; init; } = [];
    public bool Truncated { get; init; }
}

public sealed record DashboardRetainedContextSummary
{
    public string ContextId { get; init; } = "";
    public long Revision { get; init; }
    public string Title { get; init; } = "";
    public string? Summary { get; init; }
    public string[] TaskIds { get; init; } = [];
    public DateTimeOffset? UpdatedAt { get; init; }
    public string Uri { get; init; } = "";
}

public sealed record DashboardRetainedContextManifest
{
    public string ContextId { get; init; } = "";
    public long Revision { get; init; }
    public string Generation { get; init; } = "";
    public string Title { get; init; } = "";
    public string Summary { get; init; } = "";
    public string[] NextSteps { get; init; } = [];
    public string[] TaskIds { get; init; } = [];
    public DashboardRetainedContextPinnedEntry[] PinnedEntries { get; init; } = [];
    public int EntryCount { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public string Uri { get; init; } = "";
    public string Untrusted { get; init; } = "";
}

public sealed record DashboardRetainedContextPinnedEntry
{
    public string EntryId { get; init; } = "";
    public long Version { get; init; }
    public string Title { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Uri { get; init; } = "";
}

public sealed record DashboardRetainedContextSearch
{
    public string ContextId { get; init; } = "";
    public long Revision { get; init; }
    public string Encoding { get; init; } = "";
    public DashboardRetainedContextHit[] Hits { get; init; } = [];
    public bool Truncated { get; init; }
}

public sealed record DashboardRetainedContextHit
{
    public string EntryId { get; init; } = "";
    public long Version { get; init; }
    public string Kind { get; init; } = "";
    public string Title { get; init; } = "";
    public string Snippet { get; init; } = "";
    public string[] SourceRefs { get; init; } = [];
    public string Generation { get; init; } = "";
    public string Uri { get; init; } = "";
}

/// <summary>Exact text and provenance of one immutable address; the next part is another explicit read.</summary>
public sealed record DashboardRetainedContextPart
{
    public string ContextId { get; init; } = "";
    public long Revision { get; init; }
    public string Generation { get; init; } = "";
    public string EntryId { get; init; } = "";
    public long Version { get; init; }
    public string Kind { get; init; } = "";
    public string Title { get; init; } = "";
    public string Text { get; init; } = "";
    public string[] SourceRefs { get; init; } = [];
    public string Author { get; init; } = "";
    public string SourceSessionId { get; init; } = "";
    public DateTimeOffset? UpdatedAt { get; init; }
    public int Part { get; init; }
    public int PartCount { get; init; }
    public string Encoding { get; init; } = "";
    public string Uri { get; init; } = "";
    public string? NextUri { get; init; }
}
