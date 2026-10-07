using Assistant.Application.Families;
using Assistant.Application.Vet;
using Assistant.Domain.Families;
using Assistant.Domain.Places;
using Assistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Vet;

internal sealed class VetStoreGuard(AssistantDbContext db, ICurrentFamily current)
{
    public void Family(long familyId)
    {
        if (current.FamilyId != familyId)
            throw new InvalidOperationException("Vet scope is not active.");
    }

    public async Task BotAsync(long familyId, long botDbId, long? telegramId, CancellationToken ct)
    {
        Family(familyId);
        if (!await db.Bots.AnyAsync(b => b.Id == botDbId && b.FamilyId == familyId
            && (telegramId == null || b.TelegramBotId == telegramId) && b.Role.Trim().ToLower() == "vet", ct))
            throw new InvalidOperationException("Vet bot scope is invalid.");
    }

    public async Task LockAsync(long familyId, long botDbId, CancellationToken ct)
    {
        // All diary/profile/source transactions for one bot serialize. The key is the globally
        // unique bots.id; a namespace prefix keeps this separate from other advisory lock users.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended('vet:' || CAST({botDbId} AS text), 0))", ct);
        await BotAsync(familyId, botDbId, null, ct);
    }

    public async Task<bool> ActorAsync(VetDiaryScope scope, long actor, CancellationToken ct) =>
        actor > 0 && await db.FamilyMembers.AnyAsync(m => m.FamilyId == scope.FamilyId
            && m.TelegramUserId == actor && m.Status == FamilyMemberStatus.Approved, ct)
        && (await db.Set<Assistant.Domain.Vet.VetTextSource>().AnyAsync(s => s.FamilyId == scope.FamilyId
                && s.BotDbId == scope.BotDbId && s.ChatId == scope.ChatId && s.TopicId == scope.TopicId
                && s.ChatType == "private" && s.SourceAuthorUserId == actor, ct)
            || await db.Places.AnyAsync(p => p.BotId == scope.BotDbId && p.ChatId == scope.ChatId
                && p.TopicId == scope.TopicId && p.Status == PlaceStatus.Approved, ct));
}
