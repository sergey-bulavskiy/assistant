using Assistant.Application.Common;
using Assistant.Application.Families;
using Assistant.Application.Health;
using Assistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Health;

/// <summary>safety_alerts. Fails closed like the other health stores. The claim is one raw statement
/// on the request's own context: nothing is tracked and a conflict is not an exception, so the shared
/// context is never poisoned and no fresh DI scope is needed (where ICurrentFamily would be unset).
/// The column list is written by hand: keep it in sync with SafetyAlertConfiguration.</summary>
public class SafetyAlertStore : ISafetyAlertStore
{
    private readonly AssistantDbContext _db;
    private readonly ICurrentFamily _currentFamily;
    private readonly IClock _clock;

    public SafetyAlertStore(AssistantDbContext db, ICurrentFamily currentFamily, IClock clock)
    {
        _db = db;
        _currentFamily = currentFamily;
        _clock = clock;
    }

    public async Task<bool> TryClaimAsync(long familyId, NewSafetyAlert alert, CancellationToken cancellationToken)
    {
        EnsureFamilyScope(familyId);

        var now = _clock.UtcNow;
        // The raw SQL bypasses the query filters, so the SELECT itself inserts nothing for an event of
        // another family or a deleted one. Nullable parameters are cast so Postgres knows their type.
        var affected = await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO safety_alerts (family_id, event_id, rule_key, level, threshold, threshold_source, chat_id, topic_id, created_at)
            SELECT {familyId}, e.id, {alert.RuleKey}, {alert.Level}, CAST({alert.Threshold} AS numeric), {alert.ThresholdSource},
                   {alert.ChatId}, CAST({alert.TopicId} AS integer), {now}
            FROM events e
            WHERE e.id = {alert.EventId} AND e.family_id = {familyId} AND e.deleted_at IS NULL
            ON CONFLICT (event_id, rule_key) DO NOTHING
            """,
            cancellationToken);
        return affected == 1;
    }

    // A null or different FamilyId both mean "not this family's request scope".
    private void EnsureFamilyScope(long familyId)
    {
        if (_currentFamily.FamilyId != familyId)
        {
            throw new InvalidOperationException("Health data is only accessed on a request scope of the same family.");
        }
    }
}
