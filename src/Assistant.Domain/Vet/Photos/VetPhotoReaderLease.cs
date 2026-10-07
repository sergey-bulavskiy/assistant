namespace Assistant.Domain.Vet.Photos;

public sealed class VetPhotoReaderLease
{
    public Guid Id { get; set; }
    public long FamilyId { get; set; }
    public long BotDbId { get; set; }
    public long TelegramBotId { get; set; }
    public long ChatId { get; set; }
    public int? TopicId { get; set; }
    public Guid BlobId { get; set; }
    public Guid OriginalReferenceId { get; set; }
    public int OriginalReferenceRevision { get; set; }
    public Guid AttemptId { get; set; }
    public Guid ClaimToken { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
}
