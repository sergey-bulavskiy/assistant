using Assistant.Application.Common;
using Assistant.Application.Families;
using Assistant.Application.Health;
using Assistant.Domain.Health;
using Assistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Health;

/// <summary>health_profiles and safety_rules. Fails closed for health data: every method first checks
/// that the request's ICurrentFamily is the same family, on top of the family query filter and an
/// explicit FamilyId predicate. Never IgnoreQueryFilters() here, and never use this store from a
/// fresh DI scope: ICurrentFamily is unset there, so every call throws.</summary>
public class HealthProfileStore : IHealthProfileStore
{
    private readonly AssistantDbContext _db;
    private readonly ICurrentFamily _currentFamily;
    private readonly IClock _clock;

    public HealthProfileStore(AssistantDbContext db, ICurrentFamily currentFamily, IClock clock)
    {
        _db = db;
        _currentFamily = currentFamily;
        _clock = clock;
    }

    // Read-then-insert: two concurrent first messages of one bot would race on the unique bot_id
    // index, but a bot's updates are handled one at a time by its poller, so this does not happen.
    public async Task<HealthProfileInfo> GetOrCreateAsync(long familyId, long botDbId, CancellationToken cancellationToken)
    {
        EnsureFamilyScope(familyId);

        var existing = await _db.HealthProfiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.FamilyId == familyId && p.BotId == botDbId, cancellationToken);
        if (existing is not null)
        {
            return ToInfo(existing);
        }

        var now = _clock.UtcNow;
        var profile = new HealthProfile { FamilyId = familyId, BotId = botDbId, CreatedAt = now, UpdatedAt = now };

        // One transaction: a profile never exists without its default rules.
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        _db.HealthProfiles.Add(profile);
        await _db.SaveChangesAsync(cancellationToken);

        foreach (var rule in SafetyRuleDefaults.All)
        {
            _db.SafetyRules.Add(new SafetyRule
            {
                FamilyId = familyId,
                ProfileId = profile.Id,
                RuleKey = rule.RuleKey,
                LowUrgent = rule.LowUrgent,
                LowAlert = rule.LowAlert,
                TargetHigh = rule.TargetHigh,
                HighAlert = rule.HighAlert,
                HighUrgent = rule.HighUrgent,
                SymptomLevel = rule.SymptomLevel,
                WindowHours = rule.WindowHours,
                Source = rule.Source,
                UpdatedAt = now
            });
        }

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToInfo(profile);
    }

    public async Task SaveProfileAsync(long familyId, HealthProfileInfo profile, long updatedByUserId, CancellationToken cancellationToken)
    {
        EnsureFamilyScope(familyId);

        var row = await _db.HealthProfiles.FirstOrDefaultAsync(p => p.Id == profile.Id && p.FamilyId == familyId, cancellationToken)
            ?? throw new InvalidOperationException("Health profile not found in this family.");
        row.StageStartDate = profile.StageStartDate;
        row.TimeZone = profile.TimeZone;
        row.EmergencyPhone = profile.EmergencyPhone;
        row.ContextNote = profile.ContextNote;
        row.UpdatedAt = _clock.UtcNow;
        row.UpdatedByUserId = updatedByUserId;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SafetyRuleInfo>> GetRulesAsync(long familyId, long profileId, CancellationToken cancellationToken)
    {
        EnsureFamilyScope(familyId);

        return await _db.SafetyRules.AsNoTracking()
            .Where(r => r.FamilyId == familyId && r.ProfileId == profileId)
            .OrderBy(r => r.Id)
            .Select(r => new SafetyRuleInfo(
                r.RuleKey, r.LowUrgent, r.LowAlert, r.TargetHigh, r.HighAlert, r.HighUrgent, r.SymptomLevel, r.WindowHours, r.Source))
            .ToListAsync(cancellationToken);
    }

    public async Task SaveRuleAsync(long familyId, long profileId, SafetyRuleInfo rule, long updatedByUserId, CancellationToken cancellationToken)
    {
        EnsureFamilyScope(familyId);

        var row = await _db.SafetyRules
            .FirstOrDefaultAsync(r => r.FamilyId == familyId && r.ProfileId == profileId && r.RuleKey == rule.RuleKey, cancellationToken)
            ?? throw new InvalidOperationException("Safety rule not found in this family.");
        row.LowUrgent = rule.LowUrgent;
        row.LowAlert = rule.LowAlert;
        row.TargetHigh = rule.TargetHigh;
        row.HighAlert = rule.HighAlert;
        row.HighUrgent = rule.HighUrgent;
        row.SymptomLevel = rule.SymptomLevel;
        row.WindowHours = rule.WindowHours;
        row.Source = rule.Source;
        row.UpdatedAt = _clock.UtcNow;
        row.UpdatedByUserId = updatedByUserId;
        await _db.SaveChangesAsync(cancellationToken);
    }

    // A null or different FamilyId both mean "not this family's request scope".
    private void EnsureFamilyScope(long familyId)
    {
        if (_currentFamily.FamilyId != familyId)
        {
            throw new InvalidOperationException("Health data is only accessed on a request scope of the same family.");
        }
    }

    private static HealthProfileInfo ToInfo(HealthProfile profile) =>
        new(profile.Id, profile.StageStartDate, profile.TimeZone, profile.EmergencyPhone, profile.ContextNote);
}
