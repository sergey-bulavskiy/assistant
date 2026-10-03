namespace Assistant.Domain.Health;

/// <summary>A safety alert sent (or attempted) for one event and rule; unique per (EventId, RuleKey)
/// so a redelivered or repeated check never alerts twice. Family-scoped. Inserted only by the raw
/// claim statement of SafetyAlertStore.</summary>
public class SafetyAlert
{
    public long Id { get; set; }
    public long FamilyId { get; set; }

    /// <summary>events.id.</summary>
    public long EventId { get; set; }

    public string RuleKey { get; set; } = string.Empty;

    /// <summary><see cref="SafetyAlertLevels"/> value.</summary>
    public string Level { get; set; } = string.Empty;

    /// <summary>The threshold crossed; null for symptom and combination alerts.</summary>
    public decimal? Threshold { get; set; }

    /// <summary><see cref="SafetyRuleSources"/> value.</summary>
    public string ThresholdSource { get; set; } = string.Empty;

    /// <summary>Where the alert went (where the reading was posted).</summary>
    public long ChatId { get; set; }

    public int? TopicId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
