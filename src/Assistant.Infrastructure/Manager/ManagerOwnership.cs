using Assistant.Domain.Families;
using Assistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Assistant.Infrastructure.Manager;

/// <summary>The one owner check behind every manager callback.</summary>
internal static class ManagerOwnership
{
    // Approval-button taps carry only a guessable sequential id (place_approve:1, member_allow:2, …),
    // so the button data itself proves nothing about who is tapping. The DM was sent to the owning
    // family's owners, so resolving it must be gated on the tapping user actually being one of them —
    // not just an owner of some other family.
    public static Task<bool> IsApprovedOwnerAsync(AssistantDbContext db, long userId, long familyId, CancellationToken cancellationToken) =>
        db.FamilyMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.TelegramUserId == userId && m.FamilyId == familyId && m.IsOwner && m.Status == FamilyMemberStatus.Approved, cancellationToken);
}
