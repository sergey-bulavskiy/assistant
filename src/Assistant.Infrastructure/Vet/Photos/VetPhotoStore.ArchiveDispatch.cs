using Assistant.Application.Vet;
using Assistant.Application.Vet.Photos;
using Assistant.Domain.Families;
using Assistant.Domain.Places;
using Assistant.Domain.Vet.Photos;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Vet.Photos;

public sealed partial class VetPhotoStore : IVetPhotoArchiveDispatchStore
{
    public async Task<IReadOnlyList<VetPhotoWork>> GetArchiveDueAsync(long familyId, long botDbId, int limit, CancellationToken ct)
    {
        _guard.Family(familyId); ForgetPhotoSnapshots();
        if (limit is < 1 or > 5) throw new InvalidOperationException("Photo archive work limit is invalid.");
        var bot = await RepairDownloadsAsync(familyId, botDbId, ct);
        if (bot == null) return [];
        var now = clock.UtcNow;
        var sources = DispatchSources(familyId, botDbId, bot.TelegramBotId);
        var due = await (from source in sources
            join input in db.Set<VetPhotoInputRevision>().AsNoTracking() on source.Id equals input.SourceId
            join attempt in db.Set<VetPhotoAttempt>().AsNoTracking() on input.Id equals attempt.InputRevisionId
            where source.State != "late" && source.State != "full" && source.BatchId != null
                && input.FamilyId == familyId && input.BotDbId == botDbId && input.TelegramBotId == bot.TelegramBotId
                && input.ChatId == source.ChatId && input.TopicId == source.TopicId
                && attempt.FamilyId == familyId && attempt.BotDbId == botDbId && attempt.TelegramBotId == bot.TelegramBotId
                && attempt.SourceId == source.Id && attempt.ChatId == source.ChatId && attempt.TopicId == source.TopicId
                && attempt.Kind == "download" && attempt.DownloadAttemptCount < 2
                && (attempt.State == "queued" || attempt.State == "retry_wait" && attempt.RetryNotBefore <= now)
                && (input.ReportedSize == null || input.ReportedSize <= VetPhotoImageLimits.MaxEncodedBytes)
                && !db.Set<VetPhotoOriginalReference>().Any(r => r.FamilyId == familyId && r.BotDbId == botDbId
                    && r.TelegramBotId == bot.TelegramBotId && r.ChatId == input.ChatId && r.TopicId == input.TopicId
                    && r.InputRevisionId == input.Id)
                && db.FamilyMembers.Any(m => m.FamilyId == familyId && m.TelegramUserId == source.SourceAuthorUserId
                    && m.Status == FamilyMemberStatus.Approved)
                && (source.ChatType == "private" && source.ChatId == source.SourceAuthorUserId && source.TopicId == null
                    || (source.ChatType == "group" || source.ChatType == "supergroup") && db.Places.Any(p =>
                        p.BotId == botDbId && p.ChatId == source.ChatId && p.TopicId == source.TopicId && p.Status == PlaceStatus.Approved))
                && db.Set<VetPhotoBatch>().Any(b => b.Id == source.BatchId && b.FamilyId == familyId && b.BotDbId == botDbId
                    && b.TelegramBotId == bot.TelegramBotId && b.ChatId == source.ChatId && b.TopicId == source.TopicId)
            orderby input.ReceivedAt, input.Id
            select new VetPhotoWork(new(familyId, botDbId, bot.TelegramBotId, source.ChatId, source.TopicId),
                source.Id, input.Id, source.SourceAuthorUserId, null) { ArchiveOnly = true }).Take(limit).ToListAsync(ct);
        return due;
    }
}
