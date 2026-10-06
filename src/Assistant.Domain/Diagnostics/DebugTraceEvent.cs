namespace Assistant.Domain.Diagnostics;

public sealed class DebugTraceEvent
{
    public long Id { get; set; }
    public Guid TraceId { get; set; }
    public long FamilyId { get; set; }
    public long BotId { get; set; }
    public long ChatId { get; set; }
    public int? TopicId { get; set; }
    public long? SourceMessageId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Stage { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public string? ReasonCode { get; set; }
    public Guid? AttemptId { get; set; }
    public long? LlmCallId { get; set; }
    public long? PendingRecordId { get; set; }
    public long? RelatedSourceMessageId { get; set; }
    public long? ActorId { get; set; }
    public string DetailJson { get; set; } = "{}";
    public int PayloadBytes { get; set; }
    public int AccountedBytes { get; set; }
}
