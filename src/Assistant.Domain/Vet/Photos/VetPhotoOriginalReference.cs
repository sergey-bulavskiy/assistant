namespace Assistant.Domain.Vet.Photos;

public sealed class VetPhotoOriginalReference
{
    public Guid Id { get; set; }
    public long FamilyId { get; set; }
    public long BotDbId { get; set; }
    public long TelegramBotId { get; set; }
    public long ChatId { get; set; }
    public int? TopicId { get; set; }
    public Guid InputRevisionId { get; set; }
    public Guid BlobId { get; set; }
    public string ContentHash { get; set; } = "";
    public int Revision { get; set; } = 1;
    public string State { get; set; } = "retained";
    public long ActualBytes { get; set; }
    public string Format { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTimeOffset RetainedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public long? DeletedByUserId { get; set; }
    public Guid? DeletionReviewId { get; set; }
}
