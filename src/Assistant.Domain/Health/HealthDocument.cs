namespace Assistant.Domain.Health;

public sealed class HealthDocument
{
    public long Id { get; set; }
    public long FamilyId { get; set; }
    public long ProfileId { get; set; }
    public Guid AdmissionId { get; set; }
    public long SourceMessageId { get; set; }
    public string TelegramFileId { get; set; } = "";
    public string? FileName { get; set; }
    public string? MimeType { get; set; }
    public long? SizeBytes { get; set; }
    public string? Caption { get; set; }
    public string? Text { get; set; }
    public long? PostedByUserId { get; set; }
    public DateTimeOffset PostedAt { get; set; }
    public string TextStatus { get; set; } = "processing";
    public string? TextFailureReason { get; set; }
    public bool TextTruncated { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
