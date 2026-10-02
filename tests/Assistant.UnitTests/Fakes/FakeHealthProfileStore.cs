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

    public Task<IReadOnlyList<SafetyRuleInfo>> GetRulesAsync(long familyId, long profileId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SafetyRuleInfo>>(new List<SafetyRuleInfo>(Rules));

    public Task SaveRuleAsync(long familyId, long profileId, SafetyRuleInfo rule, long updatedByUserId, CancellationToken cancellationToken)
    {
        var index = Rules.FindIndex(r => r.RuleKey == rule.RuleKey);
        Rules[index] = rule;
        LastUpdatedByUserId = updatedByUserId;
        return Task.CompletedTask;
    }
}
