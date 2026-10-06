namespace Assistant.Domain.Diagnostics;

public sealed class DebugTrace
{
    public Guid Id { get; set; }
    public long FamilyId { get; set; }
    public long BotId { get; set; }
    public long ChatId { get; set; }
    public int? TopicId { get; set; }
    public long? UpdateId { get; set; }
    public long? SourceMessageId { get; set; }
    public bool IsEdit { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string BuildIdentity { get; set; } = string.Empty;
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset LastEventAt { get; set; }
    public long AccountedBytes { get; set; } = 1024;
    public long PayloadBytes { get; set; }
    public int EventCount { get; set; }
    public int OmittedEventCount { get; set; }
    public bool Truncated { get; set; }
    public bool Redacted { get; set; }
    public string? FinalOutcome { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? LastDisposition { get; set; }
}
