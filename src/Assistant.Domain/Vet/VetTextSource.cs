namespace Assistant.Domain.Vet;

public sealed class VetTextSource
{
    public Guid Id { get; set; }
    public long FamilyId { get; set; }
    public long BotDbId { get; set; }
    public long TelegramBotId { get; set; }
    public long ChatId { get; set; }
    public int? TopicId { get; set; }
    public string ChatType { get; set; } = "";
    public int TelegramMessageId { get; set; }
    public int SourceSlot { get; set; }
    public long SourceAuthorUserId { get; set; }
    public DateTimeOffset SentAt { get; set; }
    public long? SourceMessageDbId { get; set; }
    public Guid CurrentInputRevisionId { get; set; }
    public int CurrentOrdinal { get; set; }
    public int? ReplyToMessageId { get; set; }
    public long? ReplyToUserId { get; set; }
}
