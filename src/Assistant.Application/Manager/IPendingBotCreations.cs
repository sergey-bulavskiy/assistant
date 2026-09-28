namespace Assistant.Application.Manager;

/// <summary>Correlates a /newbot command to the managed_bot update that follows it (Telegram's
/// managed_bot update carries the creator and the new bot, but not which role the owner typed).
/// In-memory only, one pending role per creator, overwritten by a repeat /newbot.</summary>
public interface IPendingBotCreations
{
    void SetPendingRole(long creatorTelegramUserId, string role);

    /// <summary>Removes and returns the pending role for this creator, if any.</summary>
    string? TakeRole(long creatorTelegramUserId);
}
