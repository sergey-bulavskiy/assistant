namespace Assistant.Application.Health;

/// <summary>A validated event ready to save. OccurredAt is UTC; PayloadJson comes from
/// HealthEventPayloads.Serialize; Type is a HealthEventTypes value; Flags are HealthEventFlags values
/// from the safety rules (null = none).</summary>
public sealed record NewHealthEvent(
    string Type, DateTimeOffset OccurredAt, string OccurredAtSource, string PayloadJson, IReadOnlyList<string>? Flags = null);

/// <summary>Where events were posted. MessageDbId is messages.id; BotId is the Telegram bot id
/// (as messages.bot_id); UserId is the sender's Telegram user id.</summary>
public sealed record HealthEventSource(long? MessageDbId, long BotId, long ChatId, int? TopicId, long? UserId);

public sealed record HealthEventInfo(long Id, string Type, DateTimeOffset OccurredAt, string PayloadJson, long? SourceMessageId);

/// <summary>A Telegram message (chat id + Telegram message id), e.g. to clear its reaction.</summary>
public sealed record MessageRef(long ChatId, int TelegramMessageId);

/// <summary>Events just soft-deleted (oldest id first) and the source messages that have no active
/// event left (their reaction should be cleared).</summary>
public sealed record DeletedEvents(IReadOnlyList<HealthEventInfo> Events, IReadOnlyList<MessageRef> MessagesWithoutEvents)
{
    public static DeletedEvents None { get; } = new(Array.Empty<HealthEventInfo>(), Array.Empty<MessageRef>());
}

/// <summary>Health events of one profile. Fails closed like IHealthProfileStore: every method takes
/// familyId and throws InvalidOperationException unless ICurrentFamily.FamilyId equals it. Deleted
/// events are never returned or deleted again. botId is always the Telegram bot id.</summary>
public interface IEventStore
{
    /// <summary>Saves the events (subject tag copied from the profile). Throws
    /// InvalidOperationException if the profile is not in this family.</summary>
    Task<IReadOnlyList<HealthEventInfo>> AddAsync(
        long familyId, long profileId, HealthEventSource source, IReadOnlyList<NewHealthEvent> events, CancellationToken cancellationToken);

    /// <summary>Active events with fromUtc ≤ OccurredAt &lt; toUtc, oldest first (then by id).</summary>
    Task<IReadOnlyList<HealthEventInfo>> GetActiveAsync(
        long familyId, long profileId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken);

    /// <summary>/undo: among this user's active events in this bot/chat/topic created at or after
    /// createdAfter, takes the newest source message (highest messages.id) and deletes all of its
    /// active events.</summary>
    Task<DeletedEvents> DeleteLatestOfUserAsync(
        long familyId, long profileId, long botId, long chatId, int? topicId, long userId, DateTimeOffset createdAfter, string reason,
        CancellationToken cancellationToken);

    /// <summary>/del as a reply: deletes the active events read from that Telegram message.</summary>
    Task<DeletedEvents> DeleteBySourceTelegramMessageAsync(
        long familyId, long profileId, long botId, long chatId, int telegramMessageId, string reason, CancellationToken cancellationToken);

    /// <summary>/del 123: deletes that event if it is active and belongs to this family and profile.</summary>
    Task<DeletedEvents> DeleteByIdAsync(long familyId, long profileId, long eventId, string reason, CancellationToken cancellationToken);
}
