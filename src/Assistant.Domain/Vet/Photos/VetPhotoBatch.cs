namespace Assistant.Domain.Vet.Photos;

public sealed class VetPhotoBatch
{
    public Guid Id { get; set; }
    public long FamilyId { get; set; }
    public long BotDbId { get; set; }
    public long TelegramBotId { get; set; }
    public long ChatId { get; set; }
    public int? TopicId { get; set; }
    public long ProfileId { get; set; }
    public int ProfileRevision { get; set; }
    public long StarterUserId { get; set; }
    public string State { get; set; } = "collecting";
    public string IntakeKind { get; set; } = "collection";
    public int ReviewRevision { get; set; } = 1;
    public int NextItemNumber { get; set; } = 1;
    public int? ProgressMessageId { get; set; }
    public string AssumptionsJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset IntakeOpenedAt { get; set; }
    public DateTimeOffset? IntakeClosedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
