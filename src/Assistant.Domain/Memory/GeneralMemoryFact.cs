namespace Assistant.Domain.Memory;

public sealed class GeneralMemoryFact
{
    public long Id { get; set; }
    public long FamilyId { get; set; }
    public long BotId { get; set; }
    public long ChatId { get; set; }
    public int? TopicId { get; set; }
    public long SourceMessageId { get; set; }
    public long ActorUserId { get; set; }
    public string Text { get; set; } = "";
    public string Tag { get; set; } = "family";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RetiredAt { get; set; }
    public long? RetiredByUserId { get; set; }
}
