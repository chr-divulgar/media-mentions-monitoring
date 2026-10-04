namespace MediaOpsCore.Modules.Alerting.Domain;

// N1 informativa, N2 seguimiento, N3 riesgo, N4 crisis (PLAN_MAESTRO RQ-034, ANX-81). Ordered by
// severity so the highest level among matched keywords is simply the maximum.
public enum AlertLevel
{
    Informative = 1,
    FollowUp = 2,
    Risk = 3,
    Crisis = 4,
}

public static class AlertLevelExtensions
{
    public static string ToLabel(this AlertLevel level) => level switch
    {
        AlertLevel.Informative => "N1",
        AlertLevel.FollowUp => "N2",
        AlertLevel.Risk => "N3",
        AlertLevel.Crisis => "N4",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
    };

    // Anything unreadable — no value, a typo, a level added later — is the lowest level rather than
    // an error: a keyword config that predates levels must keep alerting exactly as before.
    public static AlertLevel Parse(string? label) => label?.Trim().ToUpperInvariant() switch
    {
        "N2" => AlertLevel.FollowUp,
        "N3" => AlertLevel.Risk,
        "N4" => AlertLevel.Crisis,
        _ => AlertLevel.Informative,
    };
}
