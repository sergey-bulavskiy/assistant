namespace Assistant.Application.Telegram;

public enum UpdateKind
{
    Message,
    EditedMessage,
    CallbackQuery,
    MyChatMember,
    ManagedBot
}

/// <summary>Telegram limits <c>callback_data</c> to 64 bytes (UTF-8 encoded); callers must keep
/// encoded action strings under that limit.</summary>
public record InlineButton(string Label, string CallbackData);

public interface ITelegramClient
{
    Task<BotIdentity> GetMeAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<IncomingUpdate>> GetUpdatesAsync(
        long offset, int timeoutSeconds, IReadOnlyList<UpdateKind> allowedUpdates, CancellationToken cancellationToken);

    /// <summary>Returns the sent message's Telegram message id (needed by the General assistant to
    /// store its own reply as context for the next turn). <paramref name="replyToMessageId"/>, when
    /// given, makes this message a Telegram reply to that message id (spec §8.1: "In groups the
    /// reply is sent as a reply to the triggering message").</summary>
    Task<int> SendTextAsync(long chatId, int? topicId, string text, int? replyToMessageId, CancellationToken cancellationToken);

    /// <summary>Sends a chat action (e.g. "typing"). Best-effort: Telegram clears it automatically
    /// after a few seconds, so callers waiting on a slow model repeat this on a timer rather than
    /// calling it once (spec 2.2: "repeated every 4 s"). This call itself is also best-effort: it is
    /// cosmetic, never load-bearing, so callers must catch and ignore its exceptions (a failed
    /// "typing" indicator must never fail or delay the actual reply).</summary>
    Task SendChatActionAsync(long chatId, int? topicId, string action, CancellationToken cancellationToken);

    /// <summary>Sends a message with one inline button per row and returns the sent message's id
    /// (needed later to edit its buttons once an approval is resolved).</summary>
    Task<int> SendTextWithButtonsAsync(
        long chatId, int? topicId, string text, IReadOnlyList<InlineButton> buttons, CancellationToken cancellationToken);

    Task EditMessageButtonsAsync(long chatId, int messageId, IReadOnlyList<InlineButton> buttons, CancellationToken cancellationToken);

    Task EditMessageTextAsync(long chatId, int messageId, string text, CancellationToken cancellationToken);

    Task AnswerCallbackAsync(string callbackQueryId, string? text, CancellationToken cancellationToken);

    /// <summary>Fetches the token of a bot created via Telegram Managed Bots. Must be called on the
    /// manager bot's own client (the manager is the one with `can_manage_bots`).</summary>
    Task<string> GetManagedBotTokenAsync(long managedBotUserId, CancellationToken cancellationToken);
}
