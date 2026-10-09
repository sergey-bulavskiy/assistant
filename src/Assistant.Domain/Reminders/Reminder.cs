namespace Assistant.Domain.Reminders;

public sealed class Reminder
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
    public int SourceMessageId { get; set; }
    public string Text { get; set; } = "";
    public DateTimeOffset DueAt { get; set; }
    public int? DailyMinute { get; set; }
    public int OffsetMinutes { get; set; }
    public string Status { get; set; } = "draft";
    public bool PreviewStarted { get; set; }
    public int? PreviewMessageId { get; set; }
    public string? LastOutcome { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public int? LastTelegramMessageId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
public sealed class ReminderPreference
{
    public long FamilyId { get; set; }
    public long ActorUserId { get; set; }
    public int OffsetMinutes { get; set; }
    public int QuietStartMinute { get; set; } = 1320;
    public int QuietEndMinute { get; set; } = 480;
}
public sealed class ReminderAttempt
{
    public Guid Id { get; set; }
    public Guid ReminderId { get; set; }
    public long FamilyId { get; set; }
    public string Role { get; set; } = "";
    public DateTimeOffset OccurrenceDueAt { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public string Outcome { get; set; } = "unknown";
    public int? TelegramMessageId { get; set; }
}
public sealed class ReminderSettingsReceipt
{
    public long FamilyId { get; set; }
    public long BotId { get; set; }
    public long ChatId { get; set; }
    public int SourceMessageId { get; set; }
    public long ActorUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
