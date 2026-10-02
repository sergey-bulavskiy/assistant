namespace Assistant.Application.Health;

/// <summary>Field names of a safety rule as typed in /threshold and shown in /thresholds (the
/// safety_rules column names). <see cref="All"/> is the display order.</summary>
public static class SafetyRuleFields
{
    public const string LowUrgent = "low_urgent";
    public const string LowAlert = "low_alert";
    public const string TargetHigh = "target_high";
    public const string HighAlert = "high_alert";
    public const string HighUrgent = "high_urgent";
    public const string SymptomLevel = "symptom_level";
    public const string WindowHours = "window_hours";

    public static IReadOnlyList<string> All { get; } = new[] { LowUrgent, LowAlert, TargetHigh, HighAlert, HighUrgent, SymptomLevel, WindowHours };

    /// <summary>The fields this rule uses (non-null), in display order. Only these can be changed.</summary>
    public static IReadOnlyList<string> UsedBy(SafetyRuleInfo rule) =>
        All.Where(field => field switch
        {
            LowUrgent => rule.LowUrgent is not null,
            LowAlert => rule.LowAlert is not null,
            TargetHigh => rule.TargetHigh is not null,
            HighAlert => rule.HighAlert is not null,
            HighUrgent => rule.HighUrgent is not null,
            SymptomLevel => rule.SymptomLevel is not null,
            _ => rule.WindowHours is not null
        }).ToArray();
}
