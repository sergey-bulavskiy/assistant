namespace Assistant.Domain.Vet.Photos;

public sealed class VetPhotoAttempt
{
    public Guid Id { get; set; }
    public long FamilyId { get; set; }
    public long BotDbId { get; set; }
    public long TelegramBotId { get; set; }
    public long ChatId { get; set; }
    public int? TopicId { get; set; }
    public Guid SourceId { get; set; }
    public Guid InputRevisionId { get; set; }
    public Guid? RunWindowId { get; set; }
    public Guid? ExtractionResultId { get; set; }
    public long ActorUserId { get; set; }
    public string Kind { get; set; } = "download";
    public string State { get; set; } = "queued";
    public Guid? ClaimToken { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public long ReservedBytes { get; set; }
    public bool ReservedInputSlot { get; set; }
    public bool ReservedResultSlot { get; set; }
    public int DownloadAttemptCount { get; set; }
    public int ExpectedSourceOrdinal { get; set; }
    public Guid ExpectedCurrentInputId { get; set; }
    public bool HistoricalSelection { get; set; }
    public string? FailureCategory { get; set; }
    public DateTimeOffset? RetryNotBefore { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
