namespace Assistant.Domain.Health;

/// <summary>One threshold rule of a health profile (unique per profile and key). A null value field
/// is not used by that rule. Family-scoped; deleted together with its profile.</summary>
public class SafetyRule
{
    public long Id { get; set; }
    public long FamilyId { get; set; }
    public long ProfileId { get; set; }
    public string RuleKey { get; set; } = string.Empty;
    public decimal? LowUrgent { get; set; }
    public decimal? LowAlert { get; set; }
    public decimal? TargetHigh { get; set; }
    public decimal? HighAlert { get; set; }
    public decimal? HighUrgent { get; set; }

    /// <summary>Symptom rules only: <see cref="SymptomLevels.Alert"/> or <see cref="SymptomLevels.Urgent"/>.</summary>
    public string? SymptomLevel { get; set; }

    /// <summary>Combination rules only.</summary>
    public int? WindowHours { get; set; }

    /// <summary><see cref="SafetyRuleSources.GuidelineDefault"/> or <see cref="SafetyRuleSources.Doctor"/>.</summary>
    public string Source { get; set; } = SafetyRuleSources.GuidelineDefault;

    public long? UpdatedByUserId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
