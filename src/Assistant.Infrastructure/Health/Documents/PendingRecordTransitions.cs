using Assistant.Domain.Health;
using Assistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Health.Documents;

internal static class PendingRecordTransitions
{
    public static Task<int> ResolveAsync(
        AssistantDbContext db, long familyId, long id, string status, long? actorId, DateTimeOffset now, CancellationToken token)
    {
        if (db.Database.CurrentTransaction is null || status == PendingRecordStatuses.Pending)
            throw new InvalidOperationException("Pending resolution requires its existing transaction and a final status.");
        return db.PendingRecords.Where(p => p.Id == id && p.FamilyId == familyId && p.Status == PendingRecordStatuses.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, status)
                .SetProperty(p => p.ResolvedByUserId, actorId).SetProperty(p => p.ResolvedAt, (DateTimeOffset?)now), token);
    }
}
