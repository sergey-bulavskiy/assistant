using Assistant.Domain.Messages;

namespace Assistant.Application.Telegram;

public record IncomingMessage(
    long ChatId,
    string ChatType,
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
    string RawJson);

public record IncomingUpdate(long UpdateId, IncomingMessage? Message);

public record BotIdentity(long Id, string Username);
