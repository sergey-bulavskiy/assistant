namespace Assistant.Domain.Vet;

public sealed class VetPendingDecision
{
    public long Id { get; set; }
    public long FamilyId { get; set; }
    public long BotDbId { get; set; }
    public long TelegramBotId { get; set; }
    public long ChatId { get; set; }
    public int? TopicId { get; set; }
    public Guid SourceId { get; set; }
    public Guid InputRevisionId { get; set; }
    public Guid ExtractionResultId { get; set; }
    public long RequesterUserId { get; set; }
    public int ReviewRevision { get; set; } = 1;
    public Guid OperationKey { get; set; }
    public string ProposalJson { get; set; } = "";
    public string State { get; set; } = "pending";
    public int? PromptMessageId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public long? ResolvedByUserId { get; set; }
}
