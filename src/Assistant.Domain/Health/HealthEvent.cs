namespace Assistant.Domain.Health;

/// <summary>One recorded health diary event (reading or note; events table). Family-scoped; soft-deleted only.
/// Payload is a jsonb document (one Application payload record per Type). BotId is the Telegram bot
/// id (as messages.bot_id); SourceMessageId is messages.id of the message it was read from.</summary>
public class HealthEvent
{
    public long Id { get; set; }
    public long FamilyId { get; set; }
    public long ProfileId { get; set; }

    /// <summary>One of <see cref="HealthEventTypes"/>.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Copied from the profile when the event is saved.</summary>
    public string SubjectTag { get; set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; set; }

    /// <summary><see cref="OccurredAtSources.Stated"/> or <see cref="OccurredAtSources.Message"/>.</summary>
    public string OccurredAtSource { get; set; } = string.Empty;

    public string Payload { get; set; } = "{}";

    /// <summary><see cref="HealthEventFlags"/> values set by the safety rules when the event is saved.</summary>
    public string[] Flags { get; set; } = Array.Empty<string>();

    public long? SourceMessageId { get; set; }
    public long BotId { get; set; }
    public long ChatId { get; set; }
    public int? TopicId { get; set; }
    public long? RecordedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary><see cref="EventDeleteReasons"/> value when deleted.</summary>
    public string? DeleteReason { get; set; }
}
