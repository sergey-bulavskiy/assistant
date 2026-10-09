using System.Globalization;
using Assistant.Domain.Bots;
using Assistant.Domain.Families;
using Assistant.Domain.Places;
using Assistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Expectations;

internal static class AuthorityMutation
{
    // Manager paths already authorize the actor against this explicit family. They do not have
    // a CurrentFamily scope; these updates therefore use explicit family-scoped SQL throughout.
    // Capture revocation before SaveChanges changes entity states, and never hold this transaction
    // across a coordinator stop/start or Telegram notification.
    public static async Task SaveAsync(AssistantDbContext db, long familyId, DateTimeOffset now, CancellationToken ct)
    {
        if (familyId <= 0 || db.Database.CurrentTransaction != null)
            throw new InvalidOperationException("Authority mutation scope unavailable.");
        var revoked = new List<(long? Actor, long? Bot, long? Chat, int? Topic)>();
        foreach (var entry in db.ChangeTracker.Entries<FamilyMember>())
        {
            if (entry.State is not (EntityState.Modified or EntityState.Deleted)) continue;
            if (entry.Entity.FamilyId != familyId) throw new InvalidOperationException("Authority mutation scope unavailable.");
            if (entry.State == EntityState.Deleted || entry.Entity.Status != FamilyMemberStatus.Approved)
                revoked.Add((entry.Entity.TelegramUserId, null, null, null));
        }
        foreach (var entry in db.ChangeTracker.Entries<Place>())
        {
            if (entry.State is not (EntityState.Modified or EntityState.Deleted)) continue;
            if (!await db.Bots.IgnoreQueryFilters().AnyAsync(x => x.Id == entry.Entity.BotId && x.FamilyId == familyId, ct))
                throw new InvalidOperationException("Authority mutation scope unavailable.");
            if (entry.State == EntityState.Deleted || entry.Entity.Status != PlaceStatus.Approved)
                revoked.Add((null, entry.Entity.BotId, entry.Entity.ChatId, entry.Entity.TopicId));
        }
        foreach (var entry in db.ChangeTracker.Entries<Bot>())
        {
            if (entry.State is not (EntityState.Modified or EntityState.Deleted)) continue;
            if (entry.Entity.FamilyId != familyId) throw new InvalidOperationException("Authority mutation scope unavailable.");
            if (entry.State == EntityState.Deleted) revoked.Add((null, entry.Entity.Id, null, null));
        }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var key = familyId.ToString(CultureInfo.InvariantCulture);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(61008, hashtext({key}))", ct);
        await db.SaveChangesAsync(ct);
        foreach (var s in revoked.Distinct())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE expectation_drafts AS d SET status='retired', updated_at={now}
                FROM expectations AS e WHERE d.expectation_id=e.id AND e.family_id={familyId}
                AND d.status='pending' AND e.status IN ('draft','active','paused')
                AND (({s.Actor}::bigint IS NOT NULL AND e.actor_user_id={s.Actor})
                  OR ({s.Bot}::bigint IS NOT NULL AND e.bot_db_id={s.Bot}
                    AND ({s.Chat}::bigint IS NULL OR (e.chat_id={s.Chat} AND e.topic_id IS NOT DISTINCT FROM {s.Topic}))))
                """, ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE expectation_occurrences AS o SET outcome='suppressed-authorization', updated_at={now}
                FROM expectations AS e WHERE o.expectation_id=e.id AND e.family_id={familyId}
                AND o.outcome='pending' AND e.status IN ('draft','active','paused')
                AND (({s.Actor}::bigint IS NOT NULL AND e.actor_user_id={s.Actor})
                  OR ({s.Bot}::bigint IS NOT NULL AND e.bot_db_id={s.Bot}
                    AND ({s.Chat}::bigint IS NULL OR (e.chat_id={s.Chat} AND e.topic_id IS NOT DISTINCT FROM {s.Topic}))))
                """, ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE expectations AS e SET status='retired', revision=revision+1, next_date=NULL,
                    due_at=NULL, last_outcome='suppressed-authorization', updated_at={now}
                WHERE e.family_id={familyId} AND e.status IN ('draft','active','paused')
                AND (({s.Actor}::bigint IS NOT NULL AND e.actor_user_id={s.Actor})
                  OR ({s.Bot}::bigint IS NOT NULL AND e.bot_db_id={s.Bot}
                    AND ({s.Chat}::bigint IS NULL OR (e.chat_id={s.Chat} AND e.topic_id IS NOT DISTINCT FROM {s.Topic}))))
                """, ct);
        }
        await tx.CommitAsync(ct);
    }
}
