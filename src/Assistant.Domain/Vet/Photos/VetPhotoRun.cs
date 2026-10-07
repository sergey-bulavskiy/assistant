namespace Assistant.Domain.Vet.Photos;

public sealed class VetPhotoRun
{
    public Guid Id { get; set; }
    public long FamilyId { get; set; }
    public long BotDbId { get; set; }
    public long TelegramBotId { get; set; }
    public long ChatId { get; set; }
    public int? TopicId { get; set; }
    public long ActorUserId { get; set; }
    public Guid OperationKey { get; set; }
    public Guid SelectionReviewId { get; set; }
    public string SelectionMode { get; set; } = "current";
    public string SelectionJson { get; set; } = "[]";
    public string ModelName { get; set; } = "";
    public string State { get; set; } = "preview";
    public int NextWindowOrdinal { get; set; }
    public int SelectedCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
}
