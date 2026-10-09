namespace Assistant.Domain.Expectations;

public sealed class Expectation
{
    public Guid Id { get; set; }
    public long FamilyId { get; set; }
    public long BotDbId { get; set; }
    public long BotId { get; set; }
    public string Role { get; set; } = "";
    public long ChatId { get; set; }
    public int? TopicId { get; set; }
    public string ChatType { get; set; } = "";
    public long ActorUserId { get; set; }
    public long ProfileId { get; set; }
    public string EventType { get; set; } = "";
    public string Status { get; set; } = "draft";
    public long Revision { get; set; } = 1;
    public int OffsetMinutes { get; set; }
    public int CurrentVersion { get; set; }
    public int? NextVersion { get; set; }
    public int LastVersion { get; set; }
    public DateOnly? FirstDate { get; set; }
    public DateOnly? NextDate { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public DateTimeOffset? DraftExpiresAt { get; set; }
    public DateOnly? LastDate { get; set; }
    public string? LastOutcome { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public Guid? LastAttemptId { get; set; }
    public int? LastTelegramMessageId { get; set; }
    public DateOnly? SkippedFrom { get; set; }
    public DateOnly? SkippedThrough { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ExpectationVersion
{
    public long FamilyId { get; set; }
    public Guid ExpectationId { get; set; }
    public int Number { get; set; }
    public int DeadlineMinute { get; set; }
    public int GraceMinutes { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ExpectationDraft
{
    public Guid Id { get; set; }
    public long FamilyId { get; set; }
    public Guid ExpectationId { get; set; }
    public string Kind { get; set; } = "create";
    public long ExpectedRevision { get; set; }
    public int ExpectedCurrentVersion { get; set; }
    public int DeadlineMinute { get; set; }
    public int GraceMinutes { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public string Status { get; set; } = "pending";
    public bool PreviewStarted { get; set; }
    public int? PreviewMessageId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ExpectationOccurrence
{
    public long FamilyId { get; set; }
    public Guid ExpectationId { get; set; }
    public DateOnly LocalDate { get; set; }
    public int Version { get; set; }
    public DateTimeOffset WindowStart { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string Outcome { get; set; } = "pending";
    public long? MatchedEventId { get; set; }
    public string? MatchedEventKind { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ExpectationAttempt
{
    public Guid Id { get; set; }
    public long FamilyId { get; set; }
    public string Role { get; set; } = "";
    public Guid ExpectationId { get; set; }
    public DateOnly LocalDate { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public string Outcome { get; set; } = "unknown";
    public int? TelegramMessageId { get; set; }
}

public sealed class ExpectationReceipt
{
    public long FamilyId { get; set; }
    public long BotId { get; set; }
    public long ChatId { get; set; }
    public int SourceMessageId { get; set; }
    public int? TopicId { get; set; }
    public long ActorUserId { get; set; }
    public Guid? ExpectationId { get; set; }
    public Guid? DraftId { get; set; }
    public string Result { get; set; } = "unavailable";
    public DateTimeOffset CreatedAt { get; set; }
}
