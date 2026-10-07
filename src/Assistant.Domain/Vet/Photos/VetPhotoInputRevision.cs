namespace Assistant.Domain.Vet.Photos;

public sealed class VetPhotoInputRevision
{
    public Guid Id { get; set; }
    public long FamilyId { get; set; }
    public long BotDbId { get; set; }
    public long TelegramBotId { get; set; }
    public long ChatId { get; set; }
    public int? TopicId { get; set; }
    public Guid SourceId { get; set; }
    public int Ordinal { get; set; }
    public long UpdateId { get; set; }
    public bool IsEdit { get; set; }
    public string FileId { get; set; } = "";
    public string? FileUniqueId { get; set; }
    public string? FileName { get; set; }
    public string? ReportedMimeType { get; set; }
    public long? ReportedSize { get; set; }
    public int? ReportedWidth { get; set; }
    public int? ReportedHeight { get; set; }
    public string Caption { get; set; } = "";
    public Guid? TextInputRevisionId { get; set; }
    public Guid? ReusesImageInputId { get; set; }
    public string InputFingerprint { get; set; } = "";
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? EditedAt { get; set; }
}
