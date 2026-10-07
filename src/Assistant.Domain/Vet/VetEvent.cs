namespace Assistant.Domain.Vet;

public sealed class VetEvent
{
    public long Id { get; set; }
    public long FamilyId { get; set; }
    public long BotDbId { get; set; }
    public long TelegramBotId { get; set; }
    public long ChatId { get; set; }
    public int? TopicId { get; set; }
    public long ProfileId { get; set; }
    public string EventType { get; set; } = "";
    public decimal Value { get; set; }
    public string Unit { get; set; } = "";
    public string? Product { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string LocalTime { get; set; } = "";
    public string TimeZoneSnapshot { get; set; } = "";
    public string OccurredAtSource { get; set; } = "";
    public string ValueUnitSource { get; set; } = "";
    public string SourceKind { get; set; } = "text";
    public Guid SourceId { get; set; }
    public int CandidateOrdinal { get; set; }
    public Guid? TextSourceId { get; set; }
    public Guid? PhotoSourceId { get; set; }
    public Guid? PhotoBatchId { get; set; }
    public Guid InputRevisionId { get; set; }
    public Guid ExtractionResultId { get; set; }
    public long SourceAuthorUserId { get; set; }
    public long SourceMessageDbId { get; set; }
    public int TelegramMessageId { get; set; }
    public int Revision { get; set; } = 1;
    public DateTimeOffset? DeletedAt { get; set; }
    public string? DeleteReason { get; set; }
    public long? DeletedByUserId { get; set; }
    public string LastMutationKind { get; set; } = "save";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
