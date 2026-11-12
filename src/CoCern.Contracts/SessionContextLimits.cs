namespace CoCern.Contracts;

/// <summary>Bounds for deliberately published, repository-shared session knowledge.</summary>
public static class SessionContextLimits
{
    public const int IdLength = 64;
    public const int TitleLength = 160;
    public const int Entries = 64;
    public const int EntryBytes = 8192;
    public const int StateBytes = 1024 * 1024;
    public const int PatchBytes = 32 * 1024;
    public const int Changes = 8;
    public const int References = 8;
    public const int SummaryTokens = 400;
    public const int NextStepTokens = 200;
    public const int ManifestTokens = 1200;
    public const int ReplyTokens = 1500;
    public const int ReplyBytes = 8192;
    public const int PartTokens = 512;
    public const int SnippetTokens = 160;
    public const int SearchHits = 5;
    public const int DiscoveryRows = 10;
    public const string BudgetExceeded = "ContextBudgetExceeded";
    public const string VersionUnavailable = "ContextVersionUnavailable";
    public const string ListRestartRequired = "ContextListRestartRequired";
    public const string SearchUnavailable = "ContextSearchUnavailable";

    public static bool ValidId(string? id) => id is { Length: > 0 and <= IdLength }
        && id[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
        && id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');
}
