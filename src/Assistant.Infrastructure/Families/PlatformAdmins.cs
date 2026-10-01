using Assistant.Domain.Families;
using Assistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Families;

/// <summary>"Platform admin" (spec §10.3) = an approved, owner FamilyMember of the family with the
/// smallest Families.Id -- today's only family (/claim refuses a second one outright). Reads with
/// IgnoreQueryFilters on purpose: this is inherently cross-family, platform-level data.</summary>
public static class PlatformAdmins
{
    public static async Task<IReadOnlyList<FamilyMember>> GetAsync(AssistantDbContext db, CancellationToken cancellationToken)
    {
        var firstFamilyId = await db.Families.IgnoreQueryFilters()
            .OrderBy(f => f.Id).Select(f => (long?)f.Id).FirstOrDefaultAsync(cancellationToken);
        if (firstFamilyId is null)
        {
            return Array.Empty<FamilyMember>();
        }

        return await db.FamilyMembers.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.FamilyId == firstFamilyId && m.IsOwner && m.Status == FamilyMemberStatus.Approved)
            .ToListAsync(cancellationToken);
    }
}
