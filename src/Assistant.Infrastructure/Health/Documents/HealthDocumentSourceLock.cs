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
        sourceMessageId is null ? Task.FromResult(false) : db.HealthDocumentAdmissions.AnyAsync(
            a => a.FamilyId == familyId && a.ProfileId == profileId && a.SourceMessageId == sourceMessageId, token);

    public static Task<bool> IsDeletedAsync(AssistantDbContext db, long familyId, long profileId, long sourceMessageId, CancellationToken token) =>
        db.HealthDocumentAdmissions.AnyAsync(a => a.FamilyId == familyId && a.ProfileId == profileId
            && a.SourceMessageId == sourceMessageId && a.DeletedAt != null, token);
}
