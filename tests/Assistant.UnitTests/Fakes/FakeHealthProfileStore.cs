using Assistant.Application.Health;

namespace Assistant.UnitTests.Fakes;

public class FakeHealthProfileStore : IHealthProfileStore
{
    public HealthProfileInfo Profile { get; set; } = new(1, null, "UTC", "103 или 112", null);

    public List<SafetyRuleInfo> Rules { get; set; } = new(SafetyRuleDefaults.All);

    public int GetOrCreateCalls { get; private set; }

    public long? LastFamilyId { get; private set; }

    public long? LastBotDbId { get; private set; }

    public long? LastUpdatedByUserId { get; private set; }

    public Task<HealthProfileInfo> GetOrCreateAsync(long familyId, long botDbId, CancellationToken cancellationToken)
    {
        GetOrCreateCalls++;
        LastFamilyId = familyId;
        LastBotDbId = botDbId;
        return Task.FromResult(Profile);
    }

    public Task SaveProfileAsync(long familyId, HealthProfileInfo profile, long updatedByUserId, CancellationToken cancellationToken)
    {
        Profile = profile;
        LastUpdatedByUserId = updatedByUserId;
        return Task.CompletedTask;
    }

    public bool ThrowOnGetRules { get; set; }

    public Task SaveFieldAsync(long familyId, long profileId, HealthProfileField field, string? value, long updatedByUserId, CancellationToken cancellationToken)
    {
        Profile = field switch
        {
            HealthProfileField.Conditions => Profile with { Conditions = value },
            HealthProfileField.Medications => Profile with { Medications = value },
            HealthProfileField.Allergies => Profile with { Allergies = value },
            HealthProfileField.DoctorPlan => Profile with { DoctorPlan = value },
            HealthProfileField.DoctorContacts => Profile with { DoctorContacts = value },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        LastUpdatedByUserId = updatedByUserId;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SafetyRuleInfo>> GetRulesAsync(long familyId, long profileId, CancellationToken cancellationToken)
    {
        if (ThrowOnGetRules)
        {
            throw new InvalidOperationException("simulated rules failure");
        }

        return Task.FromResult<IReadOnlyList<SafetyRuleInfo>>(new List<SafetyRuleInfo>(Rules));
    }

    public Task SaveRuleAsync(long familyId, long profileId, SafetyRuleInfo rule, long updatedByUserId, CancellationToken cancellationToken)
    {
        var index = Rules.FindIndex(r => r.RuleKey == rule.RuleKey);
        Rules[index] = rule;
        LastUpdatedByUserId = updatedByUserId;
        return Task.CompletedTask;
    }
}
