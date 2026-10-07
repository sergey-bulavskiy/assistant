namespace Assistant.Domain.Vet.Photos;

public sealed class VetPhotoSource
{
    public Guid Id { get; set; }
    public long FamilyId { get; set; }
    public long BotDbId { get; set; }
    public long TelegramBotId { get; set; }
    public long ChatId { get; set; }
    public int? TopicId { get; set; }
    public Guid? BatchId { get; set; }
    public Guid? ProposedBatchId { get; set; }
    public int TelegramMessageId { get; set; }
    public int SourceSlot { get; set; } = 1;
    public string ChatType { get; set; } = "";
    public long SourceAuthorUserId { get; set; }
    public long? SourceMessageDbId { get; set; }
    public string? MediaGroupId { get; set; }
    public int? ReplyToMessageId { get; set; }
    public int? ItemNumber { get; set; }
    public string Association { get; set; } = "single";
    public string State { get; set; } = "admitted";
    public Guid CurrentInputRevisionId { get; set; }
    public int CurrentOrdinal { get; set; }
    public DateTimeOffset SentAt { get; set; }
    public DateTimeOffset AdmittedAt { get; set; }
}
