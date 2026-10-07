namespace Assistant.Domain.Health;

public sealed class HealthDocumentAdmission
{
    public Guid Id { get; set; }
    public long FamilyId { get; set; }
    public long ProfileId { get; set; }
    public long BotDbId { get; set; }
    public long TelegramBotId { get; set; }
    public long ChatId { get; set; }
    public int? TopicId { get; set; }
    public string ChatType { get; set; } = "";
    public int TelegramMessageId { get; set; }
    public long? SenderUserId { get; set; }
    public long FirstUpdateId { get; set; }
    public DateTimeOffset SentAt { get; set; }
    public string FileId { get; set; } = "";
    public string? FileUniqueId { get; set; }
    public string? FileName { get; set; }
    public string? MimeType { get; set; }
    public long? FileSize { get; set; }
    public string? Caption { get; set; }
    public long? SourceMessageId { get; set; }
    public string Status { get; set; } = "admitted";
    public string? FailureReason { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public bool ReactionAttempted { get; set; }
    public bool NoticeAttempted { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
