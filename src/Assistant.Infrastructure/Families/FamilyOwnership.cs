using Assistant.Application.Families;
using Assistant.Infrastructure.Manager;
using Assistant.Infrastructure.Persistence;

namespace Assistant.Infrastructure.Families;

/// <summary>Role-bot owner checks reuse the manager callbacks' DB check.</summary>
public class FamilyOwnership : IFamilyOwnership
{
    private readonly AssistantDbContext _db;

    public FamilyOwnership(AssistantDbContext db)
    {
        _db = db;
    }

    public Task<bool> IsApprovedOwnerAsync(long familyId, long telegramUserId, CancellationToken cancellationToken) =>
        ManagerOwnership.IsApprovedOwnerAsync(_db, telegramUserId, familyId, cancellationToken);
}
