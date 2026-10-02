namespace Assistant.Application.Health;

/// <summary>The one profile of a health bot. Id is health_profiles.id.</summary>
public record HealthProfileInfo(long Id, DateOnly? StageStartDate, string TimeZone, string EmergencyPhone, string? ContextNote);

/// <summary>Health profile and safety rules. Fails closed: every method takes familyId and throws
/// InvalidOperationException unless ICurrentFamily.FamilyId equals it, so health rows are only ever
/// touched on a request scope of that same family (never from a fresh DI scope). botDbId is bots.id,
/// not the Telegram bot id.</summary>
public interface IHealthProfileStore
{
    /// <summary>Returns the bot's profile, creating it together with the default safety rules
    /// (SafetyRuleDefaults.All) in one transaction if it does not exist yet.</summary>
    Task<HealthProfileInfo> GetOrCreateAsync(long familyId, long botDbId, CancellationToken cancellationToken);

    /// <summary>Writes StageStartDate, TimeZone, EmergencyPhone and ContextNote of profile.Id.</summary>
    Task SaveProfileAsync(long familyId, HealthProfileInfo profile, long updatedByUserId, CancellationToken cancellationToken);

    /// <summary>The profile's rules in seed order; empty for another family's profile.</summary>
    Task<IReadOnlyList<SafetyRuleInfo>> GetRulesAsync(long familyId, long profileId, CancellationToken cancellationToken);

    /// <summary>Replaces the values and source of the rule with rule.RuleKey.</summary>
    Task SaveRuleAsync(long familyId, long profileId, SafetyRuleInfo rule, long updatedByUserId, CancellationToken cancellationToken);
}
