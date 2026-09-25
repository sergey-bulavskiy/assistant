namespace Assistant.Domain.Messages;

public class StoredMessage
{
    public long Id { get; set; }
    public long BotId { get; set; }
    public long ChatId { get; set; }
    public int? TopicId { get; set; }
    public int TelegramMessageId { get; set; }
    public long? UserId { get; set; }
    public string? Username { get; set; }
    public string ChatType { get; set; } = string.Empty;
    public MessageKind Kind { get; set; }
    public string? Text { get; set; }
    public DateTimeOffset SentAt { get; set; }
    public DateTimeOffset? EditedAt { get; set; }
    public string Raw { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
