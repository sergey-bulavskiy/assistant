using Assistant.Domain.Families;
using Assistant.Domain.Places;

namespace Assistant.Application.Families;

public interface IApprovalService
{
    Task<long> GetOrCreatePendingPlaceAsync(long botDbId, long chatId, int? topicId, string title, CancellationToken cancellationToken);

    Task<ApprovalResolution> ResolvePlaceApprovalAsync(long placeId, bool approve, CancellationToken cancellationToken);

    Task<PlaceStatus> GetPlaceStatusAsync(long placeId, CancellationToken cancellationToken);

    /// <summary>The place row's own reply_to_all flag (for a topic, never the chat-wide row's).</summary>
    Task<bool> GetPlaceReplyToAllAsync(long placeId, CancellationToken cancellationToken);

    Task<long> GetOrCreatePendingFamilyMemberAsync(
        long familyId, long telegramUserId, string displayName, string? username, string requestingBotUsername, CancellationToken cancellationToken);

    Task<ApprovalResolution> ResolveUserApprovalAsync(long familyMemberId, bool approve, CancellationToken cancellationToken);

    Task<FamilyMemberStatus> GetFamilyMemberStatusAsync(long familyMemberId, CancellationToken cancellationToken);
}
