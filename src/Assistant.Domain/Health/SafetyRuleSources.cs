namespace Assistant.Domain.Health;

/// <summary>Where a safety rule's values come from (safety_rules.source).</summary>
public static class SafetyRuleSources
{
    /// <summary>Published-guideline default, seeded for every new profile; shown as "не подтверждено врачом".</summary>
    public const string GuidelineDefault = "guideline_default";

    /// <summary>Entered by an owner with /threshold; shown as "врач".</summary>
    public const string Doctor = "doctor";
}
