namespace Assistant.Application.Health;

/// <summary>Rule keys the safety rules read (safety_rules.rule_key, as seeded by SafetyRuleDefaults).
/// A symptom rule's key is SymptomPrefix + the symptom code.</summary>
public static class SafetyRuleKeys
{
    public const string GlucoseAny = "glucose.any";
    public const string GlucoseFasting = "glucose.fasting";
    public const string GlucoseAfter1h = "glucose.after_1h";
    public const string GlucoseAfter2h = "glucose.after_2h";
    public const string BloodPressureSystolic = "blood_pressure.systolic";
    public const string BloodPressureDiastolic = "blood_pressure.diastolic";
    public const string ComboBpSymptoms = "combo.bp_symptoms";
    public const string SymptomPrefix = "symptom.";
}
