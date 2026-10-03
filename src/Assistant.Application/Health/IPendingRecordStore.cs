namespace Assistant.Application.Health;

/// <summary>Values of one message waiting for Да/Нет. Events are validated (HealthEventValidator)
/// and carry no flags (the rules run again when they are saved). BotId is the Telegram bot id,
/// SourceMessageId messages.id, TelegramMessageId the original message's Telegram id,
/// RequestedByUserId its sender. AlertedRuleKeys: rules whose fixed alert was already sent.</summary>
public sealed record NewPendingRecord(
    long? SourceMessageId,
    long BotId,
    long ChatId,
    int? TopicId,
    int TelegramMessageId,
    long? RequestedByUserId,
    IReadOnlyList<NewHealthEvent> Events,
    IReadOnlyList<string> AlertedRuleKeys);

/// <summary>A pending_records row. Status is a PendingRecordStatuses value; PromptMessageId is the
/// bot's button message (null until sent).</summary>
public sealed record PendingRecordInfo(
    long Id,
    long ProfileId,
    long? SourceMessageId,
    long BotId,
    long ChatId,
    int? TopicId,
    int TelegramMessageId,
    int? PromptMessageId,
    long? RequestedByUserId,
    IReadOnlyList<NewHealthEvent> Events,
    IReadOnlyList<string> AlertedRuleKeys,
    string Status,
    DateTimeOffset CreatedAt);

/// <summary>pending_records. Fails closed like the other health stores: every method takes familyId
/// and throws InvalidOperationException unless ICurrentFamily.FamilyId equals it. Rows are never
/// deleted; a row leaves "pending" exactly once (TryResolveAsync).</summary>
public interface IPendingRecordStore
{
    /// <summary>Adds a pending row and returns its id. Throws InvalidOperationException if the
    /// profile is not in this family.</summary>
    Task<long> AddAsync(long familyId, long profileId, NewPendingRecord record, CancellationToken cancellationToken);

    /// <summary>Remembers the bot's button message of a row.</summary>
    Task SetPromptMessageAsync(long familyId, long id, int promptMessageId, CancellationToken cancellationToken);

    /// <summary>The row with this id in this family (any status), or null.</summary>
    Task<PendingRecordInfo?> FindAsync(long familyId, long id, CancellationToken cancellationToken);

    /// <summary>Still pending rows of this bot and chat whose original message or button message is
    /// this Telegram message, oldest first.</summary>
    Task<IReadOnlyList<PendingRecordInfo>> FindPendingByTelegramMessageAsync(
        long familyId, long botId, long chatId, int telegramMessageId, CancellationToken cancellationToken);

    /// <summary>The newest still pending row asked for this user's message in this bot/chat/topic and
    /// created at or after createdAfter, or null.</summary>
    Task<PendingRecordInfo?> FindLatestPendingOfUserAsync(
        long familyId, long botId, long chatId, int? topicId, long userId, DateTimeOffset createdAfter, CancellationToken cancellationToken);

    /// <summary>Moves a row from pending to status (accepted, declined or expired) only if it is still
    /// pending: one conditional UPDATE in a database transaction. When inSameTransaction is given it
    /// runs after the UPDATE in the same transaction (e.g. saving the events), so if it throws the row
    /// stays pending and nothing is saved. True only when this call changed the row.</summary>
    Task<bool> TryResolveAsync(
        long familyId, long id, string status, long? resolvedByUserId, Func<CancellationToken, Task>? inSameTransaction,
        CancellationToken cancellationToken);
}
