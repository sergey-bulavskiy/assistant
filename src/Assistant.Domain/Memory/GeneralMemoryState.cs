namespace Assistant.Domain.Memory;

public sealed class GeneralMemoryState
{
    public long Id { get; set; }
    public long FamilyId { get; set; }
    public long BotId { get; set; }
    public long ChatId { get; set; }
    public int? TopicId { get; set; }
    public long ResetCutoff { get; set; }
    public long ThroughMessageId { get; set; }
    public long SourceVersion { get; set; }
    public string SummaryText { get; set; } = "";
    public string SourceFingerprint { get; set; } = "";
    public string? ModelName { get; set; }
    public DateTimeOffset? GeneratedAt { get; set; }
}
