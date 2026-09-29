namespace Assistant.Domain.Bots;

public class Bot
{
    public long Id { get; set; }
    public long? FamilyId { get; set; }
    public long TelegramBotId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public byte[]? TokenEncrypted { get; set; }
    public BotStatus Status { get; set; }
    public long LastUpdateId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
