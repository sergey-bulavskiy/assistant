namespace Assistant.Application.Families;

public interface IApprovalService
{
    Task<long> GetOrCreatePendingPlaceAsync(long botDbId, long chatId, int? topicId, string title, CancellationToken cancellationToken);

    Task<ApprovalResolution> ResolvePlaceApprovalAsync(long placeId, bool approve, CancellationToken cancellationToken);

    Task<long> GetOrCreatePendingFamilyMemberAsync(
        long familyId, long telegramUserId, string displayName, string? username, string requestingBotUsername, CancellationToken cancellationToken);

    Task<ApprovalResolution> ResolveUserApprovalAsync(long familyMemberId, bool approve, CancellationToken cancellationToken);
}
