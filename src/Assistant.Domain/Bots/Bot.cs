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

    /// <summary>When <see cref="LastUpdateId"/> was last advanced by a processed update (null: never,
    /// or before this column existed). Drives the idle re-base of the polling offset.</summary>
    public DateTimeOffset? LastUpdateAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
