using Assistant.Domain.Messages;

namespace Assistant.Application.Telegram;

public record IncomingMessage(
    long ChatId,
    string ChatType,
    string? ChatTitle,
    int? TopicId,
    int MessageId,
    long? UserId,
    string? Username,
    string? Text,
    MessageKind Kind,
    bool IsEdit,
    DateTimeOffset SentAt,
    DateTimeOffset? EditedAt,
    long? MigrateToChatId,
    string RawJson,
    int? ReplyToMessageId,
    long? ReplyToUserId);

/// <summary>A tap on an inline button. <see cref="MessageChatId"/>/<see cref="MessageId"/> identify
/// the message the button was attached to, so its buttons can later be edited/removed.
/// <see cref="MessageTopicId"/> is that message's forum topic (null outside topics) and
/// <see cref="MessageChatType"/> its chat type ("private", "group", "supergroup", …; null when
/// Telegram sent no message), so a role bot can re-check the place the button lives in.</summary>
public record CallbackQueryInfo(
    string CallbackQueryId,
    long FromUserId,
    string Data,
    long MessageChatId,
    int MessageId,
    int? MessageTopicId = null,
    string? MessageChatType = null);

/// <summary>The bot's own membership in a chat changed (my_chat_member). <see cref="IsNowMember"/>
/// is true when the bot transitioned into being able to see the chat (added, or un-kicked);
/// false when it was removed.</summary>
public record BotMembershipChange(long ChatId, string ChatTitle, bool IsNowMember);

public record IncomingUpdate(
    long UpdateId,
    IncomingMessage? Message,
    CallbackQueryInfo? CallbackQuery = null,
    BotMembershipChange? MembershipChange = null,
    long? ManagedBotCreatorUserId = null,
    long? ManagedBotUserId = null);

public record BotIdentity(long Id, string Username);
