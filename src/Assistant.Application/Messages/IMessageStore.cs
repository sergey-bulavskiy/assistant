using Assistant.Application.Telegram;
using Assistant.Domain.Messages;

namespace Assistant.Application.Messages;

public enum StoreOutcome
{
    Stored,
    Updated,
    Duplicate,
    OffsetOnly,
    AlreadyProcessed
}

public record StoreResult(StoreOutcome Outcome, long? MessageDbId);

/// <summary>One stored text message for context building, oldest first. Direction/Username/Text
/// mirror the stored row; commands, service messages and non-text messages are never returned here
/// (spec 2.4: "Only text messages; commands, service messages and messages without text are
/// skipped").</summary>
public record ContextMessage(MessageDirection Direction, string? Username, string Text, DateTimeOffset SentAt);

public interface IMessageStore
{
    Task EnsureBotStateAsync(BotIdentity identity, CancellationToken cancellationToken);

    Task<long> GetLastUpdateIdAsync(long botId, CancellationToken cancellationToken);

    /// <summary>Resets the bot's stored offset (`last_update_id`) to 0 when no update has advanced
    /// it since <paramref name="idleBefore"/> (or never recorded when), so the next poll accepts
    /// whatever update_id Telegram sends: after a week without updates Telegram picks the next
    /// update_id randomly, possibly below the stored one. The first update stored afterwards
    /// re-bases the offset. Returns true if the offset was reset.</summary>
    Task<bool> RebaseOffsetIfIdleAsync(long botId, DateTimeOffset idleBefore, CancellationToken cancellationToken);

    Task<StoreResult> StoreAsync(long botId, long updateId, IncomingMessage? message, CancellationToken cancellationToken);

    /// <summary>Stores the General assistant's own reply as a message row (Direction = Out,
    /// UserId = null) so it becomes context for the next turn. Not deduplicated against any Telegram
    /// update_id -- the bot sent this message itself, there is nothing to redeliver.</summary>
    Task StoreOutgoingAsync(
        long botId, long chatId, int? topicId, string chatType, int telegramMessageId, string text, CancellationToken cancellationToken);

    /// <summary>Up to <paramref name="maxMessages"/> most recent text messages (both directions) for
    /// this (bot, chat, topic), with <c>messages.id &gt; afterMessageId</c> if given (spec §8.3:
    /// `/new` is `chat_settings.context_start_message_id`, a `messages.id`, not a timestamp) and
    /// <c>messages.id &lt; beforeMessageId</c> if given -- the caller passes the incoming message's
    /// own `messages.id` here so it is never duplicated into its own context, since it is stored
    /// before context is built. Commands (text starting with `/`) are excluded regardless of
    /// direction (spec §2.4). Oldest first.</summary>
    Task<IReadOnlyList<ContextMessage>> GetRecentContextAsync(
        long botId, long chatId, int? topicId, long? afterMessageId, long? beforeMessageId, int maxMessages, CancellationToken cancellationToken);
}
