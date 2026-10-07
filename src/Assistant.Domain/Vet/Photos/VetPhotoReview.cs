namespace Assistant.Domain.Vet.Photos;

public sealed class VetPhotoReview
{
    public Guid Id { get; set; }
    public long FamilyId { get; set; }
    public long BotDbId { get; set; }
    public long TelegramBotId { get; set; }
    public long ChatId { get; set; }
    public int? TopicId { get; set; }
    public Guid? BatchId { get; set; }
    public long? ProfileId { get; set; }
    public int? ProfileRevision { get; set; }
    public Guid? RunWindowId { get; set; }
    public Guid OperationKey { get; set; }
    public string Kind { get; set; } = "";
    public string State { get; set; } = "preview";
    public int Revision { get; set; } = 1;
    public int? BatchReviewRevision { get; set; }
    public long RequesterUserId { get; set; }
    public long? DecisionActorUserId { get; set; }
    public string SelectionJson { get; set; } = "[]";
    public string Fingerprint { get; set; } = "";
    public string PreviewPagesJson { get; set; } = "[]";
    public string DeliveredPagesJson { get; set; } = "[]";
    public int PageCount { get; set; }
    public bool CompletePreviewDelivered { get; set; }
    public int? AcceptancePromptMessageId { get; set; }
    public long? ActionId { get; set; }
    public string? OutcomeJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}
