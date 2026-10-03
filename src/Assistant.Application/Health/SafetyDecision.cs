namespace Assistant.Application.Health;

/// <summary>What a safety decision is about; picks the fixed text template.</summary>
public enum SafetyAlertKind
{
    Glucose,
    Systolic,
    Diastolic,
    Symptom,
    Combo
}

/// <summary>One alert the rules decided on (never model output). Level is a SafetyAlertLevels value;
/// ThresholdSource is SafetyRuleSources.Doctor or .GuidelineDefault. Threshold, Value and IsLow are
/// set for Glucose/Systolic/Diastolic (IsLow: the value is below a low threshold); Systolic,
/// Diastolic and SymptomCode for Combo; SymptomCode for Symptom.</summary>
public sealed record SafetyDecision(
    string RuleKey,
    string Level,
    SafetyAlertKind Kind,
    string ThresholdSource,
    decimal? Threshold = null,
    decimal? Value = null,
    bool IsLow = false,
    int? Systolic = null,
    int? Diastolic = null,
    string? SymptomCode = null);

/// <summary>The rules' result for one new event: the flags to save with it and the alert to send
/// (null: none, or the reading is too old to alert).</summary>
public sealed record SafetyEvaluation(IReadOnlyList<string> Flags, SafetyDecision? Alert);
