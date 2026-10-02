namespace Assistant.Application.Families;

/// <summary>Whether a Telegram user is an approved owner of a family. Always re-read from the DB:
/// approval and ownership can change at any time.</summary>
public interface IFamilyOwnership
{
    Task<bool> IsApprovedOwnerAsync(long familyId, long telegramUserId, CancellationToken cancellationToken);
}
