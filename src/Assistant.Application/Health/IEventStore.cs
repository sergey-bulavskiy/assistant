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
/// Health event or document left (their reaction should be cleared).</summary>
public sealed record DeletedEvents(IReadOnlyList<HealthEventInfo> Events, IReadOnlyList<MessageRef> MessagesWithoutEvents)
{
    public static DeletedEvents None { get; } = new(Array.Empty<HealthEventInfo>(), Array.Empty<MessageRef>());
}

/// <summary>Result of reading an edited message again. Events: one entry per new event, in input
/// order; an event equal to one of the message's earlier active events (same type, time and payload)
/// keeps that row and id, the others are new rows. KeptCount: how many earlier events were kept.
/// Deleted: the message's earlier events no longer in its text, soft-deleted with reason edit
/// (oldest id first).</summary>
public sealed record ReplacedEvents(IReadOnlyList<HealthEventInfo> Events, int KeptCount, IReadOnlyList<HealthEventInfo> Deleted)
{
    /// <summary>True when the message had active events before the edit.</summary>
    public bool HadEvents => KeptCount > 0 || Deleted.Count > 0;
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

    /// <summary>Newest active notes, optionally filtered by exact normalized tag in the last 90 days.</summary>
    Task<IReadOnlyList<HealthEventInfo>> GetNotesAsync(
        long familyId, long profileId, string? normalizedTag, DateTimeOffset nowUtc, int limit,
        CancellationToken cancellationToken);

    /// <summary>The source message (messages.id) that /undo would delete now: the highest one among
    /// this user's active events in this bot/chat/topic created at or after createdAfter; null when
    /// there is none. Reads only.</summary>
    Task<long?> FindLatestSourceMessageOfUserAsync(
        long familyId, long profileId, long botId, long chatId, int? topicId, long userId, DateTimeOffset createdAfter,
        CancellationToken cancellationToken);

    /// <summary>/undo: among this user's active events in this bot/chat/topic created at or after
    /// createdAfter, takes the newest source message (highest messages.id) and deletes all of its
    /// active events.</summary>
    Task<DeletedEvents> DeleteLatestOfUserAsync(
        long familyId, long profileId, long botId, long chatId, int? topicId, long userId, DateTimeOffset createdAfter, string reason,
        CancellationToken cancellationToken);

    /// <summary>/del as a reply: deletes the active events read from that Telegram message. createdAfter,
    /// when given, limits this to events created at or after it (the free-text undo's /undo window);
    /// /del itself passes null, with no age limit.</summary>
    Task<DeletedEvents> DeleteBySourceTelegramMessageAsync(
        long familyId, long profileId, long botId, long chatId, int telegramMessageId, string reason, CancellationToken cancellationToken,
        DateTimeOffset? createdAfter = null);

    /// <summary>/del 123: deletes that event if it is active and belongs to this family and profile.</summary>
    Task<DeletedEvents> DeleteByIdAsync(long familyId, long profileId, long eventId, string reason, CancellationToken cancellationToken);

    /// <summary>Edited message: makes <paramref name="events"/> the active events of the source message
    /// (source.MessageDbId, required) with one save. The message's earlier active events (same bot
    /// and chat) that are unchanged are kept, the others are soft-deleted with reason edit, and the
    /// remaining new events are added. A failure changes nothing. Throws InvalidOperationException if
    /// the profile is not in this family, ArgumentException without a source message.</summary>
    Task<ReplacedEvents> ReplaceMessageEventsAsync(
        long familyId, long profileId, HealthEventSource source, IReadOnlyList<NewHealthEvent> events, CancellationToken cancellationToken);
}
