namespace Assistant.Domain.Bots;

/// <summary>The role an owner asked for with /newbot, waiting for Telegram's managed_bot update
/// that reports the created bot. One row per creator; a repeat /newbot replaces it.</summary>
public class PendingBotCreation
{
    public long CreatorTelegramUserId { get; set; }
    public string Role { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
