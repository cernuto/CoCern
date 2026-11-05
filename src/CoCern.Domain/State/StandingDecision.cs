namespace CoCern.Domain.State;

/// <summary>Optional exact human-answer evidence on active Standing and its bounded history. Not a permission token.</summary>
public static class StandingDecision
{
    public const string Key = "decision";
    public const string AuditRefKey = "auditRef";
    public const string PrincipalIdKey = "principalId";
    public const string SessionIdKey = "sessionId";
    public const string AtKey = "at";
    public const string HumanWordsKey = "humanWords";
    public const string OutcomeKey = "outcome";
    public const string ProposalIdKey = "proposalId";
    public const string EffectKey = "effect";

    public static StateShape Shape { get; } = StateShape.Object((AuditRefKey, null), (PrincipalIdKey, null), (SessionIdKey, null), (AtKey, null),
        (HumanWordsKey, null), (OutcomeKey, null), (ProposalIdKey, null), (EffectKey, null));
}
