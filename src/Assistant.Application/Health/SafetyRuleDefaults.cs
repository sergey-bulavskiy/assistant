using Assistant.Domain.Health;

namespace Assistant.Application.Health;

/// <summary>Published-guideline default thresholds: seeded into every new health profile and restored
/// by "/threshold &lt;key&gt; default". Glucose in mmol/L, blood pressure in mm Hg. Every value is data
/// in the family's own safety_rules rows afterwards; this list is only the seed. Order = display
/// order of /thresholds.</summary>
public static class SafetyRuleDefaults
{
    public static IReadOnlyList<SafetyRuleInfo> All { get; } = new[]
    {
        Rule("glucose.any", lowUrgent: 3.0m, lowAlert: 3.9m, highAlert: 11.0m, highUrgent: 13.9m),
        Rule("glucose.fasting", targetHigh: 5.1m),
        Rule("glucose.after_1h", targetHigh: 7.0m),
        Rule("glucose.after_2h", targetHigh: 6.7m),
        Rule("blood_pressure.systolic", highAlert: 140m, highUrgent: 160m),
        Rule("blood_pressure.diastolic", highAlert: 90m, highUrgent: 110m),
        Rule("combo.bp_symptoms", windowHours: 24),
        Rule("symptom.reduced_movement", symptomLevel: SymptomLevels.Urgent),
        Rule("symptom.vision_disturbance", symptomLevel: SymptomLevels.Alert),
        Rule("symptom.epigastric_pain", symptomLevel: SymptomLevels.Alert),
        Rule("symptom.bleeding", symptomLevel: SymptomLevels.Urgent),
        Rule("symptom.fluid_leak", symptomLevel: SymptomLevels.Urgent),
        Rule("symptom.seizure", symptomLevel: SymptomLevels.Urgent)
    };

    public static SafetyRuleInfo? Find(string ruleKey) =>
        All.FirstOrDefault(r => string.Equals(r.RuleKey, ruleKey.Trim(), StringComparison.OrdinalIgnoreCase));

    private static SafetyRuleInfo Rule(
        string ruleKey,
        decimal? lowUrgent = null,
        decimal? lowAlert = null,
        decimal? targetHigh = null,
        decimal? highAlert = null,
        decimal? highUrgent = null,
        string? symptomLevel = null,
        int? windowHours = null) =>
        new(ruleKey, lowUrgent, lowAlert, targetHigh, highAlert, highUrgent, symptomLevel, windowHours, SafetyRuleSources.GuidelineDefault);
}
