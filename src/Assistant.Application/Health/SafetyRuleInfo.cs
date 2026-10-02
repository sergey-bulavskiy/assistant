namespace Assistant.Application.Health;

/// <summary>One safety rule as Application sees it (a safety_rules row without ids). A null value
/// field is not used by that rule. Source is SafetyRuleSources.GuidelineDefault or .Doctor.</summary>
public record SafetyRuleInfo(
    string RuleKey,
    decimal? LowUrgent,
    decimal? LowAlert,
    decimal? TargetHigh,
    decimal? HighAlert,
    decimal? HighUrgent,
    string? SymptomLevel,
    int? WindowHours,
    string Source);
