namespace Assistant.Domain.Vet.Photos;

public sealed class VetPhotoRunWindow
{
    public Guid Id { get; set; }
    public long FamilyId { get; set; }
    public long BotDbId { get; set; }
    public long TelegramBotId { get; set; }
    public long ChatId { get; set; }
    public int? TopicId { get; set; }
    public Guid RunId { get; set; }
    public int Ordinal { get; set; }
    public string State { get; set; } = "queued";
    public string SelectionJson { get; set; } = "[]";
    public Guid? ComparisonReviewId { get; set; }
    public long? ActionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
