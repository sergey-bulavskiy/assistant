namespace Assistant.Domain.Vet;

public sealed class VetDiaryAction
{
    public long Id { get; set; }
    public long FamilyId { get; set; }
    public long BotDbId { get; set; }
    public long TelegramBotId { get; set; }
    public long ChatId { get; set; }
    public int? TopicId { get; set; }
    public long ActorUserId { get; set; }
    public Guid OperationKey { get; set; }
    public string Fingerprint { get; set; } = "";
    public string Kind { get; set; } = "";
    public Guid? SourceId { get; set; }
    public long? PendingDecisionId { get; set; }
    public Guid? PhotoBatchId { get; set; }
    public long? ReversesActionId { get; set; }
    public long? ReversedByActionId { get; set; }
    public string OutcomeJson { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
