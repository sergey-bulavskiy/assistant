using Assistant.Domain.Messages;
using Assistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Health.Documents;

internal static class HealthDocumentSourceLock
{
    public static async Task<bool> LockAsync(AssistantDbContext db, long familyId, long sourceMessageId, CancellationToken token)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Document source locking requires its database transaction.");
        var source = await db.Messages.FromSqlInterpolated($"SELECT * FROM messages WHERE id = {sourceMessageId} AND family_id = {familyId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(token);
        return source is { Direction: MessageDirection.In };
    }

    public static Task<bool> IsDocumentSourceAsync(AssistantDbContext db, long familyId, long profileId, long? sourceMessageId, CancellationToken token) =>
        sourceMessageId is null ? Task.FromResult(false)
            : MatchingAdmissions(db, familyId, profileId, sourceMessageId.Value).AnyAsync(token);

    public static Task<bool> IsDeletedAsync(AssistantDbContext db, long familyId, long profileId, long sourceMessageId, CancellationToken token) =>
        MatchingAdmissions(db, familyId, profileId, sourceMessageId).AnyAsync(a => a.DeletedAt != null, token);

    // Include exact immutable unbound sources and tombstones: a failed post-offset bind must not
    // turn the original document caption into an ordinary source outside deletion's shared lock.
    private static IQueryable<Assistant.Domain.Health.HealthDocumentAdmission> MatchingAdmissions(
        AssistantDbContext db, long familyId, long profileId, long sourceMessageId) =>
        from admission in db.HealthDocumentAdmissions
        join profile in db.HealthProfiles on admission.ProfileId equals profile.Id
        join bot in db.Bots on admission.BotDbId equals bot.Id
        join source in db.Messages on admission.TelegramBotId equals source.BotId
        where admission.FamilyId == familyId && admission.ProfileId == profileId
            && profile.FamilyId == familyId && profile.BotId == bot.Id
            && bot.FamilyId == familyId && bot.TelegramBotId == admission.TelegramBotId && bot.Role == "health"
            && source.Id == sourceMessageId && source.FamilyId == familyId
            && (admission.SourceMessageId == null || admission.SourceMessageId == source.Id)
            && source.ChatId == admission.ChatId && source.TopicId == admission.TopicId
            && source.TelegramMessageId == admission.TelegramMessageId
            && source.Direction == MessageDirection.In && source.Kind == MessageKind.Document
            && source.UserId == admission.SenderUserId && source.ChatType == admission.ChatType
            && source.SentAt == admission.SentAt
        select admission;
}
