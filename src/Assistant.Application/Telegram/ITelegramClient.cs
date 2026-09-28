namespace Assistant.Application.Telegram;

public enum UpdateKind
{
    Message,
    EditedMessage,
    CallbackQuery,
    MyChatMember,
    ManagedBot
}

public record InlineButton(string Label, string CallbackData);

public interface ITelegramClient
{
    Task<BotIdentity> GetMeAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<IncomingUpdate>> GetUpdatesAsync(
        long offset, int timeoutSeconds, IReadOnlyList<UpdateKind> allowedUpdates, CancellationToken cancellationToken);

    Task SendTextAsync(long chatId, int? topicId, string text, CancellationToken cancellationToken);

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
