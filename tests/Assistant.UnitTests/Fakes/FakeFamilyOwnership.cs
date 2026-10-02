using Assistant.Application.Families;

namespace Assistant.UnitTests.Fakes;

public class FakeFamilyOwnership : IFamilyOwnership
{
    public HashSet<long> OwnerUserIds { get; } = new();

    public Task<bool> IsApprovedOwnerAsync(long familyId, long telegramUserId, CancellationToken cancellationToken) =>
        Task.FromResult(OwnerUserIds.Contains(telegramUserId));
}
