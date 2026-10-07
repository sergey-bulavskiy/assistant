namespace Assistant.Domain.Vet.Photos;

public sealed class VetPhotoExtraction
{
    public Guid Id { get; set; }
    public long FamilyId { get; set; }
    public long BotDbId { get; set; }
    public long TelegramBotId { get; set; }
    public long ChatId { get; set; }
    public int? TopicId { get; set; }
    public Guid SourceId { get; set; }
    public Guid InputRevisionId { get; set; }
    public Guid AttemptId { get; set; }
    public Guid? ReusesExtractionId { get; set; }
    public string ModelName { get; set; } = "";
    public string PromptVersion { get; set; } = "photo-v1";
    public int SchemaVersion { get; set; } = 1;
    public string State { get; set; } = "returned";
    public string StructuredJson { get; set; } = "";
    public string? FailureCategory { get; set; }
    public Guid? DiagnosticAttemptId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
